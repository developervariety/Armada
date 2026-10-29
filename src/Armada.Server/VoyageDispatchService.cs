namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;

    /// <summary>
    /// Shared voyage dispatch orchestration used by MCP armada_dispatch, REST voyage creation and WebSocket create_voyage.
    /// </summary>
    public sealed class VoyageDispatchService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly LoggingModule? _Logging;
        private readonly ICodeIndexService? _CodeIndexService;
        private readonly ObjectiveService? _ObjectiveService;
        private readonly IObjectiveDispatchPreviewService? _ObjectiveDispatchPreview;
        private readonly ArmadaSettings? _Settings;
        private readonly TypedDispatchStalenessAdapter? _DispatchStalenessAdapter;

        private const string _CodeContextModeAuto = "auto";
        private const string _CodeContextModeOff = "off";
        #endregion

        #region Private-Types

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral orchestration service.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="codeIndexService">Optional code-index service.</param>
        /// <param name="objectiveService">Optional objective service.</param>
        /// <param name="settings">Optional Armada settings.</param>
        /// <param name="objectiveDispatchPreview">Optional shared objective dispatch preview.</param>
        public VoyageDispatchService(
            DatabaseDriver database,
            IAdmiralService admiral,
            LoggingModule? logging = null,
            ICodeIndexService? codeIndexService = null,
            ObjectiveService? objectiveService = null,
            ArmadaSettings? settings = null,
            IObjectiveDispatchPreviewService? objectiveDispatchPreview = null,
            TypedDispatchStalenessAdapter? dispatchStalenessAdapter = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Logging = logging;
            _CodeIndexService = codeIndexService;
            _ObjectiveService = objectiveService;
            _Settings = settings;
            _ObjectiveDispatchPreview = objectiveDispatchPreview;
            _DispatchStalenessAdapter = dispatchStalenessAdapter;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Dispatch a voyage using the shared REST and MCP orchestration path.
        /// </summary>
        /// <param name="request">Dispatch request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result.</returns>
        /// <summary>
        /// Run every cheap, deterministic precondition a dispatch must satisfy and return the failing
        /// result, or null when the request is dispatchable. Callers that hand dispatch to a background
        /// job call this FIRST so a bad request still fails fast and specifically, instead of being
        /// accepted as a job the caller must poll only to discover the vesselId was a typo.
        /// <see cref="DispatchAsync"/> also calls it, so validation has exactly one implementation.
        /// </summary>
        /// <param name="request">Dispatch request to validate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The failing result, or null when all preconditions pass.</returns>
        public async Task<VoyageDispatchResult?> ValidatePreconditionsAsync(SharedVoyageDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            VoyageDispatchResult? validation = ValidateRequest(request.Title, request.Missions);
            if (validation != null) return validation;

            // The hold refuses at submission, before anything is accepted. The MCP dispatch runs these
            // preconditions synchronously and only then accepts a background job, so a job is never
            // accepted into a queue that the restart the hold announces would erase.
            VoyageDispatchResult? held = HoldRefusalResult();
            if (held != null) return held;

            try
            {
                PipelineStageSkip.ValidateNamesOrThrow(PipelineStageSkip.FromOperator(request.SkipStages, request.SkipStagesReason, request.ObjectiveAuthContext));
            }
            catch (StageSkipRefusedException refused)
            {
                return StageSkipRefusedResult(refused);
            }

            // The vessel and dependency missions a request names are read in the caller's scope, exactly as a
            // path id is: the admiral reads them again by id alone and gives the voyage the vessel's tenant, so
            // an unchecked id dispatches into another tenant's vessel and captains. A captain named by a captain
            // assignment is read in the same scope below.
            string vesselId = request.VesselId;
            Vessel? dispatchVessel = request.ObjectiveAuthContext != null
                ? await CallerScopedRead.ReadVesselAsync(_Database, request.ObjectiveAuthContext, vesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (dispatchVessel == null) return VoyageDispatchResult.NotFound(new
            {
                Error = "Vessel not found: " + vesselId,
                Code = "vessel_not_found",
                Reason = "Vessel " + vesselId + " does not exist in this admiral.",
                Action = "Register the vessel via armada_add_vessel or verify the vesselId.",
                VesselId = vesselId
            });

            VoyageDispatchResult? referenceValidation = await ValidateCallerReferencesAsync(request, token).ConfigureAwait(false);
            if (referenceValidation != null) return referenceValidation;

            VoyageDispatchResult? assignmentValidation = await ValidateCaptainAssignmentsAsync(request, dispatchVessel, token).ConfigureAwait(false);
            if (assignmentValidation != null) return assignmentValidation;

            VoyageDispatchResult? objectiveValidation = await ValidateObjectiveAsync(
                NormalizeEmpty(request.ObjectiveId), request.ObjectiveAuthContext, vesselId).ConfigureAwait(false);
            if (objectiveValidation != null) return objectiveValidation;
            foreach (string linkedObjectiveId in CollectAdmissionObjectiveIds(null, request.LinkedObjectiveIds))
            {
                VoyageDispatchResult? linkedValidation = await ValidateObjectiveAsync(
                    linkedObjectiveId, request.ObjectiveAuthContext, vesselId).ConfigureAwait(false);
                if (linkedValidation != null) return linkedValidation;
            }

            string? objectiveId = NormalizeEmpty(request.ObjectiveId);
            if (objectiveId != null && _ObjectiveDispatchPreview != null && _ObjectiveService != null)
            {
                AuthContext objectiveAuth = RequireObjectiveCaller(request.ObjectiveAuthContext, objectiveId);
                Objective? objective = await _ObjectiveService.ReadAsync(objectiveAuth, objectiveId, token).ConfigureAwait(false);
                if (objective != null)
                {
                    // A request that names its own skipStages, an empty list included, replaces the
                    // objective's stored default skip, so the stored skip neither applies nor gates it.
                    if (request.SkipStages != null)
                    {
                        objective.Preparation ??= new ObjectivePreparation();
                        objective.Preparation.StageSkip = PipelineStageSkip.FromOperator(request.SkipStages, request.SkipStagesReason, objectiveAuth);
                    }
                    string? effectivePipeline = NormalizeEmpty(request.PipelineId)
                        ?? NormalizeEmpty(request.Pipeline)
                        ?? NormalizeEmpty(objective.SuggestedPipelineId);
                    ObjectiveDispatchPreview preview = await _ObjectiveDispatchPreview.PreviewAsync(
                        objectiveAuth,
                        objective,
                        vesselId,
                        effectivePipeline,
                        request.CaptainAssignments,
                        request.Missions,
                        token).ConfigureAwait(false);
                    VoyageDispatchResult? gate = PreflightGateResult(preview, request.ForcePreflight, objectiveId);
                    if (gate != null)
                    {
                        ObjectiveDispatchPreviewIssue? stop = preview.Issues.FirstOrDefault(item => item.Code == ObjectiveReviewRepeatGuard.IssueCode);
                        if (stop != null)
                        {
                            await ReviewRepeatStopNotice.PostAsync(_Database, _Logging ?? new LoggingModule(),
                                objective, stop, request.DispatcherParticipantKey, token).ConfigureAwait(false);
                        }
                        return gate;
                    }
                    request.ResolvedSourceCommit = preview.MissionStartRefs.Count == 1
                        ? preview.MissionStartRefs[0].ResolvedCommit : preview.ResolvedStartCommit;
                    request.PreparationAnchorMoves = preview.PreparationAnchorMoves ?? new List<ObjectivePreparationAnchorMove>();
                    request.PreflightModelFlaggedQuestions = request.ForcePreflight
                        ? ObjectivePreflightGate.ModelFlaggedQuestions(preview)
                        : new List<int>();
                }
            }

            if (IsCodeIndexEnabled() && ShouldEvaluateCodeIndexPrecondition(request))
            {
                object? blockedByIndex = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    _CodeIndexService,
                    vesselId,
                    "armada_dispatch",
                    _Settings?.CodeIndex,
                    _Logging,
                    LogCodeContextWarning,
                    token,
                    _DispatchStalenessAdapter,
                    request.Title,
                    request.Description,
                    DispatchSubjectObjectiveId(request)).ConfigureAwait(false);
                if (blockedByIndex != null) return VoyageDispatchResult.BadRequest(blockedByIndex);
            }
            else
            {
                LogCodeContextInfo(
                    "code index dispatch precondition skipped for vessel " + vesselId
                    + (IsCodeIndexEnabled()
                        ? " because every effective mission mode is off"
                        : " because code indexing is disabled"));
            }

            string? resolvedPipelineId = await ResolvePipelineIdAsync(request.PipelineId, request.Pipeline).ConfigureAwait(false);
            if (String.Equals(resolvedPipelineId, "__pipeline_not_found__", StringComparison.Ordinal))
            {
                return VoyageDispatchResult.BadRequest(new
                {
                    Error = "Pipeline not found: " + request.Pipeline,
                    Code = "pipeline_not_found",
                    Reason = "Pipeline named \"" + request.Pipeline + "\" does not exist in this admiral.",
                    Action = "Verify the pipeline name via armada_enumerate(entityType=\"pipelines\").",
                    Pipeline = request.Pipeline
                });
            }

            // A retired pipeline is refused here, before a background job is accepted, wherever the
            // reference comes from. The dispatch never falls back to another pipeline.
            try
            {
                await ResolveDispatchPipelineAsync(request, dispatchVessel, token).ConfigureAwait(false);
            }
            catch (PipelineInactiveException inactive)
            {
                return PipelineInactiveResult(inactive);
            }

            // A full fleet or sibling lane refuses at submission with the code the reservation would give, so a
            // background job is never accepted only to fail on capacity a moment later. The reservation at
            // creation still decides, because capacity can change in between.
            FleetCapacityAdmissionException? full = await _Admiral
                .ProbeCapacityAsync(dispatchVessel, null, token).ConfigureAwait(false);
            if (full != null) return CapacityConflictResult(full);

            return null;
        }

        private async Task<VoyageDispatchResult?> ValidateConfiguredAssignmentEligibilityAsync(
            SharedVoyageDispatchRequest request,
            Vessel vessel,
            string? pipelineId,
            IReadOnlyList<MissionDescription> effectiveMissions,
            CancellationToken token)
        {
            Pipeline? resolvedPipeline;
            try
            {
                resolvedPipeline = await _Admiral.ResolvePipelineAsync(pipelineId, vessel, token).ConfigureAwait(false);
            }
            catch (PipelineInactiveException inactive)
            {
                return PipelineInactiveResult(inactive);
            }
            PipelineStageSkipResult skipped;
            try
            {
                skipped = PipelineStageSkip.Apply(
                    resolvedPipeline,
                    PipelineStageSkip.FromOperator(request.SkipStages, request.SkipStagesReason, request.ObjectiveAuthContext));
            }
            catch (StageSkipRefusedException refused)
            {
                return StageSkipRefusedResult(refused);
            }
            Pipeline? pipeline = skipped.Pipeline;
            bool isMultiStage = pipeline != null
                && !(pipeline.Stages.Count == 1 && String.Equals(pipeline.Stages[0].PersonaName, "Worker", StringComparison.Ordinal));
            ArmadaSettings settings = request.Settings ?? _Settings ?? new ArmadaSettings();
            IReadOnlyList<MissionDescription> missions;
            try
            {
                missions = MissionAliasResolver.ResolveAndOrder(effectiveMissions.ToList());
            }
            catch (InvalidDataException ex)
            {
                return VoyageDispatchResult.BadRequest(new { Error = ex.Message });
            }

            foreach (MissionDescription description in missions)
            {
                List<PipelineStage> stages = isMultiStage
                    ? pipeline!.Stages.OrderBy(stage => stage.Order).ToList()
                    : new List<PipelineStage> { new PipelineStage(1, AdmiralService.SingleStagePersona(pipeline)) };
                foreach (PipelineStage stage in stages)
                {
                    string persona = stage.PersonaName;
                    string? preferredModel = isMultiStage
                        ? PreferredModelTierSelector.ResolveEffectivePreferredModel(
                            stage.PreferredModel,
                            description.PreferredModel,
                            settings.ModelTier.MinimumTierForPersona(persona))
                        : description.PreferredModel;
                    CaptainAssignmentOverride? assignment = MissionService.SelectCaptainOverride(request.CaptainAssignments, persona);
                    OwnedRecordLookup<Persona> personaLookup = await MissionService.ReadUsablePersonaAsync(
                        _Database,
                        vessel.TenantId,
                        vessel.UserId,
                        persona,
                        token).ConfigureAwait(false);
                    RequestedCaptainResolution requested = RequestedCaptainAssignmentRule.ResolveRequest(
                        assignment,
                        personaLookup.Record?.DefaultCaptainId);
                    Mission probe = new Mission(description.Title, description.Description)
                    {
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        VesselId = vessel.Id,
                        Persona = persona,
                        PreferredModel = preferredModel,
                        RequestedCaptainId = requested.CaptainId,
                        Tier = requested.FallbackTier
                    };

                    string? reason = await MissionService.DescribeUnassignableByConstructionAsync(
                        _Database, settings, probe, token).ConfigureAwait(false);
                    if (reason == null) continue;

                    return VoyageDispatchResult.BadRequest(new
                    {
                        Error = "Mission cannot be dispatched: no configured captain can serve persona " + persona + " at its requested tier.",
                        Code = "mission_unassignable_by_construction",
                        Reason = reason,
                        Action = "Add or configure a captain that can serve this persona and tier, or change the requested tier."
                    });
                }
            }

            return null;
        }

        public async Task<VoyageDispatchResult> DispatchAsync(SharedVoyageDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                CollectionLimits.RequireDispatch(request.Missions, request.SelectedPlaybooks);
            }
            catch (CollectionLimitExceededException limit)
            {
                return VoyageDispatchResult.BadRequest(new
                {
                    Error = limit.Message,
                    Code = CollectionLimitExceededException.ErrorCode,
                    Field = limit.Field,
                    Limit = limit.Limit,
                    Actual = limit.Actual
                });
            }

            string title = request.Title;
            string description = request.Description ?? "";
            string vesselId = request.VesselId;
            List<MissionDescription> missions = request.Missions!;
            List<SelectedPlaybook> callerPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>();
            string? objectiveId = NormalizeEmpty(request.ObjectiveId);

            // Per-step elapsed logging: a prior regression let dispatch hang with no voyage and no log
            // line at all, making the stalled step invisible. These markers are cheap and pinpoint which
            // step is slow if a future stall recurs.
            Stopwatch dispatchWatch = Stopwatch.StartNew();
            LogDispatchInfo("dispatch start vessel " + vesselId + " missions=" + (missions?.Count ?? 0));

            VoyageDispatchResult? preconditions = await ValidatePreconditionsAsync(request, token).ConfigureAwait(false);
            if (preconditions != null) return preconditions;

            // The admiral's own hold rule, checked again before admission and before either creation path:
            // a background job can start after a hold was engaged. The alias path creates its voyage row
            // directly, so waiting for the admiral would leave an empty voyage behind.
            VoyageDispatchResult? heldAtStart = HoldRefusalResult();
            if (heldAtStart != null) return heldAtStart;
            LogDispatchInfo("dispatch step preconditions_ok elapsedMs=" + dispatchWatch.ElapsedMilliseconds);

            // Dispatch preparation can add objective text, inherited mode, start refs, and context
            // metadata. Work on a copy so a failed caller retry always starts from its original input.
            missions = missions!.Select(CloneMissionDescription).ToList();

            // A Research objective linked to this dispatch runs its missions read-only, so a mission
            // that did not state its own mode inherits the objective's mode before the pipeline
            // expands. Without this the operator path produced Implementation missions for a
            // report-only objective and the Judge failed the correct empty diff -- the autonomous
            // scheduler already derives the mode; this is the same rule on the operator path.
            Objective? dispatchObjective = await ApplyObjectiveDefaultsAsync(
                objectiveId,
                request.ObjectiveAuthContext,
                missions).ConfigureAwait(false);
            // Keep the authorizer even when an operator supplies additional voyage context.
            // Put its marker first so a caller-provided marker cannot select another objective.
            if (dispatchObjective != null
                && !String.Equals(VoyageObjectiveResolver.ReadBriefObjectiveId(description), dispatchObjective.Id, StringComparison.Ordinal))
            {
                string brief = ObjectiveBriefRenderer.Render(dispatchObjective, request.PreparationAnchorMoves);
                description = String.IsNullOrWhiteSpace(description) ? brief : brief + Environment.NewLine + Environment.NewLine + description;
            }

            // An operator forced past the preflight only when force was set and the objective was actually
            // incomplete or model-flagged; a force flag on a clean objective records nothing. Preconditions
            // already refused any other blocking issue, so reaching here with both means an override.
            if (objectiveId != null
                && request.ForcePreflight
                && dispatchObjective != null
                && (!ObjectivePreflightEvaluator.IsComplete(dispatchObjective.Preparation?.Preflight)
                    || request.PreflightModelFlaggedQuestions.Count > 0))
            {
                await EmitPreflightOverrideEventAsync(
                    dispatchObjective, request.ObjectiveAuthContext, request.PreflightModelFlaggedQuestions, token).ConfigureAwait(false);
            }

            Vessel? dispatchVessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (dispatchVessel == null) return VoyageDispatchResult.NotFound(new
            {
                Error = "Vessel not found: " + vesselId,
                Code = "vessel_not_found",
                Reason = "Vessel " + vesselId + " does not exist in this admiral.",
                Action = "Register the vessel via armada_add_vessel or verify the vesselId.",
                VesselId = vesselId
            });

            List<SelectedPlaybook> mergedPlaybooks = PlaybookMerge.MergeWithVesselDefaults(dispatchVessel.GetDefaultPlaybooks(), callerPlaybooks);

            string? requestedPipelineId = NormalizeEmpty(request.PipelineId);
            string? requestedPipelineName = NormalizeEmpty(request.Pipeline);
            if (requestedPipelineId == null && requestedPipelineName == null)
                requestedPipelineId = NormalizeEmpty(dispatchObjective?.SuggestedPipelineId);
            string? pipelineId = await ResolvePipelineIdAsync(
                requestedPipelineId,
                requestedPipelineName).ConfigureAwait(false);

            string? codeContextError = await PrepareDispatchCodeContextAsync(
                dispatchVessel,
                request.CodeContextMode,
                request.CodeContextTokenBudget,
                request.CodeContextMaxResults,
                missions,
                token).ConfigureAwait(false);
            LogDispatchInfo("dispatch step code_context_prepared elapsedMs=" + dispatchWatch.ElapsedMilliseconds
                + " deterministic=true");
            if (codeContextError != null) return VoyageDispatchResult.BadRequest(new { Error = codeContextError });

            // Keep roster readiness out of synchronous job preflight. Evaluate the effective, prepared
            // mission descriptions here, after request and code-context errors but before dispatch records.
            if (objectiveId == null)
            {
                VoyageDispatchResult? assignmentFeasibility = await ValidateConfiguredAssignmentEligibilityAsync(
                    request, dispatchVessel, pipelineId, missions, token).ConfigureAwait(false);
                if (assignmentFeasibility != null) return assignmentFeasibility;
            }

            bool hasAliases = missions.Any(m =>
                !String.IsNullOrEmpty(m.Alias) || !String.IsNullOrEmpty(m.DependsOnMissionAlias));
            ObjectiveDispatchAdmission? admission = null;
            List<string> admissionObjectiveIds = CollectAdmissionObjectiveIds(objectiveId, request.LinkedObjectiveIds);
            if (admissionObjectiveIds.Count > 0 && _ObjectiveService != null)
            {
                try
                {
                    admission = await _ObjectiveService.AcquireDispatchAdmissionAsync(
                        RequireObjectiveCaller(request.ObjectiveAuthContext, String.Join(", ", admissionObjectiveIds)),
                        admissionObjectiveIds,
                        new ObjectiveDispatchAttemptDescriptor { Title = title, VesselId = vesselId },
                        token).ConfigureAwait(false);
                }
                catch (ObjectiveAlreadyDispatchedException alreadyDispatched)
                {
                    return AlreadyDispatchedResult(alreadyDispatched.ObjectiveId, alreadyDispatched.WinningVoyageId);
                }
                catch (ObjectiveDispatchBusyException busy)
                {
                    return BusyResult(busy);
                }
            }

            // The request's own skipStages win, and an empty list skips nothing. Without one, a linked
            // objective's confirmed stored skip is the default; its entries that name no stage of the
            // chosen pipeline are dropped and recorded on the voyage instead of refusing the dispatch.
            StageSkipRequest? stageSkip = request.SkipStages != null
                ? PipelineStageSkip.FromOperator(request.SkipStages, request.SkipStagesReason, request.ObjectiveAuthContext)
                : PipelineStageSkip.AsObjectiveDefault(dispatchObjective?.Preparation?.StageSkip);

            Voyage? voyage = null;
            try
            {
                object dispatchResult;
                if (hasAliases)
                {
                    dispatchResult = await DispatchWithAliasesAsync(
                        title,
                        description,
                        vesselId,
                        dispatchVessel,
                        missions,
                        mergedPlaybooks,
                        pipelineId,
                        request.Settings ?? _Settings,
                        stageSkip,
                        request.CaptainAssignments,
                        request.IsEvaluation).ConfigureAwait(false);
                }
                else
                {
                    dispatchResult = await _Admiral.DispatchVoyageQueuedAsync(
                        title,
                        description,
                        vesselId,
                        missions,
                        pipelineId,
                        mergedPlaybooks,
                        stageSkip,
                        request.CaptainAssignments,
                        token,
                        request.IsEvaluation).ConfigureAwait(false);
                }

                if (dispatchResult is not Voyage createdVoyage)
                    return VoyageDispatchResult.BadRequest(dispatchResult);
                voyage = createdVoyage;
                await VoyageDispatchOrigin.RecordAsync(_Database, voyage, request.DispatcherParticipantKey, token, dispatchObjective == null ? null
                    : ObjectiveReviewRepeatGuard.InputHash(dispatchObjective, request.ResolvedSourceCommit), request.ResolvedSourceCommit).ConfigureAwait(false);

                if (admission != null)
                    await admission.RecordVoyageCreatedAsync(voyage, token).ConfigureAwait(false);
                admission?.ThrowIfOwnershipLost();

                VoyageDispatchResult? linkConflict = await LinkObjectiveToVoyageAsync(
                    admissionObjectiveIds,
                    request.ObjectiveAuthContext,
                    voyage,
                    admission,
                    token).ConfigureAwait(false);
                if (linkConflict != null)
                {
                    LogDispatchInfo("dispatch conflict voyage " + voyage.Id + " totalMs=" + dispatchWatch.ElapsedMilliseconds + " objective_link_refused=true");
                    return linkConflict;
                }
                admission?.MarkLinked();
            }
            catch (StageSkipRefusedException refused) when (voyage == null)
            {
                return StageSkipRefusedResult(refused);
            }
            catch (PipelineInactiveException inactive) when (voyage == null)
            {
                return PipelineInactiveResult(inactive);
            }
            catch (DispatchHoldActiveException held) when (voyage == null)
            {
                return VoyageDispatchResult.Conflict(DispatchHoldRefusal.From(held.Hold));
            }
            catch (FleetCapacityAdmissionException capacity)
            {
                if (voyage != null && ObjectiveService.IsActiveVoyageStatus(voyage.Status))
                {
                    await VoyageCancellation.CancelVoyageAsync(
                        _Database,
                        voyage,
                        "Voyage cancelled: " + capacity.Code + ".",
                        CancellationToken.None,
                        _Admiral.RecallCaptainAsync).ConfigureAwait(false);
                }
                return CapacityConflictResult(capacity);
            }
            catch
            {
                if (voyage != null && ObjectiveService.IsActiveVoyageStatus(voyage.Status))
                {
                    await VoyageCancellation.CancelVoyageAsync(
                        _Database,
                        voyage,
                        "Voyage cancelled: objective dispatch did not complete.",
                        CancellationToken.None,
                        _Admiral.RecallCaptainAsync).ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                if (admission != null)
                    await admission.DisposeAsync().ConfigureAwait(false);
            }

            if (voyage == null)
                throw new InvalidOperationException("Dispatch did not return a voyage.");

            // Arm the voyage's own Checks here, in the same action as the dispatch, so the voyage
            // carries a standing record of which gates it wants. A record is armed Pending with no
            // command and no branch; the executor stamps both onto it and runs it once a stage has
            // committed, so the Check measures the work under review and not the default branch.
            await ArmVoyageChecksAsync(voyage, dispatchVessel, token).ConfigureAwait(false);

            await WarnOnCoordinationClaimConflictsAsync(voyage, dispatchVessel, request.ObjectiveId, request.DispatcherParticipantKey, token).ConfigureAwait(false);

            LogDispatchInfo("dispatch complete voyage " + voyage.Id + " totalMs=" + dispatchWatch.ElapsedMilliseconds);
            return VoyageDispatchResult.Success(new VoyageDispatchResponse(voyage,
                await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false)));
        }

        /// <summary>
        /// When another participant holds an active claim on this vessel or objective,
        /// announce the overlap on the coordination board. The dispatch proceeds -
        /// claims are reservations with a named holder, not locks - so both parties
        /// see the collision while there is still time to cancel one side. A claim the
        /// dispatching participant holds itself is not an overlap and is not announced.
        /// </summary>
        private async Task WarnOnCoordinationClaimConflictsAsync(Voyage voyage, Vessel vessel, string? objectiveId, string? dispatcherParticipantKey, CancellationToken token)
        {
            List<CoordinationClaim> conflicts;
            try
            {
                CoordinationService coordination = new CoordinationService(_Logging ?? new LoggingModule(), _Database);
                conflicts = await coordination.FindDispatchConflictsAsync(vessel.Id, objectiveId, dispatcherParticipantKey, token).ConfigureAwait(false);
                if (conflicts.Count == 0) return;

                List<string> parts = new List<string>();
                foreach (CoordinationClaim claim in conflicts)
                {
                    parts.Add(claim.DisplayName + " holds a claim on " +
                        claim.SubjectType.ToString().ToLowerInvariant() + " " + claim.SubjectId +
                        (String.IsNullOrWhiteSpace(claim.Note) ? String.Empty : " (" + claim.Note + ")") +
                        ", expires " + claim.ExpiresUtc.ToString("u"));
                }

                string note = "[claims] Voyage " + voyage.Id + " dispatched on vessel " + vessel.Id +
                    (String.IsNullOrEmpty(objectiveId) ? String.Empty : " for objective " + objectiveId) +
                    " while " + String.Join("; ", parts) + ". Coordinate before both proceed.";
                await coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey,
                    Armada.Core.Enums.CoordinationAuthorTypeEnum.System,
                    null,
                    "armada",
                    note,
                    voyage.Id, null, vessel.Id).ConfigureAwait(false);

                if (_Logging != null)
                    _Logging.Warn("[VoyageDispatchService] " + note);
            }
            catch (NotSupportedException)
            {
                // Claims are SQLite/PostgreSQL-only today; other backends skip the check.
            }
            catch (Exception ex)
            {
                if (_Logging != null)
                    _Logging.Warn("[VoyageDispatchService] claim conflict check failed: " + ex.Message);
            }
        }

        #endregion

        #region Private-Methods

        private async Task<VoyageDispatchResult?> ValidateCallerReferencesAsync(SharedVoyageDispatchRequest request, CancellationToken token)
        {
            AuthContext? caller = request.ObjectiveAuthContext;
            if (caller == null) return null;

            if (request.Missions != null)
            {
                foreach (MissionDescription mission in request.Missions)
                {
                    string? dependsOn = NormalizeEmpty(mission?.DependsOnMissionId);
                    if (dependsOn == null) continue;
                    Mission? dependency = await CallerScopedRead.ReadMissionAsync(_Database, caller, dependsOn, token).ConfigureAwait(false);
                    if (dependency == null) return VoyageDispatchResult.NotFound(new
                    {
                        Error = "dependsOnMissionId not found: " + dependsOn,
                        Code = "mission_not_found",
                        Reason = "Mission " + dependsOn + " does not exist in the caller's scope.",
                        Action = "Verify the dependsOnMissionId.",
                        MissionId = dependsOn
                    });
                }
            }

            return null;
        }

        /// <summary>
        /// Refuse a captain assignment whose named captain can never take the stage it names. A named persona
        /// is checked against that persona; a wildcard entry against every stage of the effective pipeline
        /// that has no entry of its own. The finding is the one the dispatch preview reports, so an accepted
        /// dispatch never leaves a mission waiting on a captain that cannot take it. Returns null when every
        /// assignment may be accepted.
        /// </summary>
        private async Task<VoyageDispatchResult?> ValidateCaptainAssignmentsAsync(
            SharedVoyageDispatchRequest request,
            Vessel vessel,
            CancellationToken token)
        {
            List<CaptainAssignmentOverride> named = (request.CaptainAssignments ?? new List<CaptainAssignmentOverride>())
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.CaptainId))
                .ToList();
            if (named.Count == 0) return null;

            ModelTierSettings? tiers = (request.Settings ?? _Settings)?.ModelTier;
            List<string>? stagePersonas = null;
            foreach (CaptainAssignmentOverride assignment in named)
            {
                List<string> personas;
                if (!IsWildcardAssignment(assignment))
                {
                    personas = new List<string> { assignment.Persona.Trim() };
                }
                else
                {
                    try
                    {
                        stagePersonas ??= await ResolveStagePersonasAsync(request, vessel, token).ConfigureAwait(false);
                    }
                    catch (StageSkipRefusedException refused)
                    {
                        return StageSkipRefusedResult(refused);
                    }
                    catch (PipelineInactiveException inactive)
                    {
                        return PipelineInactiveResult(inactive);
                    }
                    personas = stagePersonas
                        .Where(persona => MissionService.SelectCaptainOverride(request.CaptainAssignments, persona) == assignment)
                        .ToList();
                }
                if (personas.Count == 0) continue;

                string captainId = assignment.CaptainId!.Trim();
                Captain? captain = request.ObjectiveAuthContext != null
                    ? await CallerScopedRead.ReadCaptainAsync(_Database, request.ObjectiveAuthContext, captainId, token).ConfigureAwait(false)
                    : await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
                foreach (string persona in personas)
                {
                    CaptainEligibilityFinding? finding = RequestedCaptainAssignmentRule.EvaluateAssignment(
                        captain, persona, vessel.TenantId, tiers);
                    if (finding == null) continue;
                    return VoyageDispatchResult.BadRequest(new
                    {
                        Error = "Captain assignment refused: captain " + captainId + " cannot run the " + persona
                            + " role: it is " + finding.Reason + ".",
                        Code = finding.Code,
                        Reason = "Captain " + captainId + " is " + finding.Reason + ".",
                        Action = "Name a captain that can run the " + persona + " role (its AllowedPersonas, runtime and tier), or remove the assignment.",
                        CaptainId = captainId,
                        Persona = persona
                    });
                }
            }

            return null;
        }

        private static bool IsWildcardAssignment(CaptainAssignmentOverride assignment)
        {
            return String.IsNullOrWhiteSpace(assignment.Persona) || assignment.Persona.Trim() == "*";
        }

        // The stage personas the dispatch will create, resolved by the admiral's own pipeline rule and the
        // operator's stage skips; a single-stage dispatch is its one stage persona.
        private async Task<List<string>> ResolveStagePersonasAsync(SharedVoyageDispatchRequest request, Vessel vessel, CancellationToken token)
        {
            string? requestedPipeline = await ResolvePipelineIdAsync(NormalizeEmpty(request.PipelineId), NormalizeEmpty(request.Pipeline)).ConfigureAwait(false);
            // An unknown pipeline name is refused by name later in validation; no stage applies to it here.
            if (String.Equals(requestedPipeline, "__pipeline_not_found__", StringComparison.Ordinal)) return new List<string>();

            Pipeline? pipeline = await ResolveDispatchPipelineAsync(request, vessel, token).ConfigureAwait(false);
            pipeline = PipelineStageSkip.Apply(
                pipeline, PipelineStageSkip.FromOperator(request.SkipStages, request.SkipStagesReason, request.ObjectiveAuthContext)).Pipeline;
            if (pipeline == null || pipeline.Stages == null || pipeline.Stages.Count <= 1)
                return new List<string> { AdmiralService.SingleStagePersona(pipeline) };
            return pipeline.Stages
                .Where(stage => stage != null && !String.IsNullOrWhiteSpace(stage.PersonaName))
                .Select(stage => stage.PersonaName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Apply the dispatch-preflight gate to a linked objective's preview. An incomplete preflight and
        /// a D5 preflight model flag are the only blocking issues the operator force flag may override;
        /// every other blocking issue still refuses the dispatch. Returns the refusing result, or null
        /// when the dispatch may proceed.
        /// </summary>
        private static VoyageDispatchResult? PreflightGateResult(
            ObjectiveDispatchPreview preview,
            bool forcePreflight,
            string objectiveId)
        {
            PreflightGateOutcomeEnum outcome = ObjectivePreflightGate.Classify(preview, forcePreflight);
            switch (outcome)
            {
                case PreflightGateOutcomeEnum.BlockedByPreflight:
                    return VoyageDispatchResult.BadRequest(new
                    {
                        Error = ObjectivePreflightGate.RefusalMessage,
                        Code = ObjectivePreflightGate.IssueCode,
                        ObjectiveId = objectiveId,
                        IncompleteQuestions = preview.Preflight.IncompleteQuestions,
                        ModelFlaggedQuestions = ObjectivePreflightGate.ModelFlaggedQuestions(preview),
                        Preview = preview
                    });
                case PreflightGateOutcomeEnum.BlockedByOther:
                    return VoyageDispatchResult.BadRequest(new
                    {
                        Error = "Objective dispatch preview found blocking issues.",
                        Code = "objective_dispatch_not_ready",
                        ObjectiveId = objectiveId,
                        Preview = preview
                    });
                default:
                    return null;
            }
        }

        private async Task EmitPreflightOverrideEventAsync(
            Objective objective,
            AuthContext? auth,
            IReadOnlyList<int> modelFlaggedQuestions,
            CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = ObjectivePreflightGate.BuildOverrideEvent(
                    objective, ObjectivePreflightGate.OperatorName(auth), modelFlaggedQuestions);
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging?.Warn("[VoyageDispatchService] could not record preflight override for objective "
                    + objective.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Attach Pending Build and UnitTest Checks to a freshly dispatched voyage.
        /// </summary>
        /// <remarks>
        /// The Checks are armed Pending, not executed: at dispatch there is no branch and no commit
        /// to measure. They are intent markers, executed after the voyage completes, and they carry
        /// no weight in the real-signal gate -- a Judge PASS still needs Checks that actually ran
        /// against the work. Arming costs nothing now; running a full suite at dispatch would
        /// instead load the host at the moment the first captain starts working.
        /// <para>
        /// Arming never fails a dispatch. A voyage that exists without its Checks can still be
        /// armed by hand, whereas refusing to dispatch over a Check record would turn a
        /// convenience into an outage.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Arm this voyage's Checks through the shared arming seam.
        /// </summary>
        /// <remarks>
        /// The work lives in VoyageCheckArmingService because this is not the only path that
        /// starts a voyage. The autonomous objective scheduler dispatches through the admiral
        /// directly and calls the same seam, so neither path can arm while the other silently
        /// does not.
        /// </remarks>
        private async Task ArmVoyageChecksAsync(Voyage voyage, Vessel vessel, CancellationToken token)
        {
            VoyageCheckArmingService arming = new VoyageCheckArmingService(_Database, _Settings, _Logging);
            await arming.ArmAsync(voyage, vessel, "dispatch", token).ConfigureAwait(false);
        }

        private VoyageDispatchResult? ValidateRequest(string title, List<MissionDescription>? missions)
        {
            if (String.IsNullOrWhiteSpace(title)) return VoyageDispatchResult.BadRequest(new
            {
                Error = "armada_dispatch requires a non-empty title.",
                Code = "missing_title",
                Reason = "The voyage title was null or whitespace.",
                Action = "Provide a non-empty title naming the voyage."
            });

            if (missions == null || missions.Count == 0) return VoyageDispatchResult.BadRequest(new
            {
                Error = "armada_dispatch requires a non-empty missions array; each mission needs a title and description.",
                Code = "missing_missions",
                Reason = "The missions array was null or empty.",
                Action = "Provide at least one mission, each with a title and a description."
            });

            for (int i = 0; i < missions.Count; i++)
            {
                MissionDescription mission = missions[i];
                int missionNumber = i + 1;

                if (String.IsNullOrWhiteSpace(mission.Title)) return VoyageDispatchResult.BadRequest(new
                {
                    Error = "armada_dispatch mission " + missionNumber + " is missing a title.",
                    Code = "missing_mission_title",
                    Reason = "Mission " + missionNumber + " had a null or whitespace title.",
                    Action = "Provide a non-empty title for mission " + missionNumber + "."
                });

                if (String.IsNullOrWhiteSpace(mission.Description)) return VoyageDispatchResult.BadRequest(new
                {
                    Error = "armada_dispatch mission " + missionNumber + " is missing a description.",
                    Code = "missing_mission_description",
                    Reason = "Mission " + missionNumber + " had a null or whitespace description.",
                    Action = "Provide a non-empty description for mission " + missionNumber + "."
                });

                // Optional: reject a title that already carries a configured stage-persona prefix.
                // AdmiralService prepends "[<persona>] " to each pipeline stage's title, so a
                // dispatched title that already begins with one is a prior run's MATERIALIZED
                // stage mission. The guard is off by default; operators enable it and supply
                // the prefix list in settings.
                if (TitleCarriesStagePersonaPrefix(mission.Title)) return VoyageDispatchResult.BadRequest(new
                {
                    Error = "armada_dispatch mission " + missionNumber + " has a title that already carries a stage-persona prefix: '" + mission.Title!.Trim() + "'.",
                    Code = "mission_title_carries_stage_persona_prefix",
                    Reason = "A title that begins with a configured stage-persona prefix is a materialized pipeline STAGE mission, not a task. The pipeline prepends the persona itself, so dispatching stage missions as tasks multiplies the work by the stage count.",
                    Action = "Dispatch the objective's ORIGINAL task once, not a prior run's per-stage missions. Remove the leading '[<persona>] ' tag from the title."
                });

                // Reject an unrecognized mode rather than parsing it down to Implementation. A typo
                // such as "audits" would otherwise produce an implementing mission that is judged by
                // the commit gate, which is the exact failure mode modes exist to remove.
                if (!String.IsNullOrWhiteSpace(mission.Mode) && !Armada.Core.Enums.MissionModes.IsKnown(mission.Mode))
                    return VoyageDispatchResult.BadRequest(new
                    {
                        Error = "armada_dispatch mission " + missionNumber + " has an unknown mode: " + mission.Mode + ".",
                        Code = "invalid_mission_mode",
                        Reason = "Mission " + missionNumber + " requested mode '" + mission.Mode + "', which Armada does not recognize.",
                        Action = "Use Implementation, Audit, or Research, or omit mode to default to Implementation."
                    });
            }

            return null;
        }

        /// <summary>
        /// Reports whether a mission title already carries a configured stage-persona prefix.
        /// The guard is off unless settings enable it and supply at least one prefix.
        /// Leading whitespace is tolerated; the match is case-insensitive.
        /// </summary>
        /// <param name="title">The mission title to inspect.</param>
        /// <returns>True when the guard is on and the title opens with a configured prefix.</returns>
        private bool TitleCarriesStagePersonaPrefix(string? title)
        {
            VoyageDispatchSettings guard = _Settings?.VoyageDispatch ?? new VoyageDispatchSettings();
            if (!guard.RejectStagePersonaTitlePrefixes)
                return false;
            if (String.IsNullOrWhiteSpace(title))
                return false;

            string trimmed = title.TrimStart();
            IReadOnlyList<string> prefixes = guard.StagePersonaTitlePrefixes;
            if (prefixes == null || prefixes.Count == 0)
                return false;

            foreach (string prefix in prefixes)
            {
                if (String.IsNullOrWhiteSpace(prefix))
                    continue;
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool ShouldEvaluateCodeIndexPrecondition(SharedVoyageDispatchRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string dispatchMode = String.IsNullOrWhiteSpace(request.CodeContextMode)
                ? _CodeContextModeAuto
                : request.CodeContextMode.Trim();

            foreach (MissionDescription mission in request.Missions)
            {
                string effectiveMode = String.IsNullOrWhiteSpace(mission.CodeContextMode)
                    ? dispatchMode
                    : mission.CodeContextMode.Trim();
                if (!String.Equals(effectiveMode, _CodeContextModeOff, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Return the caller an objective-linked dispatch acts for. Reading or linking an objective
        /// is scoped to that caller, so a dispatch that names an objective without a caller is
        /// refused rather than read with a default administrative identity.
        /// </summary>
        private static AuthContext RequireObjectiveCaller(AuthContext? authContext, string? objectiveId)
        {
            if (authContext == null || !authContext.IsAuthenticated)
                throw new UnauthorizedAccessException("A dispatch linked to objective " + objectiveId + " must carry the caller's identity.");
            return authContext;
        }

        private bool IsCodeIndexEnabled()
        {
            return _Settings?.CodeIndex?.Enabled ?? true;
        }

        private async Task<VoyageDispatchResult?> ValidateObjectiveAsync(string? objectiveId, AuthContext? authContext, string vesselId)
        {
            if (String.IsNullOrEmpty(objectiveId)) return null;
            if (_ObjectiveService == null)
                return VoyageDispatchResult.BadRequest(new { Error = "Objective service unavailable; cannot link objectiveId " + objectiveId });

            AuthContext auth = RequireObjectiveCaller(authContext, objectiveId);
            Objective? objective = await _ObjectiveService.ReadAsync(auth, objectiveId).ConfigureAwait(false);
            if (objective == null)
                return VoyageDispatchResult.NotFound(new { Error = "Objective not found: " + objectiveId });

            if (objective.VesselIds.Count > 0
                && !objective.VesselIds.Contains(vesselId, StringComparer.OrdinalIgnoreCase))
                return VoyageDispatchResult.BadRequest(new
                {
                    Error = "Objective target vessel does not match dispatch vessel.",
                    Code = "objective_vessel_mismatch",
                    ObjectiveId = objectiveId,
                    ObjectiveVesselIds = objective.VesselIds,
                    DispatchVesselId = vesselId
                });

            string? preparedTargetVessel = objective.Preparation?.Target?.VesselId;
            if (!String.IsNullOrWhiteSpace(preparedTargetVessel)
                && !String.Equals(preparedTargetVessel, vesselId, StringComparison.OrdinalIgnoreCase))
                return VoyageDispatchResult.BadRequest(new
                {
                    Error = "Prepared target vessel does not match dispatch vessel.",
                    Code = "preparation_target_vessel_mismatch",
                    ObjectiveId = objectiveId,
                    PreparedTargetVesselId = preparedTargetVessel,
                    DispatchVesselId = vesselId
                });

            return null;
        }

        /// <summary>
        /// Apply one linked objective to operator-dispatched missions. The server appends the shared
        /// objective brief, inherits the objective start ref when a mission did not set one, and
        /// applies the shared Research-mode default. Explicit mission values always win.
        /// </summary>
        /// <param name="objectiveId">The linked objective id, or null when the dispatch is unlinked.</param>
        /// <param name="authContext">Auth context for reading the objective, or null for the default tenant admin.</param>
        /// <param name="missions">Mission descriptions to mutate in place before dispatch.</param>
        private async Task<Objective?> ApplyObjectiveDefaultsAsync(string? objectiveId, AuthContext? authContext, List<MissionDescription>? missions)
        {
            if (String.IsNullOrEmpty(objectiveId) || _ObjectiveService == null || missions == null || missions.Count == 0)
                return null;

            AuthContext auth = RequireObjectiveCaller(authContext, objectiveId);
            Objective? objective = await _ObjectiveService.ReadAsync(auth, objectiveId).ConfigureAwait(false);
            if (objective == null) return null;

            string? derivedMode = MissionModes.FromObjectiveKind(objective.Kind);
            foreach (MissionDescription mission in missions)
            {
                if (mission == null) continue;
                mission.Description = ObjectiveBriefRenderer.AppendToMissionDescription(mission.Description, objective);
                if (String.IsNullOrWhiteSpace(mission.StartFromRef)
                    && !String.IsNullOrWhiteSpace(objective.StartFromRef))
                    mission.StartFromRef = objective.StartFromRef;
                if (!String.IsNullOrEmpty(derivedMode) && String.IsNullOrWhiteSpace(mission.Mode))
                {
                    mission.Mode = derivedMode;
                    LogDispatchInfo("derived read-only mode '" + derivedMode + "' for mission '" + mission.Title
                        + "' from objective " + objectiveId + " Kind=" + objective.Kind);
                }
            }
            return objective;
        }

        private static MissionDescription CloneMissionDescription(MissionDescription mission)
        {
            return new MissionDescription
            {
                Title = mission.Title,
                Description = mission.Description,
                PrestagedFiles = mission.PrestagedFiles?.ToList(),
                CodeContextMode = mission.CodeContextMode,
                CodeContextQuery = mission.CodeContextQuery,
                PreferredModel = mission.PreferredModel,
                CapabilityHint = mission.CapabilityHint,
                Mode = mission.Mode,
                DependsOnMissionId = mission.DependsOnMissionId,
                Alias = mission.Alias,
                DependsOnMissionAlias = mission.DependsOnMissionAlias,
                SelectedPlaybooks = mission.SelectedPlaybooks?.ToList(),
                StartFromRef = mission.StartFromRef
            };
        }

        private async Task<string?> ResolvePipelineIdAsync(string? requestedPipelineId, string? requestedPipeline)
        {
            string? pipelineId = requestedPipelineId;
            if (String.IsNullOrEmpty(pipelineId) && !String.IsNullOrEmpty(requestedPipeline))
            {
                // Several tenants may own a pipeline with this name. Pass the name on, so the admiral's
                // pipeline resolution picks the record the vessel's owner may use, instead of turning
                // the name into whichever record's id the storage returns first.
                Pipeline? namedPipeline = await _Database.Pipelines.ReadByNameAsync(requestedPipeline).ConfigureAwait(false);
                if (namedPipeline != null) pipelineId = requestedPipeline;
                else return "__pipeline_not_found__";
            }

            return pipelineId;
        }

        private async Task<string?> PrepareDispatchCodeContextAsync(
            Vessel vessel,
            string? topLevelMode,
            int? tokenBudget,
            int? maxResults,
            List<MissionDescription> missions,
            CancellationToken token)
        {
            DispatchContextPackStager stager = new DispatchContextPackStager(_Database, _CodeIndexService, _Settings, _Logging);
            return await stager.PrepareAsync(new DispatchContextPackRequest
            {
                VesselId = vessel.Id,
                CodeContextMode = topLevelMode,
                TokenBudget = tokenBudget,
                MaxResults = maxResults,
                Missions = missions,
                Source = "operator",
                RefuseOnFailure = true,
                TenantId = vessel.TenantId,
                UserId = vessel.UserId
            }, token).ConfigureAwait(false);
        }

        private void LogCodeContextWarning(string message)
        {
            if (_Logging == null) return;
            _Logging.Warn("[VoyageDispatchService] " + message);
        }

        private void LogCodeContextInfo(string message)
        {
            if (_Logging == null) return;
            _Logging.Info("[VoyageDispatchService] " + message);
        }

        private void LogDispatchInfo(string message)
        {
            if (_Logging == null) return;
            _Logging.Info("[VoyageDispatchService] " + message);
        }

        private static string? NormalizeEmpty(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            return value.Trim();
        }

        /// <summary>
        /// Link a freshly dispatched voyage to its objective. Returns a conflict result when the
        /// objective already has a nonterminal voyage (another dispatch won the race): the losing
        /// voyage is cancelled and the winner is named so the caller does not report success for a
        /// duplicate. Returns null when the link succeeded or did not apply.
        /// </summary>
        private async Task<VoyageDispatchResult?> LinkObjectiveToVoyageAsync(
            List<string> objectiveIds,
            AuthContext? authContext,
            Voyage voyage,
            ObjectiveDispatchAdmission? admission,
            CancellationToken token)
        {
            if (objectiveIds.Count == 0) return null;
            if (_ObjectiveService == null) return null;

            string objectiveId = objectiveIds[0];
            AuthContext auth = RequireObjectiveCaller(authContext, String.Join(", ", objectiveIds));
            List<string> linked = new List<string>();
            try
            {
                foreach (string linkObjectiveId in objectiveIds)
                {
                    objectiveId = linkObjectiveId;
                    await _ObjectiveService.LinkVoyageAsync(auth, linkObjectiveId, voyage.Id, token, false, admission).ConfigureAwait(false);
                    linked.Add(linkObjectiveId);
                }
                return null;
            }
            catch (ObjectiveDispatchOwnershipLostException)
            {
                await RevertObjectiveLinksAsync(auth, linked, voyage, admission).ConfigureAwait(false);
                throw;
            }
            catch (ObjectiveAlreadyDispatchedException alreadyDispatched)
            {
                await RevertObjectiveLinksAsync(auth, linked, voyage, admission).ConfigureAwait(false);
                // The scheduler (or a parallel operator dispatch) won the race for this objective.
                // The atomic guard refused the link; cancel this duplicate voyage and identify the
                // winning voyage so the caller sees the objective is already in flight.
                try
                {
                    await VoyageCancellation.CancelVoyageAsync(
                        _Database,
                        voyage,
                        "Voyage cancelled: objective " + objectiveId + " already dispatched as voyage " + alreadyDispatched.WinningVoyageId + ".",
                        CancellationToken.None,
                        _Admiral.RecallCaptainAsync).ConfigureAwait(false);
                }
                catch (Exception cancelEx)
                {
                    _Logging?.Warn("[VoyageDispatchService] could not cancel duplicate voyage " + voyage.Id + ": " + cancelEx.Message);
                }

                _Logging?.Warn("[VoyageDispatchService] voyage " + voyage.Id + " cancelled: objective " + objectiveId +
                    " already dispatched as voyage " + alreadyDispatched.WinningVoyageId + ".");
                return AlreadyDispatchedResult(objectiveId, alreadyDispatched.WinningVoyageId);
            }
            catch (Exception ex)
            {
                await RevertObjectiveLinksAsync(auth, linked, voyage, admission).ConfigureAwait(false);
                string cleanupError = String.Empty;
                try
                {
                    await VoyageCancellation.CancelVoyageAsync(
                        _Database,
                        voyage,
                        "Voyage cancelled: objective " + objectiveId + " could not be linked.",
                        CancellationToken.None,
                        _Admiral.RecallCaptainAsync).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    cleanupError = " Cleanup also failed: " + cleanupEx.Message;
                }

                _Logging?.Warn("[VoyageDispatchService] voyage " + voyage.Id + " could not be linked to objective "
                    + objectiveId + " and was cancelled: " + ex.Message + cleanupError);
                return VoyageDispatchResult.InternalError(new
                {
                    Error = "Objective link failed; the new voyage was cancelled.",
                    Code = "objective_link_failed",
                    Reason = ex.Message,
                    CleanupError = String.IsNullOrEmpty(cleanupError) ? null : cleanupError.Trim(),
                    ObjectiveId = objectiveId,
                    VoyageId = voyage.Id
                });
            }
        }

        /// <summary>
        /// Restore objectives this dispatch linked before a later link in the same admission failed,
        /// so a refused multi-objective dispatch leaves no objective pointing at its cancelled voyage.
        /// </summary>
        private async Task RevertObjectiveLinksAsync(
            AuthContext auth,
            List<string> linked,
            Voyage voyage,
            ObjectiveDispatchAdmission? admission)
        {
            if (_ObjectiveService == null || admission == null) return;
            foreach (string linkedObjectiveId in linked)
            {
                Objective? admitted = admission.FindObjective(linkedObjectiveId);
                if (admitted == null) continue;
                try
                {
                    await _ObjectiveService.RevertVoyageLinkAsync(auth, admitted, voyage.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging?.Warn("[VoyageDispatchService] could not restore objective " + linkedObjectiveId
                        + " after voyage " + voyage.Id + " failed to link: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The one objective a dispatch is about, for recording on its decisions: the dispatched objective, or
        /// the only linked objective. Null when the dispatch names none, or links several and dispatches none.
        /// </summary>
        private static string? DispatchSubjectObjectiveId(SharedVoyageDispatchRequest request)
        {
            string? objectiveId = NormalizeEmpty(request.ObjectiveId);
            if (objectiveId != null) return objectiveId;
            List<string> linked = CollectAdmissionObjectiveIds(null, request.LinkedObjectiveIds);
            return linked.Count == 1 ? linked[0] : null;
        }

        private static List<string> CollectAdmissionObjectiveIds(string? objectiveId, List<string>? linkedObjectiveIds)
        {
            List<string> ids = new List<string>();
            if (!String.IsNullOrWhiteSpace(objectiveId)) ids.Add(objectiveId.Trim());
            if (linkedObjectiveIds != null)
            {
                foreach (string linkedObjectiveId in linkedObjectiveIds)
                {
                    string? normalized = NormalizeEmpty(linkedObjectiveId);
                    if (normalized != null && !ids.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        ids.Add(normalized);
                }
            }
            return ids;
        }

        private static VoyageDispatchResult BusyResult(ObjectiveDispatchBusyException busy)
        {
            return VoyageDispatchResult.Conflict(new
            {
                Error = "Objective dispatch admission is busy.",
                Code = "objective_dispatch_busy",
                Reason = busy.Message,
                Action = "Retry after RetryAfterSeconds; another request is dispatching this objective and nothing was created.",
                Retryable = true,
                RetryAfterSeconds = (int)Math.Ceiling(busy.RetryAfter.TotalSeconds),
                ObjectiveId = busy.ObjectiveId
            });
        }

        private static VoyageDispatchResult AlreadyDispatchedResult(string objectiveId, string winningVoyageId)
        {
            return VoyageDispatchResult.Conflict(new
            {
                Error = "Objective already dispatched.",
                Code = "objective_already_dispatched",
                Reason = "Objective " + objectiveId + " already has a nonterminal voyage " + winningVoyageId + ".",
                Action = "Cancel the winning voyage first if you intend to re-dispatch this objective.",
                ObjectiveId = objectiveId,
                VoyageId = winningVoyageId
            });
        }

        // The pipeline a dispatch runs: the requested pipeline, else the linked objective's suggested pipeline,
        // else the vessel or fleet default, unless a read-only dispatch skips the default. Throws
        // PipelineInactiveException when that pipeline is retired.
        private async Task<Pipeline?> ResolveDispatchPipelineAsync(SharedVoyageDispatchRequest request, Vessel vessel, CancellationToken token)
        {
            string? requestedPipeline = await ResolvePipelineIdAsync(NormalizeEmpty(request.PipelineId), NormalizeEmpty(request.Pipeline)).ConfigureAwait(false);
            if (String.Equals(requestedPipeline, "__pipeline_not_found__", StringComparison.Ordinal)) return null;
            string? objectiveId = NormalizeEmpty(request.ObjectiveId);
            if (requestedPipeline == null && objectiveId != null && _ObjectiveService != null)
            {
                Objective? objective = await _ObjectiveService.ReadAsync(
                    RequireObjectiveCaller(request.ObjectiveAuthContext, objectiveId), objectiveId, token).ConfigureAwait(false);
                requestedPipeline = NormalizeEmpty(objective?.SuggestedPipelineId);
            }

            return AdmiralService.SkipsDefaultPipeline(requestedPipeline, request.Missions)
                ? null
                : await _Admiral.ResolvePipelineAsync(requestedPipeline, vessel, token).ConfigureAwait(false);
        }

        private static VoyageDispatchResult PipelineInactiveResult(PipelineInactiveException inactive)
        {
            return VoyageDispatchResult.BadRequest(new
            {
                Error = inactive.Message,
                Code = PipelineInactiveException.ReasonCode,
                Reason = "The " + inactive.Source + " " + inactive.PipelineName + " is retired (active=false); a dispatch never substitutes another pipeline.",
                Action = "Reactivate it with update_pipeline(active=true), or name an active pipeline and point the vessel, fleet or objective at it.",
                Pipeline = inactive.PipelineName,
                PipelineId = inactive.PipelineId,
                inactive.Source
            });
        }

        private static VoyageDispatchResult StageSkipRefusedResult(StageSkipRefusedException refused)
        {
            return VoyageDispatchResult.BadRequest(new
            {
                Error = refused.Message,
                refused.Code,
                refused.Persona,
                Action = "Remove or correct the refused entry in skipStages. The Judge is never skippable; every other entry must name one stage of the effective pipeline: " + PipelineStageSkip.NamingHelp
            });
        }

        private VoyageDispatchResult? HoldRefusalResult()
        {
            DispatchHoldSnapshot? hold = _Admiral.DispatchHold?.Snapshot();
            return hold == null ? null : VoyageDispatchResult.Conflict(DispatchHoldRefusal.From(hold));
        }

        private static VoyageDispatchResult CapacityConflictResult(FleetCapacityAdmissionException capacity)
        {
            return VoyageDispatchResult.Conflict(new
            {
                Error = capacity.Message,
                capacity.Code,
                capacity.ActiveCount,
                capacity.Limit,
                capacity.CandidateVesselId,
                capacity.LaneMembers
            });
        }

        private async Task<object> DispatchWithAliasesAsync(
            string title,
            string description,
            string vesselId,
            Vessel? vessel,
            List<MissionDescription> missions,
            List<SelectedPlaybook> selectedPlaybooks,
            string? pipelineId,
            ArmadaSettings? settings = null,
            StageSkipRequest? stageSkip = null,
            List<CaptainAssignmentOverride>? captainOverrides = null,
            bool isEvaluation = false)
        {
            if (vessel == null)
                return new { Error = "Vessel not found: " + vesselId };

            IReadOnlyList<MissionDescription> sortedMissions;
            try
            {
                sortedMissions = MissionAliasResolver.ResolveAndOrder(missions);
            }
            catch (InvalidDataException ex)
            {
                return new { Error = ex.Message };
            }

            Pipeline? resolvedPipeline = await _Admiral.ResolvePipelineAsync(pipelineId, vessel).ConfigureAwait(false);
            PipelineStageSkipResult skipResult = PipelineStageSkip.Apply(resolvedPipeline, stageSkip);
            Pipeline? pipeline = skipResult.Pipeline;
            bool isMultiStage = pipeline != null
                && !(pipeline.Stages.Count == 1 && pipeline.Stages[0].PersonaName == "Worker");

            Voyage voyage = AdmiralService.NewDispatchVoyage(title, description, vessel, captainOverrides);
            voyage.IsEvaluation = isEvaluation;
            voyage = await _Database.Voyages.CreateAsync(voyage).ConfigureAwait(false);
            voyage.SelectedPlaybooks = ClonePlaybookSelectionsLocal(selectedPlaybooks);
            if (voyage.SelectedPlaybooks.Count > 0)
            {
                await _Database.Playbooks.SetVoyageSelectionsAsync(voyage.Id, voyage.SelectedPlaybooks).ConfigureAwait(false);
            }

            Dictionary<string, string> aliasToMsnId = new Dictionary<string, string>(StringComparer.Ordinal);
            bool anyAssigned = false;

            try
            {
                foreach (MissionDescription md in sortedMissions)
                {
                    string? externalDep = null;
                if (!String.IsNullOrEmpty(md.DependsOnMissionAlias))
                    externalDep = aliasToMsnId[md.DependsOnMissionAlias];
                else if (!String.IsNullOrEmpty(md.DependsOnMissionId))
                    externalDep = md.DependsOnMissionId;

                List<SelectedPlaybook> mergedForMission = PlaybookMerge.MergeWithVesselDefaults(
                    voyage.SelectedPlaybooks,
                    md.SelectedPlaybooks ?? new List<SelectedPlaybook>());

                if (!isMultiStage)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.TenantId = vessel.TenantId;
                    mission.UserId = vessel.UserId;
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.Persona = AdmiralService.SingleStagePersona(pipeline);
                    mission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);
                    mission.PreferredModel = md.PreferredModel;
                    mission.CapabilityHint = md.CapabilityHint;
                    mission.Mode = Armada.Core.Enums.MissionModes.Parse(md.Mode);
                    mission.IsEvaluation = voyage.IsEvaluation;
                    mission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(mergedForMission);
                    mission.DependsOnMissionId = externalDep;
                    if (String.IsNullOrEmpty(externalDep)) mission.StartFromRef = md.StartFromRef;

                    mission = await _Admiral.DispatchMissionQueuedAsync(mission).ConfigureAwait(false);

                    if (mission.Status == MissionStatusEnum.Assigned || mission.Status == MissionStatusEnum.InProgress)
                        anyAssigned = true;

                    if (!String.IsNullOrEmpty(md.Alias))
                        aliasToMsnId[md.Alias] = mission.Id;
                    continue;
                }

                string baseTitle = md.Title.Length > 60 ? md.Title.Substring(0, 60).TrimEnd() + "..." : md.Title;
                string? previousOrderLastMissionId = null;
                string? lastStageMissionId = null;
                string stageChainId = Mission.NewStageChainId();

                IOrderedEnumerable<IGrouping<int, PipelineStage>> stageGroups =
                    pipeline!.Stages.GroupBy(s => s.Order).OrderBy(g => g.Key);

                foreach (IGrouping<int, PipelineStage> stageGroup in stageGroups)
                {
                    string? groupDependencyId = previousOrderLastMissionId ?? externalDep;
                    string? lastMissionInGroup = null;

                    foreach (PipelineStage stage in stageGroup)
                    {
                        Mission stageMission = new Mission(
                            "[" + stage.PersonaName + "] " + baseTitle,
                            md.Description);
                        stageMission.TenantId = vessel.TenantId;
                        stageMission.UserId = vessel.UserId;
                        stageMission.VoyageId = voyage.Id;
                        stageMission.VesselId = vesselId;
                        stageMission.Persona = stage.PersonaName;
                        stageMission.DependsOnMissionId = groupDependencyId;
                        // Same-order stages are parallel siblings; StageOrder is the barrier key that
                        // makes a downstream stage wait for EVERY sibling in the group, not just the
                        // last one its dependency happens to name. Without it the alias dispatch path
                        // would let a Judge review a diff its parallel sibling reviewers had not
                        // finished contributing to.
                        stageMission.StageOrder = stage.Order;
                        stageMission.StageChainId = stageChainId;
                        stageMission.PreferredModel = PreferredModelTierSelector.ResolveEffectivePreferredModel(
                            stage.PreferredModel,
                            md.PreferredModel,
                            settings?.ModelTier.MinimumTierForPersona(stage.PersonaName));
                        stageMission.CapabilityHint = md.CapabilityHint;
                        // Every stage of a read-only voyage stays read-only: a pipeline must not
                        // silently turn an audit into an implementing mission at stage 2.
                        stageMission.Mode = Armada.Core.Enums.MissionModes.Parse(md.Mode);
                        stageMission.IsEvaluation = voyage.IsEvaluation;
                        stageMission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(mergedForMission);

                        // Each pipeline stage receives a new dock worktree. Preserve every
                        // prestaged entry on every stage so briefing and reference files are
                        // available to reviewers as well as the first Worker.
                        stageMission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);
                        bool isFirstChainMission = previousOrderLastMissionId == null && lastMissionInGroup == null;

                        if (isFirstChainMission)
                        {
                            if (String.IsNullOrEmpty(externalDep)) stageMission.StartFromRef = md.StartFromRef;
                            stageMission = await _Admiral.DispatchMissionQueuedAsync(stageMission).ConfigureAwait(false);
                            if (stageMission.Status == MissionStatusEnum.Assigned || stageMission.Status == MissionStatusEnum.InProgress)
                                anyAssigned = true;
                        }
                        else
                        {
                            stageMission = await _Database.Missions.CreateAsync(stageMission).ConfigureAwait(false);

                            if (stageMission.SelectedPlaybooks != null
                                && stageMission.SelectedPlaybooks.Count > 0
                                && !String.IsNullOrEmpty(stageMission.TenantId))
                            {
                                LoggingModule effectiveLogging = _Logging ?? CreateSilentLogging();
                                IPlaybookService playbooks = new PlaybookService(_Database, effectiveLogging);
                                List<MissionPlaybookSnapshot> snapshots = await playbooks.CreateSnapshotsAsync(
                                    stageMission.TenantId,
                                    stageMission.SelectedPlaybooks).ConfigureAwait(false);
                                await _Database.Playbooks.SetMissionSnapshotsAsync(stageMission.Id, snapshots).ConfigureAwait(false);
                            }
                        }

                        lastMissionInGroup = stageMission.Id;
                        lastStageMissionId = stageMission.Id;
                    }

                    previousOrderLastMissionId = lastMissionInGroup;
                }

                    if (!String.IsNullOrEmpty(md.Alias) && lastStageMissionId != null)
                        aliasToMsnId[md.Alias] = lastStageMissionId;
                }

                voyage.Status = anyAssigned ? VoyageStatusEnum.InProgress : VoyageStatusEnum.Open;
                voyage.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
                await PipelineStageSkip.EmitSkippedEventsAsync(_Database, _Logging, voyage, skipResult, stageSkip).ConfigureAwait(false);
                await VoyagePipelineRecord.RecordAsync(_Database, _Logging, voyage, resolvedPipeline).ConfigureAwait(false);
                await VoyageDispatchedEvent.EmitAsync(_Database, _Logging, voyage, vesselId, sortedMissions.Count).ConfigureAwait(false);

                return voyage;
            }
            catch (Exception ex)
            {
                await VoyageCancellation.CancelVoyageAsync(
                    _Database,
                    voyage,
                    ex is FleetCapacityAdmissionException capacity
                        ? "Voyage cancelled: " + capacity.Code + "."
                        : "Voyage cancelled: initial mission graph creation failed.",
                    CancellationToken.None,
                    _Admiral.RecallCaptainAsync).ConfigureAwait(false);
                throw;
            }
        }

        private static List<SelectedPlaybook> ClonePlaybookSelectionsLocal(List<SelectedPlaybook>? selections)
        {
            if (selections == null || selections.Count == 0) return new List<SelectedPlaybook>();
            List<SelectedPlaybook> copy = new List<SelectedPlaybook>(selections.Count);
            foreach (SelectedPlaybook s in selections)
            {
                copy.Add(new SelectedPlaybook { PlaybookId = s.PlaybookId, DeliveryMode = s.DeliveryMode });
            }
            return copy;
        }

        private static List<PrestagedFile>? ClonePrestagedFilesLocal(List<PrestagedFile>? entries)
        {
            if (entries == null || entries.Count == 0) return null;
            List<PrestagedFile> copy = new List<PrestagedFile>(entries.Count);
            foreach (PrestagedFile entry in entries)
            {
                if (entry == null) continue;
                copy.Add(new PrestagedFile(entry.SourcePath ?? "", entry.DestPath ?? "")
                {
                    Content = entry.Content,
                    ReadOnly = entry.ReadOnly
                });
            }
            return copy.Count > 0 ? copy : null;
        }

        private static LoggingModule CreateSilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        #endregion
    }
}
