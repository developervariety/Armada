namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Authorization;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Builds a read-only preview of the effective target, pipeline, verification, provisioning,
    /// captain, start-ref, and brief configuration for an objective dispatch.
    /// </summary>
    public sealed class ObjectiveDispatchPreviewService : IObjectiveDispatchPreviewService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly VesselReadinessService _VesselReadiness;
        private readonly IGitService _Git;
        private readonly ArmadaSettings _Settings;
        private readonly ICaptainExecutionEnvironmentProbe _ExecutionEnvironment;

        // The general-purpose tools a brief most often assumes; each is reported as available or missing.
        private static readonly string[] _DockProbeTools = { "git", "bash", "python3", "node", "npm", "dotnet", "rg", "jq", "curl" };

        // Deterministic-fact parsing. The preflight facts read only what the description already
        // states, so the patterns match the shapes an operator writes: a recover/ ref, a full commit
        // SHA, a path:line citation, and a backticked code identifier. Facts are best-effort and
        // bounded so the preview a scheduler runs never becomes expensive.
        private const int _MaxFactTokens = 12;
        private const int _MaxNamedAnchorMovePaths = 10;
        private static readonly Regex _RecoverRefPattern = new Regex(@"recover/[A-Za-z0-9._/-]+", RegexOptions.Compiled);
        private static readonly Regex _CommitShaPattern = new Regex(@"\b[0-9a-fA-F]{40}\b", RegexOptions.Compiled);
        private static readonly Regex _PathLinePattern = new Regex(@"([A-Za-z0-9_./-]+\.[A-Za-z0-9]+):\d+(?:-\d+)?", RegexOptions.Compiled);
        private static readonly Regex _CitedPathLinePattern = new Regex(@"([A-Za-z0-9_./-]+\.[A-Za-z0-9]+:\d+(?:-\d+)?)", RegexOptions.Compiled);
        private static readonly Regex _BacktickIdentifierPattern = new Regex(@"`([A-Za-z_][A-Za-z0-9_.]{4,})`", RegexOptions.Compiled);

        // An Armada record id: a short prefix, the eight-character sortable stamp, and optionally the random
        // tail. A brief cites such ids as records, never as code, so they are not searched for in the tree.
        private static readonly Regex _RecordIdPattern = new Regex(@"^(?<prefix>[a-z]{2,5})_[0-9a-z]{8}(?<tail>_[A-Za-z0-9]{4,})?$", RegexOptions.Compiled);
        private static readonly HashSet<string> _RecordIdPrefixes = ReadRecordIdPrefixes();

        #endregion

        #region Public-Members

        /// <summary>
        /// Issue code for a stage whose persona duty conflicts with the objective's non-goals or the dispatch's
        /// read-only mode. The message names the conflict and the rule that wins.
        /// </summary>
        public const string StageDutyConflictIssueCode = "stage_duty_conflict";

        /// <summary>
        /// Issue code for files the vessel's workflow profile declares as gate inputs that the planned missions do
        /// not name: an Architect stage that will plan the file lists, or operator mission descriptions that name
        /// none of them. The warning lists the paths and does not block dispatch.
        /// </summary>
        public const string GateInputsUnplannedIssueCode = "gate_inputs_unplanned";

        /// <summary>
        /// Issue code for a cited symbol that moved no more than <see cref="CitedLineRangeChecker.DriftToleranceLines"/>
        /// lines from its cited range. The citation names the right code, so question 1 passes; the warning gives the
        /// corrected line and does not block dispatch.
        /// </summary>
        public const string CitationDriftIssueCode = "preflight_citation_drift";

        /// <summary>
        /// Issue code for a bare cited file name that matches several tracked files when the cited lines hold in more
        /// than one of them. Question 1 passes; the warning lists the candidates so the text can name the full path.
        /// </summary>
        public const string CitationAmbiguousIssueCode = "preflight_citation_ambiguous";

        /// <summary>
        /// The D5 preflight text-half adapter, run after the deterministic preflight block to add
        /// model flags to the preview. Null leaves the preview fully deterministic, which is the
        /// operationally-off state; it is set after construction only when the live typed-decision
        /// client exists, so every construction site and test is unchanged by default.
        /// </summary>
        public PreflightTextAdapter? PreflightAdapter { get; set; }

        /// <summary>
        /// The D19 <c>stage_necessity</c> adapter, run after the deterministic pipeline resolution to
        /// list <c>stage_optional</c> warnings for non-Judge stages the model proposes the objective
        /// does not need. Null leaves the preview listing every stage, which is the operationally-off
        /// state; it is set after construction only when the live typed-decision client exists, so every
        /// construction site and test is unchanged by default. The adapter only ever ADDS warnings; it
        /// never removes a stage, and it never proposes the Judge.
        /// </summary>
        public TypedStageNecessityAdapter? StageNecessityAdapter { get; set; }

        /// <summary>
        /// The D26 <c>prior_art</c> adapter, run after the deterministic block to retrieve prior-art
        /// candidates and, in Gate mode, add an <c>objective_prior_art_found</c> Error issue when the
        /// deliverable already exists, an <c>objective_prior_art_integrate</c> advisory when a landed seam
        /// should be consumed, and a <c>prior_art_analyst_stage_recommended</c> advisory in the uncertain
        /// band on a large objective. Null leaves the preview without a prior-art check, which is the
        /// operationally-off state; it is set after construction only when the live typed-decision client
        /// and a retriever exist. The adapter only ever ADDS issues; it never closes or re-scopes a row.
        /// </summary>
        public TypedPriorArtAdapter? PriorArtAdapter { get; set; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the objective dispatch preview service.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="workflowProfiles">Workflow profile resolver.</param>
        /// <param name="vesselReadiness">Vessel and workflow readiness service.</param>
        /// <param name="git">Git service used only for revision resolution.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="executionEnvironment">Read-only probe of the captain execution environment; null probes the local environment captains launch in.</param>
        public ObjectiveDispatchPreviewService(
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles,
            VesselReadinessService vesselReadiness,
            IGitService git,
            ArmadaSettings settings,
            ICaptainExecutionEnvironmentProbe? executionEnvironment = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
            _VesselReadiness = vesselReadiness ?? throw new ArgumentNullException(nameof(vesselReadiness));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ExecutionEnvironment = executionEnvironment ?? new LocalCaptainExecutionEnvironmentProbe(settings);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Preview one objective without creating a voyage, mission, branch, dock, Check, or lease.
        /// Optional dispatch values let operator paths preview their effective request while the
        /// autonomous path uses the objective's own values.
        /// </summary>
        /// <param name="auth">Authenticated caller context.</param>
        /// <param name="objective">Objective to preview.</param>
        /// <param name="requestedVesselId">Optional operator-selected target vessel.</param>
        /// <param name="requestedPipelineId">Optional operator-selected pipeline ID or name.</param>
        /// <param name="captainAssignments">Optional operator-selected persona assignments.</param>
        /// <param name="missionDescriptions">Optional operator mission descriptions with effective mode and start-ref values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Complete dispatch preview.</returns>
        public async Task<ObjectiveDispatchPreview> PreviewAsync(
            AuthContext auth,
            Objective objective,
            string? requestedVesselId = null,
            string? requestedPipelineId = null,
            IReadOnlyList<CaptainAssignmentOverride>? captainAssignments = null,
            IReadOnlyList<MissionDescription>? missionDescriptions = null,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (objective == null) throw new ArgumentNullException(nameof(objective));

            ObjectiveDispatchPreview result = new ObjectiveDispatchPreview
            {
                ObjectiveId = objective.Id,
                DeliverableMode = MissionModes.FromObjectiveKind(objective.Kind) ?? MissionModeEnum.Implementation.ToString(),
                StartFromRef = NormalizeEmpty(objective.StartFromRef),
                RenderedBrief = ObjectiveBriefRenderer.Render(objective)
            };

            EvaluateBrief(objective, result);
            EvaluatePreflightAnswers(objective, result);
            List<Objective> objectiveSnapshot = await ReadObjectivesAsync(auth, token).ConfigureAwait(false);
            ObjectiveDependencyAnalysis dependencies = ObjectiveDependencyAnalyzer.Analyze(objective, objectiveSnapshot);
            result.DependencyAnalysis = dependencies;
            result.BlockingChains = dependencies.BlockingChains
                .Select(chain => chain.ObjectiveIds.ToList())
                .ToList();
            if (dependencies.HasCycle)
            {
                AddIssue(result, "objective_dependency_cycle", "admission", ReadinessSeverityEnum.Error,
                    "The objective dependency graph contains a cycle: " + String.Join(" -> ", dependencies.CyclePath) + ".",
                    objective.Id);
            }
            else if (dependencies.BlockingNodes.Any(node => node.IsMissing))
            {
                // The snapshot leaves out a row whose stored data cannot be read, so such a blocker looks
                // missing. A read by id raises the stored-data error instead, which names the field to repair;
                // the blocker still blocks, because its state is unknown.
                List<string> unreadable = new List<string>();
                bool anyAbsent = false;
                foreach (ObjectiveDependencyNode node in dependencies.BlockingNodes.Where(node => node.IsMissing))
                {
                    string? problem = await ReadStoredDataProblemAsync(auth, node.ObjectiveId, token).ConfigureAwait(false);
                    if (problem != null) unreadable.Add(problem);
                    else anyAbsent = true;
                }

                if (unreadable.Count > 0)
                {
                    AddIssue(result, "objective_dependency_unreadable", "admission", ReadinessSeverityEnum.Error,
                        "One or more objective dependencies exist but their stored data cannot be read, so their state is unknown: "
                        + String.Join("; ", unreadable) + ".", objective.Id);
                }
                if (anyAbsent)
                {
                    AddIssue(result, "objective_dependency_missing", "admission", ReadinessSeverityEnum.Error,
                        "One or more objective dependencies do not exist or are not accessible.", objective.Id);
                }
            }
            else if (!dependencies.IsDependencyReady)
            {
                AddIssue(result, "objective_dependencies_incomplete", "admission", ReadinessSeverityEnum.Error,
                    "One or more objective dependency chains are not complete.", objective.Id);
            }

            string? vesselId = ResolveTargetVesselId(objective, requestedVesselId, result);
            result.VesselId = vesselId;
            if (vesselId == null)
            {
                FinalizeResult(result);
                return result;
            }

            Vessel? vessel = await ReadVesselAsync(auth, vesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                AddIssue(result, "target_vessel_not_found", "target", ReadinessSeverityEnum.Error,
                    "The target vessel does not exist or is not accessible.", vesselId);
                FinalizeResult(result);
                return result;
            }

            await EvaluateRepositoryContextAsync(vessel, result, token).ConfigureAwait(false);
            await EvaluateStartRefAsync(vessel, result, token).ConfigureAwait(false);
            await EvaluatePreflightFactsAsync(auth, objective, vessel, result, token).ConfigureAwait(false);
            List<string> effectiveMissionModes = await EvaluateMissionDescriptionsAsync(
                vessel, objective, missionDescriptions, result, token).ConfigureAwait(false);
            await EvaluatePreparationAnchorsAsync(auth, objective, result, token).ConfigureAwait(false);
            await EvaluateSiblingProvisioningAsync(auth, objective, vessel, result, token).ConfigureAwait(false);
            result.Issues.AddRange(ObjectiveExecutionRequirementsValidator.Evaluate(
                objective.Preparation?.ExecutionRequirements,
                _ExecutionEnvironment));

            string? effectivePipelineRequest = NormalizeEmpty(requestedPipelineId)
                ?? NormalizeEmpty(objective.SuggestedPipelineId);
            bool readOnlyDispatch = effectiveMissionModes.Count > 0
                ? effectiveMissionModes.All(IsReadOnlyMissionMode)
                : objective.Kind == ObjectiveKindEnum.Research;
            Pipeline? pipeline = await ResolvePipelineReadOnlyAsync(
                auth,
                vessel,
                effectivePipelineRequest,
                readOnlyDispatch,
                result,
                token).ConfigureAwait(false);
            if (pipeline != null)
            {
                result.PipelineId = pipeline.Id;
                result.PipelineName = pipeline.Name;
            }
            else if (!result.Issues.Any(issue => issue.Area == "pipeline" && issue.Severity == ReadinessSeverityEnum.Error))
            {
                result.PipelineName = "WorkerOnly";
            }

            List<Captain> captains = await ReadCaptainsAsync(auth, token).ConfigureAwait(false);
            Pipeline? coveragePipeline = ApplyStoredStageSkip(objective, pipeline, result);
            EvaluateStageDutyConflicts(objective, readOnlyDispatch, result);
            await EvaluateCaptainCoverageAsync(auth, vessel, coveragePipeline, captains, captainAssignments, missionDescriptions, result, token).ConfigureAwait(false);
            await EvaluateChecksAsync(auth, vessel, result, token).ConfigureAwait(false);
            await EvaluateGateInputsAsync(auth, objective, vessel, missionDescriptions, result, token).ConfigureAwait(false);

            // D5 preflight text half. Runs LAST, after the deterministic block computed the facts and
            // resolved the pipeline, so its state carries both. The model only adds issues; the
            // deterministic issues already in the preview stand regardless of what the model returns.
            if (PreflightAdapter != null)
                await PreflightAdapter.EvaluateAsync(objective, vessel, pipeline, result, token, BuildDockContext(objective, vessel)).ConfigureAwait(false);

            // D19 stage necessity. Runs on the resolved pipeline stages and lists a stage_optional
            // warning per non-Judge stage the model proposes the objective does not need. It only adds
            // warnings the operator confirms; the deterministic pipeline (every stage) stands. The Judge
            // is never proposed.
            if (StageNecessityAdapter != null)
                await RefineStageNecessityAsync(objective, vessel, pipeline, result, token).ConfigureAwait(false);

            // D26 prior art. Retrieves prior-art candidates for the objective and, in Gate mode, adds an
            // Error issue when the deliverable already exists, an advisory to consume a landed seam, and
            // an advisory to consider a read-only analyst stage in the uncertain band. The adapter is
            // contracted never to throw into the preview: Off and every fault path leave it unchanged.
            if (PriorArtAdapter != null)
                await PriorArtAdapter.EvaluatePreflightAsync(objective, vessel, result, token).ConfigureAwait(false);

            FinalizeResult(result);
            return result;
        }

        #endregion

        #region Private-Methods

        private static void EvaluateBrief(Objective objective, ObjectiveDispatchPreview result)
        {
            if (String.IsNullOrWhiteSpace(objective.Description))
                AddIssue(result, "brief_scope_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective description must state the dispatch scope.", null);

            if (objective.AcceptanceCriteria == null || !objective.AcceptanceCriteria.Any(item => !String.IsNullOrWhiteSpace(item)))
                AddIssue(result, "brief_acceptance_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective must state at least one acceptance criterion.", null);

            List<string> conflictingIds = ObjectiveTextLint.FindConflictingObjectiveIds(objective);
            if (conflictingIds.Count > 0)
                AddIssue(result, "objective_text_conflicting_ids", "brief", ReadinessSeverityEnum.Warning,
                    "The stored text names an objective other than this one (`" + objective.Id + "`) or its parent ("
                    + (String.IsNullOrWhiteSpace(objective.ParentObjectiveId) ? "none" : "`" + objective.ParentObjectiveId + "`")
                    + ") as current, parent or authorizing; reword or remove the superseded sentence: "
                    + String.Join(" | ", conflictingIds), null);

            List<string> conflictingCounts = ObjectiveTextLint.FindConflictingCriterionCounts(objective);
            if (conflictingCounts.Count > 0)
                AddIssue(result, "objective_text_conflicting_criterion_counts", "brief", ReadinessSeverityEnum.Warning,
                    "The stored text states criterion counts that disagree with each other or with the "
                    + (objective.AcceptanceCriteria ?? new List<string>()).Count(item => !String.IsNullOrWhiteSpace(item))
                    + " acceptance criteria; reword or remove the superseded sentence: "
                    + String.Join(" | ", conflictingCounts), null);

            ObjectivePreparation preparation = objective.Preparation ?? new ObjectivePreparation();
            List<ObjectivePreparationClaim> claims = preparation.Claims ?? new List<ObjectivePreparationClaim>();
            if (preparation.RequiredForDispatch)
            {
                if (preparation.Source == null)
                    AddIssue(result, "preparation_source_required", "brief", ReadinessSeverityEnum.Error,
                        "Required preparation must include an immutable source anchor.", null);
                if (preparation.Target == null)
                    AddIssue(result, "preparation_target_required", "brief", ReadinessSeverityEnum.Error,
                        "Required preparation must include an immutable target anchor.", null);

                if (preparation.RequiredClaimKinds == null || preparation.RequiredClaimKinds.Count == 0)
                    AddIssue(result, "preparation_required_kinds_missing", "brief", ReadinessSeverityEnum.Error,
                        "Required preparation must name at least one required claim kind.", null);

                foreach (ObjectivePreparationClaimKindEnum kind in preparation.RequiredClaimKinds ?? new List<ObjectivePreparationClaimKindEnum>())
                {
                    bool present = claims.Any(claim => claim != null
                        && claim.Kind == kind
                        && claim.State == ObjectivePreparationClaimStateEnum.Verified
                        && claim.VerifiedUtc.HasValue
                        && claim.EvidenceLinks != null
                        && claim.EvidenceLinks.Any(link => !String.IsNullOrWhiteSpace(link)));
                    if (!present)
                        AddIssue(result, "preparation_required_kind_missing", "brief", ReadinessSeverityEnum.Error,
                            "Required preparation has no current evidence-backed " + kind + " claim.", kind.ToString());
                }

                foreach (ObjectivePreparationClaim claim in claims.Where(item => item != null))
                {
                    if (!claim.VerifiedUtc.HasValue)
                        AddIssue(result, "preparation_claim_verification_time_missing", "brief", ReadinessSeverityEnum.Error,
                            "A required preparation claim has no verification time: " + claim.Text, claim.Id);
                    if (claim.EvidenceLinks == null || !claim.EvidenceLinks.Any(link => !String.IsNullOrWhiteSpace(link)))
                        AddIssue(result, "preparation_claim_evidence_missing", "brief", ReadinessSeverityEnum.Error,
                            "A required preparation claim has no evidence: " + claim.Text, claim.Id);
                }
            }
            bool hasMethod = !String.IsNullOrWhiteSpace(objective.RefinementSummary)
                || claims.Any(claim => claim != null
                    && claim.State == ObjectivePreparationClaimStateEnum.Verified
                    && !String.IsNullOrWhiteSpace(claim.Text)
                    && (claim.Kind == ObjectivePreparationClaimKindEnum.DispatchEntryPoint
                        || claim.Kind == ObjectivePreparationClaimKindEnum.ReuseType
                        || claim.Kind == ObjectivePreparationClaimKindEnum.ResponseRule));
            if (!hasMethod)
                AddIssue(result, "brief_method_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective must state an implementation method in its refinement summary or verified preparation.", null);

            foreach (ObjectivePreparationClaim claim in claims.Where(item => item != null
                && item.State == ObjectivePreparationClaimStateEnum.NeedsRecheck))
            {
                AddIssue(result, "preparation_claim_needs_recheck", "brief", ReadinessSeverityEnum.Error,
                    "A preparation claim must be verified again before dispatch: " + claim.Text, claim.Id);
            }
        }

        /// <summary>
        /// Flag each stage whose persona duty conflicts with the objective's non-goals or the dispatch's read-only
        /// mode, and record which rule wins. The mission brief states the same rule, so the warning tells the
        /// operator what each captain will be told; a conflict with a stated winner does not block dispatch.
        /// </summary>
        private static void EvaluateStageDutyConflicts(Objective objective, bool readOnlyDispatch, ObjectiveDispatchPreview result)
        {
            List<string> conflicts = MissionService.FindStageDutyConflicts(
                readOnlyDispatch,
                result.EffectivePipelineStages,
                objective.AcceptanceCriteria,
                objective.NonGoals);
            foreach (string conflict in conflicts)
            {
                AddIssue(result, StageDutyConflictIssueCode, "brief", ReadinessSeverityEnum.Warning, conflict, null);
            }
        }

        /// <summary>
        /// Apply the dispatch-preflight rule to the objective's recorded answers. Every question must
        /// be answered in a way that admits dispatch; an objective with no recorded answers has every
        /// question unanswered and is refused. The blocking rule itself lives in one place
        /// (<see cref="ObjectivePreflightEvaluator"/>) so this gate, the scheduler, and operator dispatch
        /// cannot disagree.
        /// </summary>
        private static void EvaluatePreflightAnswers(Objective objective, ObjectiveDispatchPreview result)
        {
            ObjectivePreflight preflight = objective.Preparation?.Preflight ?? new ObjectivePreflight();
            IReadOnlyList<int> blocking = ObjectivePreflightEvaluator.BlockingQuestions(preflight);
            result.Preflight.IncompleteQuestions = blocking.ToList();
            result.Preflight.IsComplete = blocking.Count == 0;
            if (blocking.Count > 0)
            {
                string numbers = String.Join(", ", blocking);
                AddIssue(result, ObjectivePreflightGate.IssueCode, "preflight", ReadinessSeverityEnum.Error,
                    "The dispatch preflight is incomplete; record an admitting answer for question(s) " + numbers + ".",
                    numbers);
            }
        }

        /// <summary>
        /// Compute the deterministic preflight facts the code can settle on its own and attach them to
        /// the preview. Each fact is informational: it lets an operator check a recorded answer against
        /// the repository and never blocks dispatch by itself. A fact the repository cannot settle is
        /// reported Unknown rather than guessed, and a git error never fails the preview.
        /// </summary>
        private async Task EvaluatePreflightFactsAsync(
            AuthContext auth,
            Objective objective,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            ObjectivePreflight preflight = objective.Preparation?.Preflight ?? new ObjectivePreflight();
            string repositoryPath = RepositoryPath(vessel);
            string targetRevision = !String.IsNullOrWhiteSpace(result.ResolvedStartCommit)
                ? result.ResolvedStartCommit!
                : "HEAD";
            string description = objective.Description ?? String.Empty;

            result.Preflight.Facts.Add(BuildVesselCountFact(objective, preflight));
            result.Preflight.Facts.Add(BuildDeliverableKindFact(objective, preflight, description));
            result.Preflight.Facts.Add(await BuildRecoverRefFact(objective, preflight, repositoryPath, description, token).ConfigureAwait(false));
            result.Preflight.Facts.Add(await BuildSiblingTipFact(auth, vessel, preflight, description, token).ConfigureAwait(false));
            List<string> drifts = new List<string>();
            List<string> ambiguities = new List<string>();
            CitationBranchRevision? branchRevision = await ResolveCitationBranchRevisionAsync(
                vessel, repositoryPath, result.ResolvedStartCommit, token).ConfigureAwait(false);
            string readLabel = targetRevision;
            if (String.Equals(targetRevision, "HEAD", StringComparison.Ordinal))
            {
                string? headCommit = NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(repositoryPath, "HEAD", token).ConfigureAwait(false));
                if (headCommit != null) readLabel = headCommit;
            }
            result.Preflight.Facts.Add(await BuildCitationResolvesFact(auth, objective, vessel, preflight, repositoryPath,
                targetRevision, readLabel, branchRevision, description, drifts, ambiguities, token).ConfigureAwait(false));
            if (ambiguities.Count > 0)
            {
                AddIssue(result, CitationAmbiguousIssueCode, "preflight", ReadinessSeverityEnum.Warning,
                    "These cited file names match several files and the cited lines hold in more than one: " + String.Join(" ", ambiguities),
                    "q1");
            }
            if (drifts.Count > 0)
            {
                AddIssue(result, CitationDriftIssueCode, "preflight", ReadinessSeverityEnum.Warning,
                    "These cited symbols moved by a few lines; correct the line numbers: " + String.Join(" ", drifts),
                    "q1");
            }
        }

        private static ObjectiveDispatchPreflightFact BuildVesselCountFact(Objective objective, ObjectivePreflight preflight)
        {
            const int question = 3;
            int count = (objective.VesselIds ?? new List<string>())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                Status = count == 1 ? PreflightFactStatusEnum.Pass : PreflightFactStatusEnum.Fail,
                Detail = "The objective names " + count + " target vessel(s); dispatch needs exactly one.",
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };
        }

        private static ObjectiveDispatchPreflightFact BuildDeliverableKindFact(
            Objective objective, ObjectivePreflight preflight, string description)
        {
            const int question = 2;
            string[] reportWords = { "deliver a report", "deliverable is a report", "report only, no code", "a report", "produce a report", "findings report", "report to the owner" };
            string[] committedWords = { "commit a", "commit the", "committed doc", "committed document", "committed test", "census", "ledger row", "discoveries.d", "write a file", "record a file" };
            string committedPattern = @"\b(?:" + String.Join("|", committedWords.Select(Regex.Escape)) + ")";
            string described = Regex.Replace(description.ToLowerInvariant(),
                @"\b(?:no|without)\s+committed\s+tests?\b|\b(?:not|never)\s+(?:(?:add|write|deliver|produce|create|include|require|commit)\s+)?(?:any\s+)?committed\s+tests?\b",
                String.Empty, RegexOptions.CultureInvariant);
            // Separate optional document work after review is not the current delivery contract.
            // A current commit duty before that condition must still participate in classification.
            string lower = String.Join(" ", Regex.Split(described, @"(?<=[.!?;])\s+|\r?\n")
                .Where(statement => Regex.IsMatch(statement.Split(',', 2)[0], committedPattern, RegexOptions.CultureInvariant)
                    || !Regex.IsMatch(statement.Trim(),
                        @"^[^,;.!?\r\n]+\b(?:writes|updates|changes),\s*if\s+needed,\s+(?:is|are)\s+separate\s+committed\s+document\s+work\s+after\s+review[.!]?$",
                        RegexOptions.CultureInvariant)));
            // A negated report deliverable does not cancel a later positive report obligation.
            string positiveReport = Regex.Replace(lower, @"\b(?:not|never)\s+(?:a\s+)?report(?:[- ]only)?(?:\s+audit)?\b",
                String.Empty, RegexOptions.CultureInvariant);
            bool report = reportWords.Any(word => positiveReport.Contains(word));
            bool committed = Regex.IsMatch(lower, committedPattern, RegexOptions.CultureInvariant);

            PreflightFactStatusEnum status;
            string detail;
            if (report && !committed)
            {
                status = objective.Kind == ObjectiveKindEnum.Research ? PreflightFactStatusEnum.Pass : PreflightFactStatusEnum.Fail;
                detail = "The description reads report-only; a report deliverable is Kind Research (Kind is " + objective.Kind + ").";
            }
            else if (committed && !report)
            {
                status = objective.Kind == ObjectiveKindEnum.Research ? PreflightFactStatusEnum.Fail : PreflightFactStatusEnum.Pass;
                detail = "The description requires committed work; a committed deliverable must not be Kind Research (Kind is " + objective.Kind + ").";
            }
            else
            {
                status = PreflightFactStatusEnum.Unknown;
                detail = "The description does not clearly indicate report-only or committed delivery (Kind is " + objective.Kind + ").";
            }

            return new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                Status = status,
                Detail = detail,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };
        }

        private async Task<ObjectiveDispatchPreflightFact> BuildRecoverRefFact(
            Objective objective, ObjectivePreflight preflight, string repositoryPath, string description, CancellationToken token)
        {
            const int question = 10;
            ObjectiveDispatchPreflightFact fact = new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };

            List<string> refs = MatchAll(description, _RecoverRefPattern).Distinct(StringComparer.Ordinal).ToList();
            if (refs.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The description names no recover/ ref.";
                return fact;
            }

            string rowText = description + " " + String.Join(" ", objective.EvidenceLinks ?? new List<string>());
            bool hasRecordedSha = _CommitShaPattern.IsMatch(rowText);
            List<string> unresolved = new List<string>();
            try
            {
                foreach (string reference in refs.Take(_MaxFactTokens))
                {
                    string? sha = NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(repositoryPath, reference, token).ConfigureAwait(false));
                    if (sha == null) unresolved.Add(reference);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The recover/ refs could not be resolved: " + ex.Message;
                return fact;
            }

            if (unresolved.Count == 0 && hasRecordedSha)
            {
                fact.Status = PreflightFactStatusEnum.Pass;
                fact.Detail = "Every named recover/ ref resolves and a 40-character SHA is recorded on the row.";
            }
            else
            {
                fact.Status = PreflightFactStatusEnum.Fail;
                fact.Detail = unresolved.Count > 0
                    ? "These recover/ refs do not resolve in the vessel repository: " + String.Join(", ", unresolved) + "."
                    : "No 40-character SHA is recorded on the row for the named recover/ ref(s).";
            }
            return fact;
        }

        private async Task<ObjectiveDispatchPreflightFact> BuildSiblingTipFact(
            AuthContext auth, Vessel vessel, ObjectivePreflight preflight, string description, CancellationToken token)
        {
            const int question = 11;
            ObjectiveDispatchPreflightFact fact = new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };

            List<SiblingRepo> siblings = (vessel.GetSiblingRepos() ?? new List<SiblingRepo>())
                .Where(sibling => sibling != null && !String.IsNullOrWhiteSpace(sibling.VesselRef))
                .ToList();
            List<string> citedCommits = MatchAll(description, _CommitShaPattern).Distinct(StringComparer.OrdinalIgnoreCase).Take(_MaxFactTokens).ToList();
            if (siblings.Count == 0 || citedCommits.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "There is no declared sibling and cited commit to compare.";
                return fact;
            }

            bool anyFail = false;
            bool anyUnknown = false;
            bool anyCompared = false;
            try
            {
                foreach (SiblingRepo sibling in siblings)
                {
                    Vessel? siblingVessel = await ResolveVesselReferenceAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
                    if (siblingVessel == null) { anyUnknown = true; continue; }
                    string siblingRepo = RepositoryPath(siblingVessel);
                    string? siblingTip = NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(siblingRepo, "HEAD", token).ConfigureAwait(false));
                    if (siblingTip == null) { anyUnknown = true; continue; }
                    foreach (string commit in citedCommits)
                    {
                        // A cited commit is judged against a sibling only when it is that sibling's commit.
                        // A brief cites the vessel's own commits too (a start commit, a preserved tip), and
                        // an ancestry probe of a commit the sibling does not hold answers "no", which read
                        // as a stale sibling for every continuation.
                        if (NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(siblingRepo, commit, token).ConfigureAwait(false)) == null)
                            continue;
                        anyCompared = true;
                        bool? ancestor = await _Git.TryIsAncestorAsync(siblingRepo, commit, siblingTip, token).ConfigureAwait(false);
                        if (ancestor == false) anyFail = true;
                        else if (ancestor == null) anyUnknown = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The sibling tips could not be compared: " + ex.Message;
                return fact;
            }

            if (anyFail)
            {
                fact.Status = PreflightFactStatusEnum.Fail;
                fact.Detail = "A declared sibling tip is behind a commit the description cites (dock.sibling_stale).";
            }
            else if (!anyCompared && !anyUnknown)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "No commit the description cites belongs to a declared sibling, so there is no sibling tip to compare.";
            }
            else if (anyUnknown)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The sibling tip ancestry could not be established for every cited commit.";
            }
            else
            {
                fact.Status = PreflightFactStatusEnum.Pass;
                fact.Detail = "Every declared sibling tip is at or after each commit the description cites.";
            }
            return fact;
        }

        private async Task<ObjectiveDispatchPreflightFact> BuildCitationResolvesFact(
            AuthContext auth,
            Objective objective,
            Vessel vessel,
            ObjectivePreflight preflight,
            string repositoryPath,
            string targetRevision,
            string readLabel,
            CitationBranchRevision? branchRevision,
            string description,
            List<string> drifts,
            List<string> ambiguities,
            CancellationToken token)
        {
            const int question = 1;
            ObjectiveDispatchPreflightFact fact = new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };

            List<string> paths = MatchAll(description, _PathLinePattern).Distinct(StringComparer.OrdinalIgnoreCase).Take(_MaxFactTokens).ToList();
            List<string> identifiers = MatchAll(description, _BacktickIdentifierPattern)
                .Where(identifier => !IsRecordId(identifier))
                .Distinct(StringComparer.Ordinal).Take(_MaxFactTokens).ToList();
            if (paths.Count == 0 && identifiers.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The description cites no path:line or backticked identifier to resolve.";
                return fact;
            }

            List<CitedLineRange> ranges = CitedLineRangeChecker.Parse(description);
            List<CitationReading> readings;
            try
            {
                readings = await ReadCitationsAsync(auth, vessel, repositoryPath, targetRevision, description, paths, identifiers, ranges, token).ConfigureAwait(false);

                // A start ref off the target branch (a recovery start on a mission branch whose brief merges the
                // branch first) cites files as the branch holds them, so a citation that fails at the start ref is
                // read again at the branch tip, and it passes when it resolves there.
                List<CitationReading> failing = readings.Where(reading => reading.Issues.Count > 0).ToList();
                if (branchRevision != null && failing.Count > 0)
                {
                    List<CitationReading> atBranch = await ReadCitationsAsync(auth, vessel, repositoryPath, branchRevision.Commit, description,
                        failing.Where(reading => reading.Path != null).Select(reading => reading.Path!).ToList(),
                        failing.Where(reading => reading.Identifier != null).Select(reading => reading.Identifier!).ToList(),
                        ranges, token).ConfigureAwait(false);
                    for (int i = 0; i < readings.Count; i++)
                    {
                        CitationReading primary = readings[i];
                        if (primary.Issues.Count == 0) continue;
                        CitationReading? second = atBranch.FirstOrDefault(reading => reading.Key == primary.Key);
                        if (second == null || second.Issues.Count > 0) continue;
                        second.Notes.Insert(0, "`" + primary.Name + "` resolves at branch " + branchRevision.Branch + " ("
                            + ShortCommit(branchRevision.Commit) + "), not at the start ref " + ShortCommit(targetRevision) + ".");
                        readings[i] = second;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The cited references could not be resolved at the target tip: " + ex.Message;
                return fact;
            }

            List<string> issues = readings.SelectMany(reading => reading.Issues).ToList();
            List<string> notes = readings.SelectMany(reading => reading.Notes).ToList();
            drifts.AddRange(readings.SelectMany(reading => reading.Drifts));
            ambiguities.AddRange(readings.SelectMany(reading => reading.Ambiguities));
            // Issues raised for citations outside the target repository are counted apart, so the fact can say
            // whether every failure lies outside it.
            int outsideIssues = readings.Where(reading => reading.Outside).Sum(reading => reading.Issues.Count);

            if (issues.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Pass;
                List<string> parts = new List<string> { "Every cited path:line and backticked identifier resolves at the target tip (" + RevisionLabel(readLabel) + ")." };
                parts.AddRange(notes);
                if (drifts.Count > 0) parts.Add(drifts.Count + " cited symbol(s) moved by a few lines (" + CitationDriftIssueCode + ").");
                if (ambiguities.Count > 0) parts.Add(ambiguities.Count + " cited file name(s) are ambiguous (" + CitationAmbiguousIssueCode + ").");
                fact.Detail = String.Join(" ", parts);
            }
            else
            {
                fact.Status = PreflightFactStatusEnum.Fail;
                fact.FailsOnlyOutsideTarget = outsideIssues == issues.Count;
                List<string> parts = new List<string>(issues);
                parts.Add(branchRevision != null
                    ? "Read at the start ref " + ShortCommit(targetRevision) + " and at branch " + branchRevision.Branch
                        + " (" + ShortCommit(branchRevision.Commit) + ")."
                    : "Read at " + RevisionLabel(readLabel) + ".");
                fact.Detail = String.Join(" ", parts);
            }
            return fact;
        }

        // Names the revision a citation was read at: a commit by its short id, the repository HEAD by name.
        private static string RevisionLabel(string revision)
        {
            return IsFullCommit(revision) ? "commit " + ShortCommit(revision) : revision;
        }

        // The target branch tip to read citations at as well, when the start commit is not on that branch.
        private async Task<CitationBranchRevision?> ResolveCitationBranchRevisionAsync(
            Vessel vessel,
            string repositoryPath,
            string? startCommit,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(startCommit)) return null;
            string branch = !String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? vessel.DefaultBranch : "main";
            try
            {
                string? tip = NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(repositoryPath, "refs/heads/" + branch, token).ConfigureAwait(false));
                if (tip == null || String.Equals(tip, startCommit, StringComparison.OrdinalIgnoreCase)) return null;
                bool? onBranch = await _Git.TryIsAncestorAsync(repositoryPath, startCommit!, tip, token).ConfigureAwait(false);
                if (onBranch != false) return null;
                return new CitationBranchRevision { Branch = branch, Commit = tip };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        // Reads each cited path (with its cited line ranges) and each cited identifier at one revision, one
        // reading per citation, so a citation can be judged at a second revision on its own.
        private async Task<List<CitationReading>> ReadCitationsAsync(
            AuthContext auth,
            Vessel vessel,
            string repositoryPath,
            string revision,
            string description,
            List<string> paths,
            List<string> identifiers,
            List<CitedLineRange> ranges,
            CancellationToken token)
        {
            List<CitationReading> readings = new List<CitationReading>();
            Dictionary<string, string> trackedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, CitationReading> byPath = new Dictionary<string, CitationReading>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                CitationReading reading = new CitationReading { Key = "path:" + path, Name = path, Path = path };
                readings.Add(reading);
                byPath[path] = reading;
                if (path.StartsWith("../", StringComparison.Ordinal))
                {
                    OutsideCitation parent = await ResolveOutsideTargetAsync(auth, vessel, path,
                        ranges.Where(range => range.Path == path).ToList(), token).ConfigureAwait(false);
                    if (parent.FoundAt != null)
                    {
                        reading.Notes.Add("`" + path + "` resolves in " + parent.RootLabel + ".");
                    }
                    else if (parent.AmbiguousCandidates.Count > 0)
                    {
                        AddSiblingAmbiguity(reading, description, path, parent);
                    }
                    else
                    {
                        AddUnresolved(reading.Issues, description, path, parent.NamesRoot
                            ? "file missing in " + parent.RootLabel
                            : "file missing; no declared sibling checkout holds this path");
                        reading.Outside = true;
                    }
                    continue;
                }

                bool exists = await _Git.PathExistsOnRevisionAsync(repositoryPath, revision, path, token).ConfigureAwait(false);
                if (exists)
                {
                    trackedPaths[path] = path;
                    continue;
                }

                string? suffix = await _Git.ResolveTrackedPathSuffixAsync(repositoryPath, revision, path, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(suffix))
                {
                    List<string> candidates = await _Git.ListTrackedPathCandidatesAsync(
                        repositoryPath, revision, path, token).ConfigureAwait(false);
                    if (candidates.Count < 2)
                    {
                        // A path the target does not track may name a declared sibling checkout or evidence
                        // root, the same trees a dock provisions beside the repository.
                        OutsideCitation outside = await ResolveOutsideTargetAsync(auth, vessel, path,
                            ranges.Where(range => range.Path == path).ToList(), token).ConfigureAwait(false);
                        if (outside.FoundAt != null)
                        {
                            reading.Notes.Add("`" + path + "` resolves in " + outside.RootLabel + " as `" + outside.FoundAt + "`.");
                        }
                        else if (outside.AmbiguousCandidates.Count > 0)
                        {
                            AddSiblingAmbiguity(reading, description, path, outside);
                        }
                        else if (outside.NamesRoot)
                        {
                            AddUnresolved(reading.Issues, description, path, "file missing in " + outside.RootLabel);
                            reading.Outside = true;
                        }
                        else
                        {
                            AddUnresolved(reading.Issues, description, path, "file missing");
                        }
                        continue;
                    }

                    // A bare name that matches several files means the one whose lines hold the citations.
                    List<CitedLineRange> cited = ranges.Where(range => range.Path == path).ToList();
                    List<string> holding = await CitedLineRangeChecker.CandidatesHoldingAsync(cited, candidates,
                        candidate => _Git.ReadFileLinesOnRevisionAsync(repositoryPath, revision, candidate, token)).ConfigureAwait(false);
                    string listed = String.Join(", ", candidates.Select(candidate => "`" + candidate + "`"));
                    if (holding.Count == 1)
                    {
                        trackedPaths[path] = holding[0];
                        reading.Notes.Add("`" + path + "` names `" + holding[0] + "`, the one of " + candidates.Count + " matching files whose lines hold the citation.");
                    }
                    else if (holding.Count > 1)
                    {
                        reading.Ambiguities.Add("`" + path + "` is an ambiguous file name: " + String.Join(", ", holding.Select(candidate => "`" + candidate + "`")) + ".");
                    }
                    else
                    {
                        AddUnresolved(reading.Issues, description, path, "ambiguous file name, and no candidate holds the cited lines (" + listed + ")");
                    }
                    continue;
                }

                trackedPaths[path] = suffix!;
                // A suffix names the tracked path as a reader says it; a different spelling does not.
                if (!suffix!.Equals(path, StringComparison.Ordinal) && !suffix.EndsWith("/" + path, StringComparison.Ordinal))
                    reading.Issues.Add("`" + path + "` is tracked as `" + suffix + "`.");
            }
            foreach (string identifier in identifiers)
            {
                CitationReading reading = new CitationReading { Key = "identifier:" + identifier, Name = identifier, Identifier = identifier };
                readings.Add(reading);
                GitAnchorPriorArt search = await _Git.SearchTrackedContentOnRevisionAsync(
                    repositoryPath, revision, identifier, 1, token).ConfigureAwait(false);
                if (!search.Found) reading.Issues.Add("`" + identifier + "` does not resolve: identifier not found.");
            }
            foreach (CitedLineRange range in ranges)
            {
                if (!byPath.TryGetValue(range.Path, out CitationReading? reading)) continue;
                if (!trackedPaths.TryGetValue(range.Path, out string? tracked)) continue;
                IReadOnlyList<string>? lines = await _Git.ReadFileLinesOnRevisionAsync(
                    repositoryPath, revision, tracked, token).ConfigureAwait(false);
                if (lines == null) continue;
                string? issue = CitedLineRangeChecker.Evaluate(range, lines, out int currentLine);
                if (issue == null) continue;
                if (GitLfsPointer.IsPointer(lines))
                {
                    reading.Notes.Add(issue);
                    continue;
                }
                if (CitedLineRangeChecker.IsSmallDrift(range, currentLine)) reading.Drifts.Add(issue);
                else reading.Issues.Add(issue + (range.StartLine > lines.Count ? " Reason: line out of range." : " Reason: outside the declaration span."));
            }
            return readings;
        }

        // A name that matched several files of a declared sibling is ambiguous: a warning when several hold the
        // cited lines, a failure naming the candidates when none does. Never a missing file.
        private static void AddSiblingAmbiguity(CitationReading reading, string description, string path, OutsideCitation outside)
        {
            string listed = String.Join(", ", outside.AmbiguousCandidates.Select(candidate => "`" + candidate + "`"));
            if (outside.NoCandidateHolds)
            {
                AddUnresolved(reading.Issues, description, path, "ambiguous file name in " + outside.RootLabel + ", and no candidate holds the cited lines (" + listed + ")");
                reading.Outside = true;
            }
            else
            {
                reading.Ambiguities.Add("`" + path + "` is an ambiguous file name in " + outside.RootLabel + ": " + listed + ".");
            }
        }

        // One cited path (with its cited line ranges) or identifier, as read at one revision.
        private sealed class CitationReading
        {
            public string Key { get; set; } = String.Empty;
            public string Name { get; set; } = String.Empty;
            public string? Path { get; set; } = null;
            public string? Identifier { get; set; } = null;
            public bool Outside { get; set; } = false;
            public List<string> Issues { get; } = new List<string>();
            public List<string> Notes { get; } = new List<string>();
            public List<string> Drifts { get; } = new List<string>();
            public List<string> Ambiguities { get; } = new List<string>();
        }

        // The target branch and its tip, read beside a start commit that is not on the branch.
        private sealed class CitationBranchRevision
        {
            public string Branch { get; set; } = String.Empty;
            public string Commit { get; set; } = String.Empty;
        }

        // Names each cited string for a path as written (file:line or file:start-end) with why it does not resolve.
        private static void AddUnresolved(List<string> issues, string description, string path, string reason)
        {
            List<string> cited = MatchAll(description, _CitedPathLinePattern)
                .Where(text => text.StartsWith(path + ":", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (cited.Count == 0) cited.Add(path);
            foreach (string text in cited) issues.Add("`" + text + "` does not resolve: " + reason + ".");
        }

        /// <summary>
        /// Where a citation the target repository does not track resolves among the trees a dock provisions beside
        /// it. A path that starts with <c>../</c>, or whose first segment is the directory name of a declared
        /// sibling checkout, is read inside that sibling at its verified tip (tracked files, then files under the
        /// sibling's declared extraction artifact paths in its working checkout). A path whose first segment is
        /// the directory name of an evidence root that applies to the vessel is read under that root. A bare file
        /// name is searched in every declared sibling. A path that would climb above the root it names never
        /// resolves.
        /// </summary>
        private async Task<OutsideCitation> ResolveOutsideTargetAsync(
            AuthContext auth,
            Vessel vessel,
            string citedPath,
            IReadOnlyList<CitedLineRange> ranges,
            CancellationToken token)
        {
            OutsideCitation result = new OutsideCitation();
            List<SiblingRepo> siblings = (vessel.GetSiblingRepos() ?? new List<SiblingRepo>())
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.VesselRef) && NormalizeRelativePath(item.RelativePath).Length > 0)
                .ToList();

            string? siblingPath = citedPath.StartsWith("../", StringComparison.Ordinal)
                ? citedPath
                : CitedPathPrefix.AsSiblingPath(citedPath, siblings);
            if (siblingPath != null)
            {
                string normalized = NormalizeRelativePath(siblingPath);
                foreach (SiblingRepo sibling in siblings)
                {
                    string declared = NormalizeRelativePath(sibling.RelativePath);
                    if (!normalized.StartsWith(declared + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    result.NamesRoot = true;
                    result.RootLabel = "declared sibling `" + declared + "`";
                    string relative = normalized.Substring(declared.Length + 1);
                    if (!IsContainedRelativePath(relative)) continue;
                    SiblingLookup lookup = await FindInSiblingAsync(auth, sibling, relative, ranges, token).ConfigureAwait(false);
                    if (lookup.Found == null)
                    {
                        RecordSiblingAmbiguity(result, lookup, declared);
                        continue;
                    }
                    result.FoundAt = declared + "/" + lookup.Found;
                    result.RootLabel = "declared sibling `" + declared + "`";
                    return result;
                }
                return result;
            }

            string[] segments = NormalizeRelativePath(citedPath).Split('/', 2);
            if (segments.Length == 2)
            {
                foreach (CaptainEvidenceRoot evidence in _Settings.CaptainEvidenceRoots)
                {
                    if (evidence == null || String.IsNullOrWhiteSpace(evidence.Path) || !evidence.AppliesTo(vessel.Id)) continue;
                    string name = Path.GetFileName(evidence.Path.TrimEnd('/', '\\'));
                    if (!String.Equals(name, segments[0], StringComparison.OrdinalIgnoreCase)) continue;
                    result.NamesRoot = true;
                    result.RootLabel = "evidence root `" + name + "`";
                    if (!Path.IsPathRooted(evidence.Path) || !IsContainedRelativePath(segments[1])) continue;
                    if (_ExecutionEnvironment.PathExists(Path.Combine(evidence.Path, segments[1].Replace('/', Path.DirectorySeparatorChar))))
                    {
                        result.FoundAt = name + "/" + segments[1];
                        return result;
                    }
                }
                return result;
            }

            if (CitedPathPrefix.IsBareName(citedPath))
            {
                foreach (SiblingRepo sibling in siblings)
                {
                    SiblingLookup lookup = await FindInSiblingAsync(auth, sibling, citedPath, ranges, token).ConfigureAwait(false);
                    string declared = NormalizeRelativePath(sibling.RelativePath);
                    if (lookup.Found == null)
                    {
                        RecordSiblingAmbiguity(result, lookup, declared);
                        continue;
                    }
                    result.FoundAt = declared + "/" + lookup.Found;
                    result.RootLabel = "declared sibling `" + declared + "`";
                    return result;
                }
            }
            return result;
        }

        // The sibling-relative path a citation names inside one declared sibling, or no path when the sibling does
        // not hold it or cannot be read at a verified tip. A name that matches several of the sibling's files
        // resolves by the same rule as in the target repository: the one file whose lines hold the citations, and
        // otherwise the candidates are reported as ambiguous, never as a missing file.
        private async Task<SiblingLookup> FindInSiblingAsync(
            AuthContext auth,
            SiblingRepo sibling,
            string relative,
            IReadOnlyList<CitedLineRange> ranges,
            CancellationToken token)
        {
            SiblingLookup lookup = new SiblingLookup();
            Vessel? siblingVessel = await ResolveVesselReferenceAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
            if (siblingVessel == null) return lookup;
            string repository = RepositoryPath(siblingVessel);
            string? tip = NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(repository, "HEAD", token).ConfigureAwait(false));
            if (tip == null) return lookup;
            if (await _Git.PathExistsOnRevisionAsync(repository, tip, relative, token).ConfigureAwait(false))
            {
                lookup.Found = relative;
                return lookup;
            }
            string? suffix = NormalizeEmpty(await _Git.ResolveTrackedPathSuffixAsync(repository, tip, relative, token).ConfigureAwait(false));
            if (suffix != null)
            {
                lookup.Found = suffix;
                return lookup;
            }

            List<string> candidates = await _Git.ListTrackedPathCandidatesAsync(repository, tip, relative, token).ConfigureAwait(false);
            if (candidates.Count >= 2)
            {
                List<string> holding = await CitedLineRangeChecker.CandidatesHoldingAsync(ranges, candidates,
                    candidate => _Git.ReadFileLinesOnRevisionAsync(repository, tip, candidate, token)).ConfigureAwait(false);
                if (holding.Count == 1)
                {
                    lookup.Found = holding[0];
                    return lookup;
                }
                lookup.NoCandidateHolds = holding.Count == 0;
                lookup.Candidates = holding.Count > 1 ? holding : candidates;
                return lookup;
            }

            // Extraction artifacts are untracked output a dock receives from the sibling's working checkout.
            string? workingDirectory = NormalizeEmpty(siblingVessel.WorkingDirectory);
            if (workingDirectory == null) return lookup;
            foreach (string artifact in sibling.ExtractionArtifactPaths ?? new List<string>())
            {
                string root = NormalizeRelativePath(artifact);
                if (root.Length == 0) continue;
                if (!relative.Equals(root, StringComparison.Ordinal) && !relative.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                if (_ExecutionEnvironment.PathExists(Path.Combine(workingDirectory, relative.Replace('/', Path.DirectorySeparatorChar))))
                {
                    lookup.Found = relative;
                    return lookup;
                }
            }
            return lookup;
        }

        // Keeps the first sibling whose several same-named files left a citation unresolved, so the miss names
        // those candidates instead of a missing file.
        private static void RecordSiblingAmbiguity(OutsideCitation result, SiblingLookup lookup, string declared)
        {
            if (lookup.Candidates.Count == 0 || result.AmbiguousCandidates.Count > 0) return;
            result.AmbiguousCandidates = lookup.Candidates.Select(candidate => declared + "/" + candidate).ToList();
            result.NoCandidateHolds = lookup.NoCandidateHolds;
            result.RootLabel = "declared sibling `" + declared + "`";
        }

        private static bool IsContainedRelativePath(string relative)
        {
            return !String.IsNullOrWhiteSpace(relative)
                && !relative.StartsWith("/", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative)
                && !relative.Split('/').Any(segment => segment == ".." || segment == ".");
        }

        // Where a citation outside the target repository resolves. NamesRoot is true when the path names a
        // declared sibling or evidence root, so a miss there is reported against that root.
        private sealed class OutsideCitation
        {
            public bool NamesRoot { get; set; } = false;
            public string RootLabel { get; set; } = "the declared sibling checkouts";
            public string? FoundAt { get; set; } = null;
            public List<string> AmbiguousCandidates { get; set; } = new List<string>();
            public bool NoCandidateHolds { get; set; } = false;
        }

        // One sibling's answer for a cited name: the file it names, or the candidates that left it unresolved.
        private sealed class SiblingLookup
        {
            public string? Found { get; set; } = null;
            public List<string> Candidates { get; set; } = new List<string>();
            public bool NoCandidateHolds { get; set; } = false;
        }

        /// <summary>
        /// Whether a backticked token is an Armada record id rather than a code identifier. The long form (with its
        /// random tail) is always a record id; the short form counts only with a known record prefix, so a
        /// snake_case identifier of the same shape is still searched for.
        /// </summary>
        private static bool IsRecordId(string token)
        {
            Match match = _RecordIdPattern.Match(token ?? String.Empty);
            if (!match.Success) return false;
            return match.Groups["tail"].Success || _RecordIdPrefixes.Contains(match.Groups["prefix"].Value);
        }

        private static HashSet<string> ReadRecordIdPrefixes()
        {
            HashSet<string> prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Reflection.FieldInfo field in typeof(Constants).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (field.FieldType != typeof(string) || !field.Name.EndsWith("IdPrefix", StringComparison.Ordinal)) continue;
                string? value = field.GetValue(null) as string;
                if (String.IsNullOrEmpty(value) || !value.EndsWith("_", StringComparison.Ordinal)) continue;
                prefixes.Add(value.Substring(0, value.Length - 1));
            }
            return prefixes;
        }

        private static List<string> MatchAll(string input, Regex pattern)
        {
            List<string> matches = new List<string>();
            foreach (Match match in pattern.Matches(input))
            {
                matches.Add(match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value);
            }
            return matches;
        }

        private static string? ResolveTargetVesselId(
            Objective objective,
            string? requestedVesselId,
            ObjectiveDispatchPreview result)
        {
            string? requested = NormalizeEmpty(requestedVesselId);
            List<string> objectiveTargets = (objective.VesselIds ?? new List<string>())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested != null)
            {
                if (objectiveTargets.Count != 1)
                {
                    AddIssue(result, "target_vessel_count", "target", ReadinessSeverityEnum.Error,
                        "Objective dispatch requires exactly one objective target vessel; the objective has " + objectiveTargets.Count + ".", null);
                }
                else if (!objectiveTargets.Contains(requested, StringComparer.OrdinalIgnoreCase))
                {
                    AddIssue(result, "objective_vessel_mismatch", "target", ReadinessSeverityEnum.Error,
                        "The requested vessel is not one of the objective target vessels.", requested);
                }
                return requested;
            }

            if (objectiveTargets.Count != 1)
            {
                AddIssue(result, "target_vessel_count", "target", ReadinessSeverityEnum.Error,
                    "Objective dispatch requires exactly one target vessel; the objective has " + objectiveTargets.Count + ".", null);
                return null;
            }

            return objectiveTargets[0];
        }

        private async Task EvaluateRepositoryContextAsync(
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            string repositoryPath = RepositoryPath(vessel);
            bool localExists = await _Git.IsRepositoryAsync(repositoryPath, token).ConfigureAwait(false);
            bool canClone = !String.IsNullOrWhiteSpace(vessel.RepoUrl);
            if (!localExists && !canClone)
            {
                AddIssue(result, "target_repository_unavailable", "provisioning", ReadinessSeverityEnum.Error,
                    "The target vessel has no usable local repository and no repository URL.", repositoryPath);
            }
            else if (!localExists)
            {
                AddIssue(result, "target_repository_clone_required", "provisioning", ReadinessSeverityEnum.Warning,
                    "Dock provisioning must clone the target repository before work starts.", vessel.RepoUrl);
            }
        }

        private async Task EvaluateStartRefAsync(Vessel vessel, ObjectiveDispatchPreview result, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(result.StartFromRef)) return;
            string repositoryPath = RepositoryPath(vessel);
            string? commit = await _Git.GetRevisionCommitShaAsync(repositoryPath, result.StartFromRef, token).ConfigureAwait(false);
            result.ResolvedStartCommit = NormalizeEmpty(commit);
            if (String.IsNullOrWhiteSpace(commit))
            {
                AddIssue(result, "start_from_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The objective start ref does not resolve in the target repository.", result.StartFromRef);
            }
        }

        private async Task<List<string>> EvaluateMissionDescriptionsAsync(
            Vessel vessel,
            Objective objective,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            List<string> modes = new List<string>();
            if (missionDescriptions == null || missionDescriptions.Count == 0) return modes;

            string? objectiveMode = MissionModes.FromObjectiveKind(objective.Kind);
            for (int index = 0; index < missionDescriptions.Count; index++)
            {
                MissionDescription? mission = missionDescriptions[index];
                if (mission == null) continue;
                string? suppliedMode = NormalizeEmpty(mission.Mode);
                if (suppliedMode != null && !MissionModes.IsKnown(suppliedMode))
                {
                    AddIssue(result, "invalid_mission_mode", "brief", ReadinessSeverityEnum.Error,
                        "Mission " + (index + 1) + " has an unknown mode: " + suppliedMode + ".", suppliedMode);
                }
                string effectiveMode = suppliedMode != null && MissionModes.IsKnown(suppliedMode)
                    ? MissionModes.Parse(suppliedMode).ToString()
                    : objectiveMode ?? MissionModeEnum.Implementation.ToString();
                modes.Add(effectiveMode);

                string? startRef = NormalizeEmpty(mission.StartFromRef) ?? NormalizeEmpty(objective.StartFromRef);
                if (!String.IsNullOrWhiteSpace(mission.DependsOnMissionId)
                    || !String.IsNullOrWhiteSpace(mission.DependsOnMissionAlias))
                    startRef = null;
                string? commit = startRef == null
                    ? null
                    : NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(RepositoryPath(vessel), startRef, token).ConfigureAwait(false));
                result.MissionStartRefs.Add(new ObjectiveDispatchMissionRef
                {
                    MissionIndex = index,
                    Title = mission.Title ?? String.Empty,
                    Mode = effectiveMode,
                    StartFromRef = startRef,
                    ResolvedCommit = commit
                });
                if (startRef != null && commit == null)
                {
                    AddIssue(result, "mission_start_from_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                        "The start ref for mission " + (index + 1) + " does not resolve in the target repository.", startRef);
                }
            }
            result.EffectiveMissionModes = modes.ToList();
            if (modes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                result.DeliverableMode = modes[0];
            else if (modes.Count > 1)
                result.DeliverableMode = "Mixed";
            return modes;
        }

        private async Task EvaluatePreparationAnchorsAsync(
            AuthContext auth,
            Objective objective,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            ObjectivePreparation preparation = objective.Preparation ?? new ObjectivePreparation();
            await EvaluateAnchorAsync(auth, objective, preparation.Source, "source", result, token).ConfigureAwait(false);
            await EvaluateAnchorAsync(auth, objective, preparation.Target, "target", result, token).ConfigureAwait(false);
            if (result.PreparationAnchorMoves.Count > 0)
                result.RenderedBrief = ObjectiveBriefRenderer.Render(objective, result.PreparationAnchorMoves);

            if (preparation.Target != null
                && !String.IsNullOrWhiteSpace(preparation.Target.VesselId)
                && !String.Equals(preparation.Target.VesselId, result.VesselId, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(result, "preparation_target_vessel_mismatch", "target", ReadinessSeverityEnum.Error,
                    "The prepared target vessel does not match the dispatch target vessel.", preparation.Target.VesselId);
            }
        }

        private async Task EvaluateAnchorAsync(
            AuthContext auth,
            Objective objective,
            ObjectivePreparationAnchor? anchor,
            string label,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (anchor == null) return;
            if (String.IsNullOrWhiteSpace(anchor.VesselId))
            {
                AddIssue(result, "preparation_" + label + "_vessel_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " anchor must name a vessel.", null);
                return;
            }
            if (String.IsNullOrWhiteSpace(anchor.Ref) || String.IsNullOrWhiteSpace(anchor.ResolvedCommit))
            {
                AddIssue(result, "preparation_" + label + "_revision_incomplete", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " anchor must include a ref and resolved commit.", anchor.VesselId);
                return;
            }

            Vessel? anchorVessel = await ReadVesselAsync(auth, anchor.VesselId, token).ConfigureAwait(false);
            if (anchorVessel == null)
            {
                AddIssue(result, "preparation_" + label + "_vessel_not_found", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " vessel does not exist or is not accessible.", anchor.VesselId);
                return;
            }

            string? current = await _Git.GetRevisionCommitShaAsync(RepositoryPath(anchorVessel), anchor.Ref, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(current))
            {
                AddIssue(result, "preparation_" + label + "_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " ref does not resolve.", anchor.Ref);
            }
            else if (!RevisionMatches(current, anchor.ResolvedCommit))
            {
                await EvaluateMovedAnchorAsync(objective, anchor, label, RepositoryPath(anchorVessel), current, result, token).ConfigureAwait(false);
            }
        }

        // A branch that moved past the recorded commit invalidates the preparation only when the move touched a
        // path the objective depends on. The paths that changed are read from the repository; when none of them
        // is cited, claimed, or assigned, the move is a warning and dispatch uses the current tip. A pin that
        // names a commit or a tag, a recorded value that is not a full commit, or a comparison that cannot be
        // read stays an error, because nothing then proves the preparation still holds.
        private async Task EvaluateMovedAnchorAsync(
            Objective objective,
            ObjectivePreparationAnchor anchor,
            string label,
            string repositoryPath,
            string current,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            string code = "preparation_" + label + "_revision_changed";
            string recorded = anchor.ResolvedCommit!.Trim();
            string refName = anchor.Ref!.Trim();
            string moved = "The prepared " + label + " ref " + refName + " no longer resolves to the recorded commit "
                + ShortCommit(recorded) + "; it now resolves to " + ShortCommit(current) + ".";

            if (!IsFullCommit(recorded))
            {
                AddIssue(result, code, "provisioning", ReadinessSeverityEnum.Error,
                    moved + " The recorded value is not a full commit, so the change since preparation cannot be read.", refName);
                return;
            }
            if (!await IsBranchRefAsync(repositoryPath, refName, token).ConfigureAwait(false))
            {
                AddIssue(result, code, "provisioning", ReadinessSeverityEnum.Error,
                    moved + " The ref is not a branch, so a move is not expected; re-verify the preparation.", refName);
                return;
            }

            ChangedPathStatusRead read = await _Git.ReadChangedPathStatusesBetweenCommitsAsync(
                repositoryPath, recorded, current, PreparationAnchorChangeReader.MaxChangedPaths, PreparationAnchorChangeReader.ReadTimeout,
                PreparationAnchorChangeReader.OutputLimitBytes, token).ConfigureAwait(false);
            if (!read.Available)
            {
                AddIssue(result, code, "provisioning", ReadinessSeverityEnum.Error,
                    moved + " The files changed between the two commits could not be read (" + read.FailureCode + ": " + read.Message
                    + "), so the preparation cannot be shown to still hold.", refName);
                return;
            }

            List<string> watched = CollectDependentPaths(objective);
            List<string> touched = PreparationAnchorChangeReader.ChangedPaths(read)
                .Where(path => watched.Any(item => PreparationAnchorChangeReader.PathCovers(item, path)))
                .ToList();
            int changedCount = read.TotalCount ?? read.Entries.Count;
            if (touched.Count > 0)
            {
                IEnumerable<string> named = touched.Take(_MaxNamedAnchorMovePaths).Select(path => "`" + path + "`");
                string more = touched.Count > _MaxNamedAnchorMovePaths ? " and " + (touched.Count - _MaxNamedAnchorMovePaths) + " more" : String.Empty;
                AddIssue(result, code, "provisioning", ReadinessSeverityEnum.Error,
                    moved + " " + changedCount + " file(s) changed, and these are cited, claimed, or assigned by this objective; recheck them: "
                    + String.Join(", ", named) + more + ".", refName);
                return;
            }
            if (!read.Complete)
            {
                AddIssue(result, code, "provisioning", ReadinessSeverityEnum.Error,
                    moved + " " + changedCount + " file(s) changed, more than the " + read.Entries.Count
                    + " that can be compared, so the preparation cannot be shown to still hold.", refName);
                return;
            }

            AddIssue(result, "preparation_" + label + "_revision_moved", "provisioning", ReadinessSeverityEnum.Warning,
                "The prepared " + label + " branch " + refName + " moved from " + ShortCommit(recorded) + " to " + ShortCommit(current)
                + ". " + changedCount + " file(s) changed and none is cited, claimed, or assigned by this objective, so dispatch uses "
                + ShortCommit(current) + ".", refName);
            result.PreparationAnchorMoves.Add(new ObjectivePreparationAnchorMove
            {
                Anchor = label,
                Ref = refName,
                RecordedCommit = recorded,
                CurrentCommit = current,
                ChangedFileCount = changedCount
            });
        }

        private async Task<bool> IsBranchRefAsync(string repositoryPath, string refName, CancellationToken token)
        {
            if (IsFullCommit(refName)) return false;
            string branch = refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName.Substring("refs/heads/".Length) : refName;
            if (branch.Length == 0 || branch.StartsWith("refs/", StringComparison.Ordinal)) return false;
            return await _Git.BranchExistsAsync(repositoryPath, branch, token).ConfigureAwait(false);
        }

        // Every repository path the objective depends on: the path:line citations of its description (the
        // preflight question-1 paths), the files its title and description name (the assigned paths), and the
        // paths its preparation claims name in their text or evidence. Sibling paths are not in this repository.
        private static List<string> CollectDependentPaths(Objective objective)
        {
            List<string> paths = new List<string>();
            string description = objective.Description ?? String.Empty;
            paths.AddRange(MatchAll(description, _PathLinePattern));
            paths.AddRange(ContextPackManifest.ExtractAssignedPaths((objective.Title ?? String.Empty) + "\n" + description));
            foreach (ObjectivePreparationClaim claim in objective.Preparation?.Claims ?? new List<ObjectivePreparationClaim>())
            {
                if (claim == null) continue;
                paths.AddRange(PreparationAnchorChangeReader.ClaimPaths(claim, out bool _));
            }
            return PreparationAnchorChangeReader.NormalizePaths(paths);
        }

        private static bool IsFullCommit(string value)
        {
            return PreparationAnchorChangeReader.IsFullCommit(value);
        }

        /// <summary>
        /// What a captain dock for this objective provides, for the preflight text half: the declared sibling
        /// checkouts, the probed tools, and the evidence roots that apply to the vessel. Names and dock-relative
        /// paths only, so no host path reaches the model.
        /// </summary>
        internal PreflightDockContext BuildDockContext(Objective objective, Vessel vessel)
        {
            PreflightDockContext dock = new PreflightDockContext
            {
                OperatingSystem = _ExecutionEnvironment.OperatingSystem,
                IsContainer = _ExecutionEnvironment.IsContainer
            };

            foreach (SiblingRepo sibling in vessel.GetSiblingRepos() ?? new List<SiblingRepo>())
            {
                if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath)) continue;
                List<string> artifacts = (sibling.ExtractionArtifactPaths ?? new List<string>())
                    .Where(item => !String.IsNullOrWhiteSpace(item)).ToList();
                dock.SiblingCheckouts.Add(artifacts.Count == 0
                    ? sibling.RelativePath
                    : sibling.RelativePath + " (artifacts: " + String.Join(", ", artifacts) + ")");
            }

            IEnumerable<string> requiredTools = objective.Preparation?.ExecutionRequirements?.Executables ?? new List<string>();
            foreach (string tool in _DockProbeTools.Concat(requiredTools)
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal))
            {
                // A required executable may be named by path; only its file name is reported.
                string name = Path.GetFileName(tool);
                if (_ExecutionEnvironment.ResolveExecutable(tool) != null) dock.AvailableTools.Add(name);
                else dock.MissingTools.Add(name);
            }

            foreach (CaptainEvidenceRoot evidence in _Settings.CaptainEvidenceRoots)
            {
                if (evidence == null || String.IsNullOrWhiteSpace(evidence.Path) || !evidence.AppliesTo(vessel.Id)) continue;
                string name = Path.GetFileName(evidence.Path.TrimEnd('/', '\\'));
                if (Path.IsPathRooted(evidence.Path) && _ExecutionEnvironment.PathExists(evidence.Path)) dock.EvidenceRoots.Add(name);
                else dock.MissingEvidenceRoots.Add(name);
            }

            return dock;
        }

        private async Task EvaluateSiblingProvisioningAsync(
            AuthContext auth,
            Objective objective,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            List<SiblingRepo> declaredSiblings = vessel.GetSiblingRepos();
            foreach (ObjectivePreparationSiblingInput required in objective.Preparation?.RequiredSiblingInputs
                ?? new List<ObjectivePreparationSiblingInput>())
            {
                Vessel? requiredVessel = await ResolveVesselReferenceAsync(auth, required.VesselRef, token).ConfigureAwait(false);
                if (requiredVessel == null)
                {
                    AddIssue(result, "required_sibling_vessel_not_found", "provisioning", ReadinessSeverityEnum.Error,
                        "The required sibling vessel does not exist or is not accessible.", required.VesselRef);
                    continue;
                }

                SiblingRepo? declaration = null;
                foreach (SiblingRepo sibling in declaredSiblings.Where(item => item != null
                    && String.Equals(NormalizeRelativePath(item.RelativePath), NormalizeRelativePath(required.RelativePath), StringComparison.OrdinalIgnoreCase)))
                {
                    if (String.Equals(sibling.VesselRef, required.VesselRef, StringComparison.OrdinalIgnoreCase))
                    {
                        declaration = sibling;
                        break;
                    }
                    Vessel? declaredVessel = await ResolveVesselReferenceAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
                    if (String.Equals(declaredVessel?.Id, requiredVessel.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        declaration = sibling;
                        break;
                    }
                }
                if (declaration == null)
                {
                    AddIssue(result, "required_sibling_not_declared", "provisioning", ReadinessSeverityEnum.Error,
                        "The target vessel does not declare required sibling " + required.VesselRef + " at " + required.RelativePath + ".",
                        required.VesselRef);
                    continue;
                }

                HashSet<string> declaredArtifacts = new HashSet<string>(
                    (declaration.ExtractionArtifactPaths ?? new List<string>()).Select(NormalizeRelativePath),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string artifactPath in required.RequiredArtifactPaths ?? new List<string>())
                {
                    if (!declaredArtifacts.Contains(NormalizeRelativePath(artifactPath)))
                        AddIssue(result, "required_sibling_artifact_not_declared", "provisioning", ReadinessSeverityEnum.Error,
                            "The sibling declaration does not provision required artifact path " + artifactPath + ".",
                            required.VesselRef);
                }

                if (!String.IsNullOrWhiteSpace(required.PinnedCommit))
                    await EvaluateSiblingPinAsync(required, declaration, requiredVessel, result, token).ConfigureAwait(false);
            }

            foreach (SiblingRepo sibling in declaredSiblings)
            {
                if (sibling == null) continue;
                if (String.IsNullOrWhiteSpace(sibling.RelativePath))
                {
                    AddIssue(result, "sibling_relative_path_missing", "provisioning", ReadinessSeverityEnum.Error,
                        "A declared sibling repository has no relative checkout path.", null);
                    continue;
                }

                Vessel? siblingVessel = null;
                if (!String.IsNullOrWhiteSpace(sibling.VesselRef))
                {
                    siblingVessel = await ReadVesselAsync(auth, sibling.VesselRef, token).ConfigureAwait(false)
                        ?? await ReadVesselByNameAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
                }
                bool registeredSourceReady = siblingVessel != null
                    && await _Git.IsRepositoryAsync(RepositoryPath(siblingVessel), token).ConfigureAwait(false);
                bool hasSource = registeredSourceReady
                    || !String.IsNullOrWhiteSpace(siblingVessel?.RepoUrl)
                    || !String.IsNullOrWhiteSpace(sibling.RepoUrl);
                if (!hasSource)
                {
                    AddIssue(result, "sibling_source_unavailable", "provisioning",
                        sibling.BuildParticipant ? ReadinessSeverityEnum.Error : ReadinessSeverityEnum.Warning,
                        "Armada cannot resolve the source for sibling repository " + sibling.RelativePath + ".", sibling.VesselRef);
                }

                if (sibling.ExtractionArtifactPaths == null || sibling.ExtractionArtifactPaths.Count == 0) continue;
                if (siblingVessel == null || String.IsNullOrWhiteSpace(siblingVessel.WorkingDirectory))
                {
                    AddIssue(result, "sibling_artifact_source_unavailable", "provisioning", ReadinessSeverityEnum.Error,
                        "The sibling extraction artifacts require a registered vessel with a working directory.", sibling.RelativePath);
                    continue;
                }

                foreach (string artifactPath in sibling.ExtractionArtifactPaths.Where(item => !String.IsNullOrWhiteSpace(item)))
                {
                    string sourcePath = Path.Combine(siblingVessel.WorkingDirectory, artifactPath);
                    if (!Directory.Exists(sourcePath))
                    {
                        AddIssue(result, "sibling_artifact_missing", "provisioning", ReadinessSeverityEnum.Error,
                            "A required sibling extraction artifact path does not exist.", sourcePath);
                    }
                }
            }
        }

        /// <summary>
        /// A pinned sibling commit names the tree the objective's evidence was measured at, while docks provision
        /// the sibling's branch tip. A pin that is not an ancestor of that tip describes a tree no dock receives,
        /// so every stage would re-measure a delta that cannot be reconciled; that refuses dispatch. A pin that
        /// is an ancestor is reported in the brief against the provisioned HEAD, which controls.
        /// </summary>
        private async Task EvaluateSiblingPinAsync(
            ObjectivePreparationSiblingInput required,
            SiblingRepo declaration,
            Vessel requiredVessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            string pinned = required.PinnedCommit!.Trim();
            string repositoryPath = RepositoryPath(requiredVessel);
            string branch = !String.IsNullOrWhiteSpace(declaration.DefaultBranch)
                ? declaration.DefaultBranch!
                : (!String.IsNullOrWhiteSpace(requiredVessel.DefaultBranch) ? requiredVessel.DefaultBranch : "main");
            if (!await _Git.IsRepositoryAsync(repositoryPath, token).ConfigureAwait(false))
            {
                AddIssue(result, "required_sibling_pin_unverified", "provisioning", ReadinessSeverityEnum.Warning,
                    "The sibling repository cannot be read, so pinned commit " + pinned + " was not checked against branch " + branch + ".",
                    required.VesselRef);
                return;
            }

            string? pinCommit = await _Git.GetRevisionCommitShaAsync(repositoryPath, pinned, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(pinCommit))
            {
                AddIssue(result, "required_sibling_pin_unresolved", "provisioning", ReadinessSeverityEnum.Error,
                    "Pinned sibling commit " + pinned + " does not exist in " + required.VesselRef
                    + ". Pin a commit on branch " + branch + ", or clear the pin so the provisioned tip controls.",
                    required.VesselRef);
                return;
            }

            string? tip = await _Git.GetRevisionCommitShaAsync(repositoryPath, "refs/heads/" + branch, token).ConfigureAwait(false);
            bool? ancestor = String.IsNullOrWhiteSpace(tip)
                ? null
                : await _Git.TryIsAncestorAsync(repositoryPath, pinCommit, tip, token).ConfigureAwait(false);
            if (ancestor == false)
            {
                AddIssue(result, "required_sibling_pin_not_ancestor", "provisioning", ReadinessSeverityEnum.Error,
                    "Pinned sibling commit " + ShortCommit(pinCommit) + " is not an ancestor of " + required.VesselRef + " branch "
                    + branch + " at " + ShortCommit(tip!) + ", the tree docks provision. Re-pin to a commit on that branch, "
                    + "or clear the pin so the provisioned tip controls.",
                    required.VesselRef);
            }
            else if (ancestor == null)
            {
                AddIssue(result, "required_sibling_pin_unverified", "provisioning", ReadinessSeverityEnum.Warning,
                    "Pinned sibling commit " + ShortCommit(pinCommit) + " could not be checked against branch " + branch + ".",
                    required.VesselRef);
            }
        }

        private static string ShortCommit(string commit)
        {
            return commit.Length > 12 ? commit.Substring(0, 12) : commit;
        }

        private async Task<Vessel?> ResolveVesselReferenceAsync(
            AuthContext auth,
            string? vesselRef,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(vesselRef)) return null;
            return await ReadVesselAsync(auth, vesselRef, token).ConfigureAwait(false)
                ?? await ReadVesselByNameAsync(auth, vesselRef, token).ConfigureAwait(false);
        }

        private static string NormalizeRelativePath(string? value)
        {
            return (value ?? String.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

        private async Task<Pipeline?> ResolvePipelineReadOnlyAsync(
            AuthContext auth,
            Vessel vessel,
            string? explicitPipeline,
            bool readOnly,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            // The preview applies the same ownership rule as dispatch: a pipeline is used on behalf of
            // the vessel's owner, and a record that owner may not use is reported as refused, not missing.
            // Only a global administrator learns that a pipeline exists in another tenant; for every other
            // caller such a pipeline reads exactly as a missing one.
            bool seesOtherTenants = auth.IsAdmin;
            if (!String.IsNullOrWhiteSpace(explicitPipeline))
            {
                Pipeline? requested = await _Database.Pipelines.ReadAsync(explicitPipeline, token).ConfigureAwait(false);
                bool refused = false;
                if (requested != null)
                {
                    if (!OwnershipPolicy.CanUseFor(vessel.TenantId, vessel.UserId, requested))
                    {
                        refused = seesOtherTenants || OwnershipPolicy.SameTenant(requested.TenantId, vessel.TenantId);
                        requested = null;
                    }
                }
                else
                {
                    OwnedRecordLookup<Pipeline> lookup = await OwnedRecordScope.ReadUsableByNameAsync(
                        vessel.TenantId,
                        vessel.UserId,
                        explicitPipeline,
                        async () => (await _Database.Pipelines.EnumerateAsync(token).ConfigureAwait(false))
                            .Where(pipeline => seesOtherTenants || pipeline.IsBuiltIn || OwnershipPolicy.SameTenant(pipeline.TenantId, vessel.TenantId))
                            .ToList(),
                        pipeline => pipeline.Name).ConfigureAwait(false);
                    requested = lookup.Record;
                    refused = lookup.WasRefused;
                }

                if (refused)
                    AddIssue(result, "pipeline_not_usable", "pipeline", ReadinessSeverityEnum.Error,
                        "The requested pipeline belongs to another owner and cannot be used for this vessel.", explicitPipeline);
                else if (requested == null)
                    AddIssue(result, "pipeline_not_found", "pipeline", ReadinessSeverityEnum.Error,
                        "The requested pipeline does not exist.", explicitPipeline);
                else
                    ValidatePipeline(requested, result);
                return requested;
            }

            if (readOnly) return null;

            string? inheritedPipelineId = NormalizeEmpty(vessel.DefaultPipelineId);
            if (inheritedPipelineId == null && !String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                Fleet? fleet = await _Database.Fleets.ReadAsync(vessel.FleetId, token).ConfigureAwait(false);
                inheritedPipelineId = NormalizeEmpty(fleet?.DefaultPipelineId);
            }
            if (inheritedPipelineId == null) return null;

            Pipeline? inherited = await _Database.Pipelines.ReadAsync(inheritedPipelineId, token).ConfigureAwait(false);
            if (inherited == null)
            {
                AddIssue(result, "default_pipeline_not_found", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective default pipeline does not exist.", inheritedPipelineId);
                return null;
            }
            if (!OwnershipPolicy.CanUseFor(vessel.TenantId, vessel.UserId, inherited))
            {
                AddIssue(result, "default_pipeline_not_usable", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective default pipeline belongs to another owner and is not inherited by this vessel.", inheritedPipelineId);
                return null;
            }
            ValidatePipeline(inherited, result);
            return inherited;
        }

        /// <summary>
        /// Apply a stored operator-confirmed stage skip as the objective's default, with the same
        /// <see cref="PipelineStageSkip.Apply"/> rule dispatch uses. An unconfirmed skip is reported and not
        /// applied. A stored entry that names no stage of the chosen pipeline is dropped and named in a
        /// <c>stage_skip_default_dropped</c> Warning. A skip dispatch would refuse is reported with the same
        /// code and not applied. Never throws into the preview.
        /// </summary>
        private static Pipeline? ApplyStoredStageSkip(
            Objective objective,
            Pipeline? pipeline,
            ObjectiveDispatchPreview result)
        {
            List<PipelineStage> source = (pipeline?.Stages ?? new List<PipelineStage> { new PipelineStage(1, "Worker") })
                .Where(stage => stage != null)
                .OrderBy(stage => stage.Order)
                .ToList();
            result.EffectivePipelineStages = source.Select(stage => stage.PersonaName).ToList();

            StageSkipRequest? skip = objective.Preparation?.StageSkip;
            if (!StageSkipRequest.HasStages(skip))
                return pipeline;

            result.StageSkipConfirmed = StageSkipRequest.HasTrustedConfirmation(skip);
            if (!result.StageSkipConfirmed)
            {
                AddIssue(result, "stage_skip_unconfirmed", "pipeline", ReadinessSeverityEnum.Warning,
                    "The stored stage skip has no server-issued operator confirmation; autonomous dispatch will skip this objective.", null);
                return pipeline;
            }

            try
            {
                PipelineStageSkipResult applied = PipelineStageSkip.Apply(pipeline, PipelineStageSkip.AsObjectiveDefault(skip));
                foreach (DroppedStageSkip dropped in applied.DroppedDefaults)
                {
                    result.DroppedStageSkips.Add(dropped.Entry);
                    AddIssue(result, PipelineStageSkip.DefaultDroppedCode, "pipeline", ReadinessSeverityEnum.Warning,
                        "The stored stage skip entry \"" + dropped.Entry + "\" is dropped for this dispatch because it names no stage of the effective pipeline: "
                        + dropped.Reason, dropped.Entry);
                }
                result.SkippedPipelineStages = applied.SkippedStages
                    .Where(stage => stage != null)
                    .Select(stage => stage.PersonaName)
                    .ToList();
                List<PipelineStage> kept = (applied.Pipeline?.Stages ?? new List<PipelineStage>())
                    .Where(stage => stage != null)
                    .OrderBy(stage => stage.Order)
                    .ToList();
                if (kept.Count == 0)
                    kept.Add(new PipelineStage(1, "Worker"));
                result.EffectivePipelineStages = kept.Select(stage => stage.PersonaName).ToList();
                return applied.Pipeline;
            }
            catch (StageSkipRefusedException refused)
            {
                result.StageSkipRefusalCode = refused.Code;
                AddIssue(result, refused.Code, "pipeline", ReadinessSeverityEnum.Error, refused.Message, refused.Persona);
                return pipeline;
            }
        }

        private static void ValidatePipeline(Pipeline pipeline, ObjectiveDispatchPreview result)
        {
            if (!pipeline.Active)
                AddIssue(result, "pipeline_inactive", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective pipeline is not active.", pipeline.Id);
            if (pipeline.Stages == null || pipeline.Stages.Count == 0)
                AddIssue(result, "pipeline_has_no_stages", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective pipeline has no stages.", pipeline.Id);
        }

        /// <summary>
        /// Consult the D19 stage-necessity adapter over the resolved pipeline stages and add one
        /// <c>stage_optional</c> Warning issue per non-Judge stage the model proposes the objective does
        /// not need. The deterministic pipeline is unchanged: the warning is advisory and the operator
        /// confirms a skip through <c>skipStages</c> before the voyage is materialised. Never throws into
        /// the preview.
        /// </summary>
        private async Task RefineStageNecessityAsync(
            Objective objective,
            Vessel vessel,
            Pipeline? pipeline,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (StageNecessityAdapter == null || objective == null) return;

            List<StageNecessityStageResult> stages = (pipeline?.Stages ?? new List<PipelineStage>())
                .Where(stage => stage != null)
                .OrderBy(stage => stage.Order)
                .Select(stage => TypedStageNecessityAdapter.Stage(stage.Order, stage.PersonaName))
                .ToList();
            if (stages.Count == 0)
                stages.Add(TypedStageNecessityAdapter.Stage(1, "Worker"));

            Dictionary<string, string> descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PipelineStage stage in (pipeline?.Stages ?? new List<PipelineStage>()).Where(item => item != null))
            {
                if (!String.IsNullOrWhiteSpace(stage.Description) && !descriptions.ContainsKey(stage.PersonaName))
                    descriptions[stage.PersonaName] = stage.Description!;
            }

            StageNecessityDecisionInput input = new StageNecessityDecisionInput
            {
                ObjectiveId = objective.Id,
                Title = objective.Title ?? String.Empty,
                Description = objective.Description ?? String.Empty,
                AcceptanceCriteria = (objective.AcceptanceCriteria ?? new List<string>())
                    .Where(item => !String.IsNullOrWhiteSpace(item)).ToList(),
                Kind = objective.Kind.ToString(),
                Stages = stages,
                StageDescriptions = descriptions,
                VesselIds = TypedDecisionEgress.VesselsOf(objective.VesselIds, vessel?.Id)
                    .Where(id => !String.IsNullOrWhiteSpace(id)).Select(id => id!).ToList()
            };

            // The adapter is contracted never to throw into the caller: Off returns the rule with no
            // call, and every fault path returns the rule verdict. So a raised exception cannot break the
            // deterministic preview, exactly as the D5 preflight adapter is called above.
            StageNecessityVerdict rule = StageNecessityVerdict.Rule(stages);
            StageNecessityVerdict verdict = await StageNecessityAdapter.DecideAsync(input, rule, token).ConfigureAwait(false);

            foreach (StageNecessityStageResult stage in verdict.OptionalStages)
            {
                string suffix = stage.ModelAutoSkip ? " (auto-skip eligible)" : String.Empty;
                AddIssue(result, "stage_optional", "pipeline", ReadinessSeverityEnum.Warning,
                    "The " + stage.PersonaName + " stage may not be needed for this objective ("
                    + stage.SkipReason + "); confirm a skip through skipStages if so." + suffix,
                    stage.PersonaName);
            }
        }

        /// <summary>
        /// Report, per required role, the captains assignment could ever choose, by asking the rules
        /// assignment asks: the mission's tenant, quarantine, Smart Routing persona routes, the requested
        /// captain (the override's captain or the persona's default captain) and its fallback tier floor,
        /// and the Legacy Routing eligibility layer, under which a pinned model no captain runs is a tier
        /// floor. A busy captain counts as coverage; idle state is capacity, not readiness. Nothing is
        /// reserved or claimed.
        /// </summary>
        private async Task EvaluateCaptainCoverageAsync(
            AuthContext auth,
            Vessel vessel,
            Pipeline? pipeline,
            List<Captain> captains,
            IReadOnlyList<CaptainAssignmentOverride>? captainAssignments,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            List<PipelineStage> stages = pipeline?.Stages?.ToList()
                ?? new List<PipelineStage> { new PipelineStage(1, "Worker") };
            ModelTierSettings tiers = _Settings.ModelTier;
            UsageRoutingSettings usage = tiers.UsageRouting;
            DateTime now = DateTime.UtcNow;
            List<string> busy = captains
                .Where(captain => captain != null && captain.State == CaptainStateEnum.Working)
                .Select(captain => captain.Id)
                .ToList();
            Dictionary<string, string?> personaDefaults = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (PipelineStage stage in stages
                .Where(item => item != null)
                .OrderBy(item => item.Order))
            {
                List<string?> resolvedPreferences = ResolveStagePreferredModels(
                    stage, missionDescriptions, tiers.MinimumTierForPersona(stage.PersonaName));
                foreach (string? preferredModel in resolvedPreferences.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    CaptainAssignmentOverride? assignment = MissionService.SelectCaptainOverride(captainAssignments, stage.PersonaName);
                    bool overrideNamesCaptain = assignment != null && !String.IsNullOrWhiteSpace(assignment.CaptainId);
                    string? personaDefault = null;
                    if (!overrideNamesCaptain && !String.IsNullOrEmpty(stage.PersonaName))
                    {
                        if (!personaDefaults.TryGetValue(stage.PersonaName, out personaDefault))
                        {
                            OwnedRecordLookup<Persona> lookup = await MissionService.ReadUsablePersonaAsync(
                                _Database, vessel.TenantId, vessel.UserId, stage.PersonaName, token).ConfigureAwait(false);
                            personaDefault = lookup.Record?.DefaultCaptainId;
                            personaDefaults[stage.PersonaName] = personaDefault;
                        }
                    }

                    RequestedCaptainResolution request = RequestedCaptainAssignmentRule.ResolveRequest(assignment, personaDefault);
                    Mission probe = new Mission
                    {
                        Persona = stage.PersonaName,
                        PreferredModel = preferredModel,
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        RequestedCaptainId = request.CaptainId,
                        Tier = request.FallbackTier
                    };

                    List<Captain> pool = captains
                        .Where(captain => captain != null && IsAssignableForCoverage(captain, probe, usage, tiers, captains, now))
                        .ToList();

                    Captain? requested = null;
                    if (request.CaptainId != null)
                    {
                        // A captain outside the caller's scope reads as absent, so the preview never describes
                        // another tenant's captain: naming its id reads exactly like naming a missing captain.
                        requested = captains.FirstOrDefault(captain => captain != null && String.Equals(captain.Id, request.CaptainId, StringComparison.Ordinal))
                            ?? await CallerScopedRead.ReadCaptainAsync(_Database, auth, request.CaptainId, token).ConfigureAwait(false);
                    }

                    string? ineligible = requested == null
                        ? null
                        : RequestedCaptainAssignmentRule.DescribeIneligibility(requested, stage.PersonaName, tiers);
                    bool requestedAssignable = requested != null
                        && ineligible == null
                        && pool.Any(captain => String.Equals(captain.Id, requested.Id, StringComparison.Ordinal));

                    // An assignable requested captain takes the mission whenever it is free; while it is busy, or
                    // when it can never take it, the fallback tier floor applies to the rest of the pool.
                    List<Captain> fallbackPool = requestedAssignable
                        ? pool.Where(captain => !String.Equals(captain.Id, requested!.Id, StringComparison.Ordinal)).ToList()
                        : pool;
                    RequestedCaptainAssignmentDecision fallback = RequestedCaptainAssignmentRule.Decide(
                        probe,
                        requested,
                        ineligible ?? (requestedAssignable ? "busy" : "not assignable"),
                        fallbackPool);

                    List<Captain> configured = new List<Captain>();
                    if (requestedAssignable) configured.Add(requested!);
                    if (fallback.Outcome != RequestedCaptainOutcomeEnum.WaitForTier)
                    {
                        foreach (Captain candidate in fallback.Candidates)
                        {
                            if (LegacyCaptainSelector.CouldSelect(tiers, probe, candidate)
                                && !configured.Any(captain => String.Equals(captain.Id, candidate.Id, StringComparison.Ordinal)))
                                configured.Add(candidate);
                        }
                    }

                    if (overrideNamesCaptain)
                    {
                        // Dispatch refuses the same assignment on the same finding, so preview and dispatch agree.
                        Captain? visible = captains.FirstOrDefault(captain => captain != null && String.Equals(
                            captain.Id, request.CaptainId, StringComparison.Ordinal));
                        CaptainEligibilityFinding? finding = RequestedCaptainAssignmentRule.EvaluateAssignment(
                            visible, stage.PersonaName, probe.TenantId, tiers);
                        if (finding != null)
                        {
                            AddIssue(result, "assigned_captain_ineligible", "captain", ReadinessSeverityEnum.Error,
                                "The assigned captain cannot run the " + stage.PersonaName + " role: it is " + finding.Reason + ".",
                                assignment!.CaptainId);
                        }
                    }
                    else if (requested != null && ineligible != null)
                    {
                        AddIssue(result, "default_captain_ineligible", "captain", ReadinessSeverityEnum.Warning,
                            "The " + stage.PersonaName + " persona's default captain is " + ineligible
                            + ", so assignment passes it over.", requested.Id);
                    }

                    ObjectiveDispatchRole role = new ObjectiveDispatchRole
                    {
                        Persona = stage.PersonaName,
                        PreferredModel = preferredModel,
                        EligibleConfiguredCaptainIds = configured.Select(captain => captain.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                        IdleEligibleCount = configured.Count(captain => IsIdleCapacity(captain, probe, usage, busy, now))
                    };
                    result.RequiredRoles.Add(role);

                    if (configured.Count == 0)
                    {
                        AddIssue(result, "required_role_has_no_captain", "captain", ReadinessSeverityEnum.Error,
                            "No configured captain can run the required " + stage.PersonaName + " role.", preferredModel);
                    }
                    else if (role.IdleEligibleCount == 0)
                    {
                        AddIssue(result, "required_role_has_no_idle_captain", "captain", ReadinessSeverityEnum.Warning,
                            "The required " + stage.PersonaName + " role has configured coverage but no idle capacity.", preferredModel);
                    }
                }
            }
        }

        // The gates assignment applies before any choice, with a busy captain counted as one that will be
        // free: its state is usable, it belongs to the mission's tenant, it is not quarantined, and the
        // persona's Smart Routing routes and model lists admit it when Smart Routing is enabled (dead lists
        // fall back to every admitted captain, as assignment does).
        private static bool IsAssignableForCoverage(
            Captain captain, Mission probe, UsageRoutingSettings usage, ModelTierSettings tiers, IReadOnlyList<Captain> roster, DateTime now)
        {
            return IsConfiguredUsableCaptain(captain)
                && MissionService.CaptainServesTenant(captain, probe.TenantId)
                && !CaptainQuarantineService.IsQuarantinedAt(captain, now)
                && (!usage.Enabled || UsageRoutingService.PersonaRoutesAdmit(usage, probe.Persona, captain))
                && SmartRoutingSelector.PersonaModelListsAdmit(usage, probe, captain, tiers, roster, now);
        }

        // Idle capacity is what assignment could take now: an idle captain whose account usage Smart Routing
        // removes (an exhausted allowance, a held account, a concurrency limit) is coverage, not capacity.
        private bool IsIdleCapacity(Captain captain, Mission probe, UsageRoutingSettings usage, IReadOnlyCollection<string> busy, DateTime now)
        {
            if (captain.State != CaptainStateEnum.Idle) return false;
            if (!usage.Enabled) return true;
            return UsageRoutingService.For(_Settings).ClassifyCaptain(usage, probe, captain, busy, now).Outcome != UsageRoutingService.OutcomeRemoved;
        }

        private static List<string?> ResolveStagePreferredModels(
            PipelineStage stage,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            Armada.Core.Enums.CaptainTierEnum? minimumTier)
        {
            List<string?> resolved = new List<string?>();
            if (missionDescriptions == null || missionDescriptions.Count == 0)
            {
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel, null, minimumTier));
                return resolved;
            }

            foreach (MissionDescription missionDescription in missionDescriptions)
            {
                if (missionDescription == null) continue;
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel,
                    missionDescription.PreferredModel,
                    minimumTier));
            }

            if (resolved.Count == 0)
            {
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel, null, minimumTier));
            }

            return resolved;
        }

        /// <summary>
        /// Flag the gate inputs the vessel's workflow profile declares that the planned missions will not name. With
        /// an Architect stage the plan does not exist yet, so the warning lists every declared input the Architect is
        /// told to assign; with operator mission descriptions it lists the inputs none of them names. A gate input
        /// the objective's rollout constraints or non-goals keep out of the change (a "no docs/ changes" rule, or an
        /// allow-list that does not name it) is not listed, and a warning with nothing left to list is not raised.
        /// A pipeline with neither an Architect nor mission descriptions has no file lists to compare, and the
        /// warning is not raised. It never blocks dispatch.
        /// </summary>
        private async Task EvaluateGateInputsAsync(
            AuthContext auth,
            Objective objective,
            Vessel vessel,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            WorkflowProfile? profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);
            List<string> declared = WorkflowGateInputs.Normalize(profile?.GateInputPaths);
            List<ObjectiveGateInputSources> mappings = objective.Preparation?.GateInputSources ?? new List<ObjectiveGateInputSources>();
            string? mappingError = WorkflowGateInputs.ValidateSources(mappings);
            if (mappingError != null)
            {
                AddIssue(result, "gate_input_sources_invalid", "brief", ReadinessSeverityEnum.Error, mappingError, null);
                return;
            }
            if (declared.Count == 0) return;

            List<string> excluded = WorkflowGateInputs.FindExcluded(declared, objective.RolloutConstraints, objective.NonGoals);
            List<string> inScope = declared.Where(entry => !excluded.Contains(entry, StringComparer.OrdinalIgnoreCase)).ToList();
            if (inScope.Count == 0) return;
            string excludedNote = excluded.Count == 0
                ? String.Empty
                : " The objective's constraints keep " + String.Join(", ", excluded) + " out of the change, so they are not listed.";

            List<string?> planTexts = new List<string?>();
            foreach (MissionDescription description in missionDescriptions ?? Array.Empty<MissionDescription>())
            {
                if (description == null) continue;
                planTexts.Add(description.Title);
                planTexts.Add(description.Description);
            }
            List<string> plannedPaths = WorkflowGateInputs.FindPlannedPaths(planTexts);
            Dictionary<string, List<string>> affectedSources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectiveGateInputSources mapping in mappings)
            {
                string input = WorkflowGateInputs.Normalize(new[] { mapping.InputPath })[0];
                if (!inScope.Contains(input, StringComparer.OrdinalIgnoreCase)) continue;
                List<string> affected = WorkflowGateInputs.FindMappedSources(mapping, plannedPaths);
                // Path mentions include references and exclusions, so absence is never proof of an unaffected input.
                affectedSources[input] = affected.Count > 0 ? affected : WorkflowGateInputs.Normalize(mapping.SourcePaths);
            }
            if (inScope.Count == 0) return;
            List<string> dependencyNotes = affectedSources.Select(pair => String.Join(", ", pair.Value) + " -> " + pair.Key).ToList();
            string mappedNote = dependencyNotes.Count == 0 ? String.Empty
                : " Declared input-to-source dependencies: " + String.Join("; ", dependencyNotes) + ".";
            bool unknownDependencies = inScope.Any(input => !affectedSources.ContainsKey(input));
            string plannedNote = (plannedPaths.Count == 0
                ? " No planned file paths have been supplied; the Architect must establish which inputs the change can affect."
                : String.Empty) + mappedNote;
            if (unknownDependencies && plannedPaths.Count > 0)
                plannedNote += " Paths named in supplied mission descriptions (may include references or exclusions): "
                    + String.Join(", ", plannedPaths.Take(WorkflowGateInputs.MaxEntries))
                    + ". The preparation does not declare which planned paths affect each input without a mapping; confirm that relation before assigning input edits.";


            bool architectPlans = result.EffectivePipelineStages.Any(stage => PersonaCatalog.Matches(stage, PersonaCatalog.Architect));
            if (architectPlans)
            {
                AddIssue(result, GateInputsUnplannedIssueCode, "brief", ReadinessSeverityEnum.Warning,
                    "The Architect stage writes the mission file lists, and the required checks read these gate inputs: "
                    + String.Join(", ", inScope)
                    + ". The Architect brief tells it to assign each one the change touches; a plan that names none of them records "
                    + WorkflowGateInputs.PlanMissingEventType + "." + plannedNote + excludedNote,
                    String.Join(",", inScope));
                return;
            }

            if (missionDescriptions == null || missionDescriptions.Count == 0) return;
            List<string> unplanned = WorkflowGateInputs.FindUnplanned(inScope, planTexts);
            if (unplanned.Count == 0) return;
            AddIssue(result, GateInputsUnplannedIssueCode, "brief", ReadinessSeverityEnum.Warning,
                "No mission description names these gate inputs the required checks read: "
                + String.Join(", ", unplanned)
                + ". If the change touches what they describe, give each one to the mission that makes the change." + plannedNote + excludedNote,
                String.Join(",", unplanned));
        }

        private async Task EvaluateChecksAsync(
            AuthContext auth,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            VoyageCheckArmingSettings arming = _Settings.VoyageCheckArming;
            if (!arming.Enabled)
            {
                AddIssue(result, "check_arming_disabled", "verification", ReadinessSeverityEnum.Error,
                    "Voyage Check arming is disabled, so dispatch cannot produce the required verification records.", null);
                return;
            }

            WorkflowProfile? profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);
            if (profile == null)
            {
                AddIssue(result, "workflow_profile_missing", "verification", ReadinessSeverityEnum.Error,
                    "No active workflow profile resolves for the target vessel.", vessel.Id);
                AddRequiredCheck(result, CheckRunTypeEnum.Build, null, false, arming.ArmBuild);
                AddRequiredCheck(result, CheckRunTypeEnum.UnitTest, null, false, arming.ArmUnitTest);
                return;
            }

            await EvaluateRequiredCheckAsync(auth, vessel, profile, CheckRunTypeEnum.Build, arming.ArmBuild, result, token).ConfigureAwait(false);
            await EvaluateRequiredCheckAsync(auth, vessel, profile, CheckRunTypeEnum.UnitTest, arming.ArmUnitTest, result, token).ConfigureAwait(false);

            // The preview asks the same plan dispatch uses, so it cannot promise a Slop check the
            // arming service would not create.
            bool isDotNet = DotNetVesselDetector.IsDotNetVessel(vessel, profile, out string _);
            if (VoyageCheckArmingPlan.Resolve(arming, profile, null, false, isDotNet).Contains(CheckRunTypeEnum.Slop))
                AddRequiredCheck(result, CheckRunTypeEnum.Slop, SlopCheckRunner.CommandLabel, true, true);
        }

        private async Task EvaluateRequiredCheckAsync(
            AuthContext auth,
            Vessel vessel,
            WorkflowProfile profile,
            CheckRunTypeEnum type,
            bool required,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (!required) return;
            VesselReadinessResult readiness = await _VesselReadiness.EvaluateAsync(
                auth,
                vessel,
                profile.Id,
                type,
                includeWorkflowRequirements: true,
                token: token).ConfigureAwait(false);
            bool inputsReady = !readiness.Issues.Any(issue => String.Equals(
                issue.Code, "required_input_missing", StringComparison.OrdinalIgnoreCase));
            foreach (VesselReadinessIssue issue in readiness.Issues.Where(issue =>
                String.Equals(issue.Code, "required_input_missing", StringComparison.OrdinalIgnoreCase)
                || String.Equals(issue.Code, "workflow_profile_invalid", StringComparison.OrdinalIgnoreCase)
                || String.Equals(issue.Code, "command_dependency_missing", StringComparison.OrdinalIgnoreCase)))
            {
                AddIssue(result, issue.Code, "verification", ReadinessSeverityEnum.Error, issue.Message, issue.RelatedValue);
            }
            AddRequiredCheck(result, type, _WorkflowProfiles.ResolveCommand(profile, type), inputsReady, true);
        }

        private static void AddRequiredCheck(
            ObjectiveDispatchPreview result,
            CheckRunTypeEnum type,
            string? command,
            bool inputsReady,
            bool required)
        {
            if (!required) return;
            result.RequiredChecks.Add(new ObjectiveDispatchCheck
            {
                Type = type,
                Command = NormalizeEmpty(command),
                RequiredInputsReady = inputsReady
            });
            if (String.IsNullOrWhiteSpace(command))
            {
                AddIssue(result, "check_command_missing", "verification", ReadinessSeverityEnum.Error,
                    "No command is configured for required Check " + type + ".", type.ToString());
            }
        }

        private async Task<Vessel?> ReadVesselAsync(AuthContext auth, string id, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Vessels.ReadAsync(id, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
            if (auth.IsTenantAdmin) return await _Database.Vessels.ReadAsync(auth.TenantId, id, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
            return await _Database.Vessels.ReadAsync(auth.TenantId, auth.UserId, id, token).ConfigureAwait(false);
        }

        private async Task<Vessel?> ReadVesselByNameAsync(AuthContext auth, string name, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Vessels.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
            Vessel? vessel = await _Database.Vessels.ReadByNameAsync(auth.TenantId, name, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin) return vessel;
            if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
            return vessel != null && String.Equals(vessel.UserId, auth.UserId, StringComparison.Ordinal)
                ? vessel
                : null;
        }

        private async Task<List<Captain>> ReadCaptainsAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return new List<Captain>();
            if (auth.IsTenantAdmin) return await _Database.Captains.EnumerateAsync(auth.TenantId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return new List<Captain>();
            return await _Database.Captains.EnumerateAsync(auth.TenantId, auth.UserId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read one objective by id in the caller's scope and return its stored-data error, or null when the
        /// row reads cleanly or is absent.
        /// </summary>
        private async Task<string?> ReadStoredDataProblemAsync(AuthContext auth, string objectiveId, CancellationToken token)
        {
            try
            {
                if (auth.IsAdmin) await _Database.Objectives.ReadAsync(objectiveId, token).ConfigureAwait(false);
                else if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
                else if (auth.IsTenantAdmin) await _Database.Objectives.ReadAsync(auth.TenantId, objectiveId, token).ConfigureAwait(false);
                else if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
                else await _Database.Objectives.ReadAsync(auth.TenantId, auth.UserId, objectiveId, token).ConfigureAwait(false);
                return null;
            }
            catch (StoredObjectiveDataException ex)
            {
                return ex.Message;
            }
        }

        private async Task<List<Objective>> ReadObjectivesAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return new List<Objective>();
            if (auth.IsTenantAdmin) return await _Database.Objectives.EnumerateAsync(auth.TenantId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return new List<Objective>();
            return await _Database.Objectives.EnumerateAsync(auth.TenantId, auth.UserId, token).ConfigureAwait(false);
        }

        private static bool IsConfiguredUsableCaptain(Captain captain)
        {
            return captain.State != CaptainStateEnum.Benched
                && captain.State != CaptainStateEnum.Quarantined
                && captain.State != CaptainStateEnum.Stalled
                && captain.State != CaptainStateEnum.Stopping;
        }

        private string RepositoryPath(Vessel vessel)
        {
            return PreparationAnchorChangeReader.RepositoryPath(vessel, _Settings);
        }

        private static bool RevisionMatches(string left, string right)
        {
            return left.Length == 40
                && right.Length == 40
                && left.All(Uri.IsHexDigit)
                && right.All(Uri.IsHexDigit)
                && String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadOnlyMissionMode(string mode)
        {
            MissionModeEnum parsed = MissionModes.Parse(mode);
            return parsed == MissionModeEnum.Audit || parsed == MissionModeEnum.Research;
        }

        private static string? NormalizeEmpty(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static void AddIssue(
            ObjectiveDispatchPreview result,
            string code,
            string area,
            ReadinessSeverityEnum severity,
            string message,
            string? relatedValue)
        {
            result.Issues.Add(new ObjectiveDispatchPreviewIssue
            {
                Code = code,
                Area = area,
                Severity = severity,
                Message = message,
                RelatedValue = relatedValue
            });
        }

        private static void FinalizeResult(ObjectiveDispatchPreview result)
        {
            result.ErrorCount = result.Issues.Count(issue => issue.Severity == ReadinessSeverityEnum.Error);
            result.WarningCount = result.Issues.Count(issue => issue.Severity == ReadinessSeverityEnum.Warning);
            result.IsReady = result.ErrorCount == 0;
        }

        #endregion
    }
}
