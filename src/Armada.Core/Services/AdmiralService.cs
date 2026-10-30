namespace Armada.Core.Services
{
    using System.IO;
    using System.Linq;
    using SyslogLogging;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Top-level orchestration service for coordinating missions and captains.
    /// Delegates domain logic to CaptainService, MissionService, VoyageService, and DockService.
    /// </summary>
    public partial class AdmiralService : IAdmiralService, Armada.Core.Harbor.IHarborCompletionRecovery
    {
        #region Public-Members

        /// <inheritdoc />
        public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent
        {
            get => _Captains.OnLaunchAgent;
            set => _Captains.OnLaunchAgent = value;
        }

        /// <inheritdoc />
        public Func<Captain, Task>? OnStopAgent
        {
            get => _Captains.OnStopAgent;
            set => _Captains.OnStopAgent = value;
        }

        /// <inheritdoc />
        public Func<Mission, Dock, Task>? OnCaptureDiff
        {
            get => _Missions.OnCaptureDiff;
            set => _Missions.OnCaptureDiff = value;
        }

        /// <inheritdoc />
        public Func<Mission, Dock, Task>? OnMissionComplete
        {
            get => _Missions.OnMissionComplete;
            set => _Missions.OnMissionComplete = value;
        }

        /// <inheritdoc />
        public Func<Voyage, Task>? OnVoyageComplete
        {
            get => _Missions.OnVoyageComplete;
            set => _Missions.OnVoyageComplete = value;
        }

        /// <inheritdoc />
        public bool IsMissionCompletionInFlight(string missionId)
        {
            return _Missions.IsCompletionInFlight(missionId);
        }

        /// <inheritdoc />
        public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }

        /// <inheritdoc />
        public Func<Task<int>>? OnReconcileMergeEntries { get; set; }

        /// <summary>
        /// Delegate that checks one bounded page of LandingFailed missions for work that is already on the vessel's
        /// default branch and proposes each to the operator. Returns the number proposed. Wired to
        /// <c>LandingReconcileSweep.SweepAsync</c> at server startup.
        /// </summary>
        public Func<Task<int>>? OnReconcileLandingFailures { get; set; }

        /// <inheritdoc />
        public Func<int, bool>? OnIsProcessExitHandled { get; set; }

        /// <summary>
        /// Optional callback for retrieving outbound remote-tunnel status.
        /// This is kept as a concrete AdmiralService hook so server-only infrastructure
        /// can surface tunnel state without expanding the public orchestration interface yet.
        /// </summary>
        public Func<RemoteTunnelStatus>? OnGetRemoteTunnelStatus { get; set; }

        /// <summary>
        /// Optional callback for retrieving the autonomous objective scheduler status.
        /// Set by ArmadaServer after the scheduler is instantiated so Core does not
        /// take a dependency on the Server-layer scheduler type.
        /// </summary>
        public Func<ObjectiveSchedulerStatus>? OnGetSchedulerStatus { get; set; }

        /// <summary>
        /// Optional callback supplying the fleet-wide code-index staleness summary for the status
        /// surface. Wired by the server to the code-index service; null when the index is disabled.
        /// </summary>
        public Func<Task<CodeIndexStalenessSummary?>>? OnGetCodeIndexStaleness { get; set; }

        #endregion

        #region Private-Members

        private string _Header = "[AdmiralService] ";
        private static readonly TimeSpan _AssignedOrphanRecoveryGracePeriod = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan _WorkProducedReleaseGracePeriod = TimeSpan.FromMinutes(2);
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private ICaptainService _Captains;
        private IMissionService _Missions;
        private IVoyageService _Voyages;
        private IDockService _Docks;
        private IPlaybookService _Playbooks;
        private IEscalationService? _Escalation;
        private IBuildDriftService? _BuildDrift;
        private ICaptainQuarantineService _CaptainQuarantine;
        private IResourcePressureAdmission _ResourcePressureAdmission;
        private readonly FleetCapacityAdmission _FleetCapacityAdmission;
        private readonly CaptainCrashLoopTracker _CrashLoopTracker;
        private readonly List<HealthLoopMaintenanceStep> _HealthCheckSteps;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _HealthCheckStepFailures =
            new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly DispatchHold? _DispatchHold;
        private ProviderProgressTracker? _ProviderProgress;
        private TerminalMarkerTracker? _TerminalMarkers;

        /// <inheritdoc />
        public DispatchHold? DispatchHold => _DispatchHold;

        /// <summary>
        /// Registry of agent processes stopped on purpose. The admiral registers each process it supersedes
        /// here, and the process-exit handler consults it before it reads an exit code.
        /// </summary>
        public IntentionalProcessStops IntentionalStops => _IntentionalStops;

        private readonly IntentionalProcessStops _IntentionalStops;
        private readonly ICaptainProcessTreeReader _ProcessTree;

        /// <summary>
        /// The D3 <c>runtime_failure</c> typed-decision adapter, when wired. Null keeps runtime
        /// classification on the signature rule alone. Set by the server after construction. The
        /// adapter may only upgrade a bare Crash to the more conservative UsageLimit or AuthFailure;
        /// it never downgrades a recognised fault and never benches a captain by itself.
        /// </summary>
        public TypedRuntimeFailureAdapter? RuntimeFailureAdapter { get; set; }

        /// <summary>
        /// The D2 <c>refusal</c> typed-decision adapter, when wired. Null keeps refusal classification
        /// on the deterministic rule alone. Set by the server after construction. A provider safeguard
        /// block stays authoritative inside the adapter's Combine.
        /// </summary>
        public TypedRefusalAdapter? RefusalAdapter { get; set; }

        private IGitService _Git;
        private bool _RetryDispatchNeeded = false;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task> _QueuedAssignments =
            new System.Collections.Concurrent.ConcurrentDictionary<long, Task>();
        private long _QueuedAssignmentSequence = 0;
        private DateTime? _LastAuditNotifyUtc = null;
        private readonly object _AuditNotifyLock = new object();
        private const string _CreditAuthQuarantineReason =
            "Provider credit, billing, payment, or authentication failure detected during mission execution.";

        // Recoverable-failure re-routing (provider safeguard block OR quota/credit/balance limit): how many times
        // a single mission may be re-routed to a different captain before we stop and hand the decision to the
        // operator (never loop). Sized to walk past same-provider siblings and try each distinct provider once.
        private const int _RerouteMaxAttempts = 5;

        // How long a captain that hit a provider safeguard block is benched. The window only needs to outlast the
        // re-dispatch so a same-provider sibling is not immediately reselected; the block itself is content-
        // deterministic, not transient, so the exact duration is not load-bearing.

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="captains">Captain service.</param>
        /// <param name="missions">Mission service.</param>
        /// <param name="voyages">Voyage service.</param>
        /// <param name="docks">Dock service.</param>
        /// <param name="escalation">Optional escalation service.</param>
        /// <param name="buildDrift">Optional build drift service.</param>
        /// <param name="captainQuarantine">Optional captain quarantine service.</param>
        /// <param name="resourcePressureAdmission">Optional resource-pressure admission policy updated on OOM classification.</param>
        /// <param name="git">Optional git service.</param>
        /// <param name="dispatchHold">Optional emergency dispatch hold.</param>
        /// <param name="fleetCapacityAdmission">Optional universal fleet-capacity admission service.</param>
        /// <param name="intentionalStops">Optional registry of processes stopped on purpose, shared with the process-exit source.</param>
        /// <param name="processTree">Optional reader for a captain's descendant processes; the host's <c>/proc</c> by default.</param>
        public AdmiralService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            ICaptainService captains,
            IMissionService missions,
            IVoyageService voyages,
            IDockService docks,
            IEscalationService? escalation = null,
            IBuildDriftService? buildDrift = null,
            ICaptainQuarantineService? captainQuarantine = null,
            IResourcePressureAdmission? resourcePressureAdmission = null,
            IGitService? git = null,
            DispatchHold? dispatchHold = null,
            FleetCapacityAdmission? fleetCapacityAdmission = null,
            IntentionalProcessStops? intentionalStops = null,
            ICaptainProcessTreeReader? processTree = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Captains = captains ?? throw new ArgumentNullException(nameof(captains));
            _Missions = missions ?? throw new ArgumentNullException(nameof(missions));
            _Voyages = voyages ?? throw new ArgumentNullException(nameof(voyages));
            _Docks = docks ?? throw new ArgumentNullException(nameof(docks));
            _Playbooks = new PlaybookService(_Database, _Logging);
            _Escalation = escalation;
            _BuildDrift = buildDrift;
            _CaptainQuarantine = captainQuarantine ?? new CaptainQuarantineService(_Database, _Settings, _Logging);
            _ResourcePressureAdmission = resourcePressureAdmission
                ?? new ResourcePressureAdmission(_Settings.ResourcePressureAdmission, new HostResourcePressureProbe(), _Logging);
            _Git = git ?? new GitService(_Logging, database: _Database);
            _DispatchHold = dispatchHold;
            _FleetCapacityAdmission = fleetCapacityAdmission
                ?? new FleetCapacityAdmission(_Database, _Settings, _Logging);
            _CrashLoopTracker = new CaptainCrashLoopTracker(_Settings.CrashLoopDetection);
            _IntentionalStops = intentionalStops ?? new IntentionalProcessStops();
            _ProcessTree = processTree ?? new ProcCaptainProcessTreeReader();
            _HealthCheckSteps = BuildHealthCheckSteps();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when a mission has left the running states: its work was produced or it reached a
        /// terminal state. A process-exit or health-check observation for such a mission is stale
        /// and must never transition it again.
        /// </summary>
        /// <param name="status">Mission status.</param>
        /// <returns>True for WorkProduced, PullRequestOpen, Complete, Failed, LandingFailed and Cancelled.</returns>
        public static bool IsPostWorkOrTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.Cancelled
                || status == MissionStatusEnum.WorkProduced
                || status == MissionStatusEnum.LandingFailed
                || status == MissionStatusEnum.PullRequestOpen;
        }

        /// <summary>Wire the shared runtime provider-progress tracker used by health recovery.</summary>
        /// <param name="tracker">Provider-progress tracker.</param>
        public void SetProviderProgress(ProviderProgressTracker tracker)
        {
            _ProviderProgress = tracker ?? throw new ArgumentNullException(nameof(tracker));
        }

        /// <summary>Wire terminal output state so recovery leaves completed stages alone.</summary>
        /// <param name="tracker">Terminal-marker tracker.</param>
        public void SetTerminalMarkers(TerminalMarkerTracker tracker)
        {
            _TerminalMarkers = tracker ?? throw new ArgumentNullException(nameof(tracker));
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            CancellationToken token = default)
        {
            return await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (List<SelectedPlaybook>?)null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            List<SelectedPlaybook>? selectedPlaybooks,
            CancellationToken token = default)
        {
            return await DispatchSingleStageVoyageAsync(title, description, vesselId, missionDescriptions, selectedPlaybooks, null, token).ConfigureAwait(false);
        }

        private async Task<Voyage> DispatchSingleStageVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            List<SelectedPlaybook>? selectedPlaybooks,
            string? stagePersona,
            CancellationToken token)
        {
            if (String.IsNullOrEmpty(title)) throw new ArgumentNullException(nameof(title));
            if (String.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            if (missionDescriptions == null || missionDescriptions.Count == 0)
                throw new ArgumentException("At least one mission is required", nameof(missionDescriptions));
            CollectionLimits.RequireDispatch(missionDescriptions, selectedPlaybooks);
            _DispatchHold?.ThrowIfActive(vesselId);

            // Verify vessel exists
            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + vesselId);
            if (selectedPlaybooks != null && selectedPlaybooks.Count > 0 && !String.IsNullOrEmpty(vessel.TenantId))
            {
                await _Playbooks.ResolveSelectionsAsync(vessel.TenantId, selectedPlaybooks, token).ConfigureAwait(false);
            }

            // Validate request-shaped inputs before creating any durable voyage state.
            ValidatePrestagedFilesOrThrow(missionDescriptions);
            await ValidateDependsOnReferencesOrThrowAsync(vessel.TenantId, missionDescriptions, token).ConfigureAwait(false);
            await ValidateStartFromRefsOrThrowAsync(vessel, missionDescriptions, token).ConfigureAwait(false);

            await using FleetCapacityReservation capacityAdmission = await _FleetCapacityAdmission
                .AcquireAsync(vessel, null, token).ConfigureAwait(false);

            Voyage? voyage = null;
            List<string> assignmentMissionIds = new List<string>();
            try
            {
            // Create voyage
            voyage = await _Database.Voyages.CreateAsync(NewDispatchVoyage(title, description, vessel, null), token).ConfigureAwait(false);
            voyage.SelectedPlaybooks = ClonePlaybookSelections(selectedPlaybooks);
            if (voyage.SelectedPlaybooks.Count > 0)
            {
                await _Database.Playbooks.SetVoyageSelectionsAsync(voyage.Id, voyage.SelectedPlaybooks, token).ConfigureAwait(false);
            }
            _Logging.Info(_Header + "created voyage " + voyage.Id + ": " + title);

            // Create missions
            foreach (MissionDescription md in missionDescriptions)
            {
                Mission mission = new Mission(md.Title, md.Description);
                mission.TenantId = vessel.TenantId;
                mission.UserId = vessel.UserId;
                mission.VoyageId = voyage.Id;
                mission.VesselId = vesselId;
                mission.Persona = stagePersona;
                mission.PrestagedFiles = ClonePrestagedFiles(md.PrestagedFiles);
                mission.PreferredModel = md.PreferredModel;
                mission.Mode = MissionModes.Parse(md.Mode);
                if (!String.IsNullOrEmpty(md.DependsOnMissionId))
                    mission.DependsOnMissionId = md.DependsOnMissionId;
                else
                    mission.StartFromRef = NormalizeStartFromRef(md.StartFromRef);
                mission.AssignmentState = MissionAssignmentStateEnum.Pending;
                mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                await RecordStartRefAsync(mission, token).ConfigureAwait(false);
                List<SelectedPlaybook> perMissionPlaybooks = PlaybookMerge.MergeWithVesselDefaults(
                    voyage.SelectedPlaybooks,
                    md.SelectedPlaybooks ?? new List<SelectedPlaybook>());
                await PersistMissionPlaybooksAsync(mission, perMissionPlaybooks, token).ConfigureAwait(false);
                _Logging.Info(_Header + "created mission " + mission.Id + ": " + md.Title);

                assignmentMissionIds.Add(mission.Id);
            }

            // Voyage remains Open; background assignment transitions it when a mission starts.
            _Logging.Info(_Header + "voyage " + voyage.Id + " left at Open -- assignment state transitions are deferred to background tasks");
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);
            await capacityAdmission.VerifyOwnershipAsync(token).ConfigureAwait(false);
            QueueVoyageAssignments(voyage.Id, vessel.Id, assignmentMissionIds);

            await VoyageDispatchedEvent.EmitAsync(_Database, _Logging, voyage, vessel.Id, missionDescriptions.Count).ConfigureAwait(false);
            return voyage;
            }
            catch
            {
                if (voyage != null)
                    await CancelPartiallyCreatedVoyageAsync(voyage).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            CancellationToken token = default)
        {
            return await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            CancellationToken token = default)
        {
            return await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            StageSkipRequest? stageSkip,
            CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(title)) throw new ArgumentNullException(nameof(title));
            if (String.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            if (missionDescriptions == null || missionDescriptions.Count == 0)
                throw new ArgumentException("At least one mission is required", nameof(missionDescriptions));
            CollectionLimits.RequireDispatch(missionDescriptions, selectedPlaybooks);
            _DispatchHold?.ThrowIfActive(vesselId);

            // Verify vessel exists
            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + vesselId);
            if (selectedPlaybooks != null && selectedPlaybooks.Count > 0 && !String.IsNullOrEmpty(vessel.TenantId))
            {
                await _Playbooks.ResolveSelectionsAsync(vessel.TenantId, selectedPlaybooks, token).ConfigureAwait(false);
            }

            // Resolve pipeline: explicit > vessel default > fleet default > WorkerOnly.
            // A read-only dispatch (all missions Audit or Research) never inherits a multi-stage
            // vessel/fleet default pipeline: a four-mission diagnostic probe once became sixteen
            // missions because the vessel default (ReferencePortingTested) expanded every stage.
            Pipeline? resolvedPipeline = await ResolvePipelineAsync(pipelineId, vessel, missionDescriptions, token).ConfigureAwait(false);

            // Operator-confirmed stage skips are dropped from the pipeline before anything is created,
            // so a refused skip leaves no voyage behind and the remaining stages chain across the gap.
            PipelineStageSkipResult skipResult = PipelineStageSkip.Apply(resolvedPipeline, stageSkip);
            Pipeline? pipeline = skipResult.Pipeline;

            // If pipeline is single-stage Worker (or null), use the standard dispatch path
            if (pipeline == null || (pipeline.Stages.Count == 1 && pipeline.Stages[0].PersonaName == "Worker" && !pipeline.Stages[0].RequiresReview))
            {
                Voyage workerVoyage = await DispatchSingleStageVoyageAsync(
                    title, description, vesselId, missionDescriptions, selectedPlaybooks, SingleStagePersona(pipeline), token).ConfigureAwait(false);
                await PipelineStageSkip.EmitSkippedEventsAsync(_Database, _Logging, workerVoyage, skipResult, stageSkip, token).ConfigureAwait(false);
                await VoyagePipelineRecord.RecordAsync(_Database, _Logging, workerVoyage, resolvedPipeline, token).ConfigureAwait(false);
                return workerVoyage;
            }

            // Validate request-shaped inputs before creating any durable voyage state.
            ValidatePrestagedFilesOrThrow(missionDescriptions);
            await ValidateDependsOnReferencesOrThrowAsync(vessel.TenantId, missionDescriptions, token).ConfigureAwait(false);
            await ValidateStartFromRefsOrThrowAsync(vessel, missionDescriptions, token).ConfigureAwait(false);

            await using FleetCapacityReservation capacityAdmission = await _FleetCapacityAdmission
                .AcquireAsync(vessel, null, token).ConfigureAwait(false);

            _Logging.Info(_Header + "resolved pipeline " + pipeline.Name + " with " + pipeline.Stages.Count + " stage(s) for voyage dispatch of " + missionDescriptions.Count + " mission(s)");

            Voyage? voyage = null;
            List<string> assignmentMissionIds = new List<string>();
            try
            {
            // Multi-stage pipeline: create voyage, then for each mission create a chain of persona stages
            voyage = await _Database.Voyages.CreateAsync(NewDispatchVoyage(title, description, vessel, null), token).ConfigureAwait(false);
            voyage.SelectedPlaybooks = ClonePlaybookSelections(selectedPlaybooks);
            if (voyage.SelectedPlaybooks.Count > 0)
            {
                await _Database.Playbooks.SetVoyageSelectionsAsync(voyage.Id, voyage.SelectedPlaybooks, token).ConfigureAwait(false);
            }
            _Logging.Info(_Header + "created pipeline voyage " + voyage.Id + ": " + title + " (pipeline: " + pipeline.Name + ")");

            foreach (MissionDescription md in missionDescriptions)
            {
                // Use a short base title (first 60 chars of the mission title)
                string baseTitle = md.Title.Length > 60 ? md.Title.Substring(0, 60).TrimEnd() + "..." : md.Title;
                string stageChainId = Mission.NewStageChainId();

                // Group stages by Order so that same-Order stages dispatch as parallel siblings
                // that all depend on the last mission of the previous order group rather than
                // forming a sequential chain within the same order.
                IOrderedEnumerable<IGrouping<int, PipelineStage>> stageGroups =
                    pipeline.Stages.GroupBy(s => s.Order).OrderBy(g => g.Key);

                string? previousOrderLastMissionId = null;

                foreach (IGrouping<int, PipelineStage> stageGroup in stageGroups)
                {
                    // All stages in this group share the same upstream dependency.
                    string? groupDependencyId = previousOrderLastMissionId
                        ?? (String.IsNullOrEmpty(md.DependsOnMissionId) ? null : md.DependsOnMissionId);

                    string? lastMissionInGroup = null;

                    foreach (PipelineStage stage in stageGroup)
                    {
                        Mission mission = new Mission(
                            "[" + stage.PersonaName + "] " + baseTitle,
                            AppendPipelineStageSection(md.Description, stage, pipeline.Stages.Count));
                        mission.TenantId = vessel.TenantId;
                        mission.UserId = vessel.UserId;
                        mission.VoyageId = voyage.Id;
                        mission.VesselId = vesselId;
                        mission.Persona = stage.PersonaName;
                        mission.DependsOnMissionId = groupDependencyId;
                        mission.StageOrder = stage.Order;
                        mission.StageChainId = stageChainId;
                        mission.RequiresReview = stage.RequiresReview;
                        mission.ReviewDenyAction = stage.ReviewDenyAction;
                        mission.PreferredModel = PreferredModelTierSelector.ResolveEffectivePreferredModel(
                            stage.PreferredModel,
                            md.PreferredModel,
                            _Settings.ModelTier.MinimumTierForPersona(stage.PersonaName));

                        // A stage that does not inherit the dispatch mode runs as Implementation: the
                        // DoD gate then judges report-only work by its commit, and the brief carries
                        // implementing instructions the captain cannot follow.
                        mission.Mode = MissionModes.Parse(md.Mode);

                        // Every pipeline stage gets its own dock worktree. Copy the requested
                        // prestaged entries to each stage so untracked briefing files are present
                        // when a downstream stage is provisioned from the shared branch.
                        mission.PrestagedFiles = ClonePrestagedFiles(md.PrestagedFiles);
                        bool isFirstChainMission = previousOrderLastMissionId == null && lastMissionInGroup == null;

                        // Only the first stage of a chain is cut from the start ref; every later
                        // stage continues the branch the first stage produced.
                        if (isFirstChainMission && String.IsNullOrEmpty(md.DependsOnMissionId))
                            mission.StartFromRef = NormalizeStartFromRef(md.StartFromRef);

                        mission.AssignmentState = MissionAssignmentStateEnum.Pending;
                        mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                        await RecordStartRefAsync(mission, token).ConfigureAwait(false);
                        List<SelectedPlaybook> perMissionPlaybooks = PlaybookMerge.MergeWithVesselDefaults(
                            voyage.SelectedPlaybooks,
                            md.SelectedPlaybooks ?? new List<SelectedPlaybook>());
                        await PersistMissionPlaybooksAsync(mission, perMissionPlaybooks, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "created pipeline mission " + mission.Id + ": " + mission.Title +
                            " (stage " + stage.Order + "/" + pipeline.Stages.Count + ", persona: " + stage.PersonaName +
                            (groupDependencyId != null ? ", depends on: " + groupDependencyId : "") + ")");

                        if (isFirstChainMission)
                            assignmentMissionIds.Add(mission.Id);

                        lastMissionInGroup = mission.Id;
                    }

                    previousOrderLastMissionId = lastMissionInGroup;
                }
            }

            // Voyage remains Open; background assignment transitions it when a mission starts.
            _Logging.Info(_Header + "voyage " + voyage.Id + " left at Open -- assignment state transitions are deferred to background tasks");
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);
            await capacityAdmission.VerifyOwnershipAsync(token).ConfigureAwait(false);
            QueueVoyageAssignments(voyage.Id, vessel.Id, assignmentMissionIds);
            await PipelineStageSkip.EmitSkippedEventsAsync(_Database, _Logging, voyage, skipResult, stageSkip, token).ConfigureAwait(false);
            await VoyagePipelineRecord.RecordAsync(_Database, _Logging, voyage, resolvedPipeline, token).ConfigureAwait(false);

            await VoyageDispatchedEvent.EmitAsync(_Database, _Logging, voyage, vessel.Id, missionDescriptions.Count).ConfigureAwait(false);
            return voyage;
            }
            catch
            {
                if (voyage != null)
                    await CancelPartiallyCreatedVoyageAsync(voyage).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageQueuedAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            CancellationToken token = default)
        {
            return await DispatchVoyageQueuedAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageQueuedAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            StageSkipRequest? stageSkip,
            CancellationToken token = default)
        {
            return await DispatchVoyageQueuedAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, stageSkip, null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Voyage> DispatchVoyageQueuedAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            StageSkipRequest? stageSkip,
            List<CaptainAssignmentOverride>? captainOverrides,
            CancellationToken token = default,
            bool isEvaluation = false)
        {
            if (String.IsNullOrEmpty(title)) throw new ArgumentNullException(nameof(title));
            if (String.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            if (missionDescriptions == null || missionDescriptions.Count == 0)
                throw new ArgumentException("At least one mission is required", nameof(missionDescriptions));
            CollectionLimits.RequireDispatch(missionDescriptions, selectedPlaybooks);
            _DispatchHold?.ThrowIfActive(vesselId);

            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + vesselId);
            if (selectedPlaybooks != null && selectedPlaybooks.Count > 0 && !String.IsNullOrEmpty(vessel.TenantId))
            {
                await _Playbooks.ResolveSelectionsAsync(vessel.TenantId, selectedPlaybooks, token).ConfigureAwait(false);
            }

            // Validate request-shaped inputs before creating any durable voyage state.
            ValidatePrestagedFilesOrThrow(missionDescriptions);
            await ValidateDependsOnReferencesOrThrowAsync(vessel.TenantId, missionDescriptions, token).ConfigureAwait(false);
            await ValidateStartFromRefsOrThrowAsync(vessel, missionDescriptions, token).ConfigureAwait(false);

            await using FleetCapacityReservation capacityAdmission = await _FleetCapacityAdmission
                .AcquireAsync(vessel, null, token).ConfigureAwait(false);

            Pipeline? resolvedPipeline = await ResolvePipelineAsync(pipelineId, vessel, missionDescriptions, token).ConfigureAwait(false);
            PipelineStageSkipResult skipResult = PipelineStageSkip.Apply(resolvedPipeline, stageSkip);
            Pipeline? pipeline = skipResult.Pipeline;
            bool isMultiStage = pipeline != null
                && !(pipeline.Stages.Count == 1 && pipeline.Stages[0].PersonaName == "Worker" && !pipeline.Stages[0].RequiresReview);

            Voyage? voyage = null;
            try
            {
            Voyage unsavedVoyage = NewDispatchVoyage(title, description, vessel, captainOverrides);
            unsavedVoyage.IsEvaluation = isEvaluation;
            voyage = await _Database.Voyages.CreateAsync(unsavedVoyage, token).ConfigureAwait(false);
            voyage.SelectedPlaybooks = ClonePlaybookSelections(selectedPlaybooks);
            if (voyage.SelectedPlaybooks.Count > 0)
            {
                await _Database.Playbooks.SetVoyageSelectionsAsync(voyage.Id, voyage.SelectedPlaybooks, token).ConfigureAwait(false);
            }

            _Logging.Info(_Header + "created queued voyage " + voyage.Id + ": " + title +
                (isMultiStage ? " (pipeline: " + pipeline!.Name + ")" : ""));

            List<string> assignmentMissionIds = new List<string>();

            if (!isMultiStage)
            {
                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.TenantId = vessel.TenantId;
                    mission.UserId = vessel.UserId;
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.Persona = SingleStagePersona(pipeline);
                    mission.PrestagedFiles = ClonePrestagedFiles(md.PrestagedFiles);
                    mission.PreferredModel = md.PreferredModel;
                    mission.Mode = MissionModes.Parse(md.Mode);
                    mission.IsEvaluation = voyage.IsEvaluation;
                    if (!String.IsNullOrEmpty(md.DependsOnMissionId))
                        mission.DependsOnMissionId = md.DependsOnMissionId;
                    else
                        mission.StartFromRef = NormalizeStartFromRef(md.StartFromRef);

                    mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                    await RecordStartRefAsync(mission, token).ConfigureAwait(false);
                    List<SelectedPlaybook> perMissionPlaybooks = PlaybookMerge.MergeWithVesselDefaults(
                        voyage.SelectedPlaybooks,
                        md.SelectedPlaybooks ?? new List<SelectedPlaybook>());
                    await PersistMissionPlaybooksAsync(mission, perMissionPlaybooks, token).ConfigureAwait(false);
                    assignmentMissionIds.Add(mission.Id);
                    _Logging.Info(_Header + "created queued mission " + mission.Id + ": " + md.Title);
                }
            }
            else
            {
                foreach (MissionDescription md in missionDescriptions)
                {
                    string baseTitle = md.Title.Length > 60 ? md.Title.Substring(0, 60).TrimEnd() + "..." : md.Title;
                    string stageChainId = Mission.NewStageChainId();
                    IOrderedEnumerable<IGrouping<int, PipelineStage>> stageGroups =
                        pipeline!.Stages.GroupBy(s => s.Order).OrderBy(g => g.Key);

                    string? previousOrderLastMissionId = null;

                    foreach (IGrouping<int, PipelineStage> stageGroup in stageGroups)
                    {
                        string? groupDependencyId = previousOrderLastMissionId
                            ?? (String.IsNullOrEmpty(md.DependsOnMissionId) ? null : md.DependsOnMissionId);

                        string? lastMissionInGroup = null;

                        foreach (PipelineStage stage in stageGroup)
                        {
                            Mission mission = new Mission("[" + stage.PersonaName + "] " + baseTitle,
                                AppendPipelineStageSection(md.Description, stage, pipeline!.Stages.Count));
                            mission.TenantId = vessel.TenantId;
                            mission.UserId = vessel.UserId;
                            mission.VoyageId = voyage.Id;
                            mission.VesselId = vesselId;
                            mission.Persona = stage.PersonaName;
                            mission.DependsOnMissionId = groupDependencyId;
                            mission.StageOrder = stage.Order;
                            mission.StageChainId = stageChainId;
                            mission.RequiresReview = stage.RequiresReview;
                            mission.ReviewDenyAction = stage.ReviewDenyAction;
                            mission.PreferredModel = PreferredModelTierSelector.ResolveEffectivePreferredModel(
                                stage.PreferredModel,
                                md.PreferredModel,
                                _Settings.ModelTier.MinimumTierForPersona(stage.PersonaName));
                            mission.Mode = MissionModes.Parse(md.Mode);
                            mission.IsEvaluation = voyage.IsEvaluation;

                            // Every pipeline stage gets a fresh dock worktree. Preserve the
                            // dispatch inputs on each stage instead of relying on untracked files
                            // from the upstream dock to appear in a new worktree.
                            mission.PrestagedFiles = ClonePrestagedFiles(md.PrestagedFiles);
                            bool isFirstChainMission = previousOrderLastMissionId == null && lastMissionInGroup == null;

                            // Only the first stage of a chain is cut from the start ref; every later
                            // stage continues the branch the first stage produced.
                            if (isFirstChainMission && String.IsNullOrEmpty(md.DependsOnMissionId))
                                mission.StartFromRef = NormalizeStartFromRef(md.StartFromRef);

                            mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                            await RecordStartRefAsync(mission, token).ConfigureAwait(false);
                            List<SelectedPlaybook> perMissionPlaybooks = PlaybookMerge.MergeWithVesselDefaults(
                                voyage.SelectedPlaybooks,
                                md.SelectedPlaybooks ?? new List<SelectedPlaybook>());
                            await PersistMissionPlaybooksAsync(mission, perMissionPlaybooks, token).ConfigureAwait(false);

                            if (isFirstChainMission)
                                assignmentMissionIds.Add(mission.Id);

                            _Logging.Info(_Header + "created queued pipeline mission " + mission.Id + ": " + mission.Title +
                                " (stage " + stage.Order + "/" + pipeline.Stages.Count + ", persona: " + stage.PersonaName +
                                (groupDependencyId != null ? ", depends on: " + groupDependencyId : "") + ")");

                            lastMissionInGroup = mission.Id;
                        }

                        previousOrderLastMissionId = lastMissionInGroup;
                    }
                }
            }

            voyage.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);
            await capacityAdmission.VerifyOwnershipAsync(token).ConfigureAwait(false);
            QueueVoyageAssignments(voyage.Id, vessel.Id, assignmentMissionIds);
            await PipelineStageSkip.EmitSkippedEventsAsync(_Database, _Logging, voyage, skipResult, stageSkip, token).ConfigureAwait(false);
            await VoyagePipelineRecord.RecordAsync(_Database, _Logging, voyage, resolvedPipeline, token).ConfigureAwait(false);

            await VoyageDispatchedEvent.EmitAsync(_Database, _Logging, voyage, vessel.Id, missionDescriptions.Count).ConfigureAwait(false);
            return voyage;
            }
            catch
            {
                if (voyage != null)
                    await CancelPartiallyCreatedVoyageAsync(voyage).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public Task<FleetCapacityAdmissionException?> ProbeCapacityAsync(Vessel vessel, string? candidateVoyageId, CancellationToken token = default)
        {
            return _FleetCapacityAdmission.ProbeAsync(vessel, candidateVoyageId, token);
        }

        /// <summary>
        /// A mission created for an evaluation voyage is evaluation work, even when the caller left the flag false.
        /// An explicit true is kept.
        /// </summary>
        private async Task InheritEvaluationFromVoyageAsync(Mission mission, CancellationToken token)
        {
            if (mission.IsEvaluation || String.IsNullOrEmpty(mission.VoyageId)) return;
            Voyage? voyage = await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
            if (voyage != null && voyage.IsEvaluation) mission.IsEvaluation = true;
        }

        /// <inheritdoc />
        public async Task<Mission> DispatchMissionQueuedAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            _DispatchHold?.ThrowIfActive(mission.VesselId);
            if (mission.SelectedPlaybooks != null && mission.SelectedPlaybooks.Count > 0 && !String.IsNullOrEmpty(mission.TenantId))
            {
                await _Playbooks.ResolveSelectionsAsync(mission.TenantId, mission.SelectedPlaybooks, token).ConfigureAwait(false);
            }

            if (mission.PrestagedFiles != null && mission.PrestagedFiles.Count > 0)
            {
                List<string> prestageErrors = PrestagedFileValidator.Validate(mission.PrestagedFiles);
                if (prestageErrors.Count > 0)
                {
                    throw new ArgumentException(
                        "Invalid prestagedFiles for mission '" + mission.Title + "': " +
                        String.Join("; ", prestageErrors),
                        nameof(mission));
                }
            }

            Vessel? capacityVessel = String.IsNullOrWhiteSpace(mission.VesselId)
                ? null
                : await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(mission.VesselId) && capacityVessel == null)
                throw new InvalidOperationException("Vessel not found: " + mission.VesselId);

            if (!String.IsNullOrEmpty(mission.DependsOnMissionId))
            {
                await ReadDependencyOrThrowAsync(capacityVessel?.TenantId ?? mission.TenantId, mission.DependsOnMissionId, token).ConfigureAwait(false);
            }
            if (capacityVessel != null && !String.IsNullOrWhiteSpace(mission.StartFromRef))
            {
                MissionDescription start = new MissionDescription(mission.Title, mission.Description ?? String.Empty)
                {
                    StartFromRef = mission.StartFromRef,
                    DependsOnMissionId = mission.DependsOnMissionId
                };
                await ValidateStartFromRefsOrThrowAsync(capacityVessel, new List<MissionDescription> { start }, token).ConfigureAwait(false);
                mission.StartFromRef = String.IsNullOrEmpty(mission.DependsOnMissionId) ? start.StartFromRef : null;
            }
            await using FleetCapacityReservation? capacityAdmission = capacityVessel == null
                ? null
                : await _FleetCapacityAdmission.AcquireAsync(capacityVessel, mission.VoyageId, token).ConfigureAwait(false);

            await InheritEvaluationFromVoyageAsync(mission, token).ConfigureAwait(false);

            Mission? createdMission = null;
            try
            {
                createdMission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                await RecordStartRefAsync(createdMission, token).ConfigureAwait(false);
                await PersistMissionPlaybooksAsync(createdMission, createdMission.SelectedPlaybooks, token).ConfigureAwait(false);
                if (capacityAdmission != null)
                    await capacityAdmission.VerifyOwnershipAsync(token).ConfigureAwait(false);
                mission = createdMission;
            }
            catch
            {
                if (createdMission != null)
                    await CancelPartiallyCreatedMissionAsync(createdMission).ConfigureAwait(false);
                throw;
            }
            _Logging.Info(_Header + "created queued mission " + mission.Id + ": " + mission.Title);

            if (!String.IsNullOrEmpty(mission.VesselId))
            {
                QueueVoyageAssignments(mission.VoyageId, mission.VesselId, new List<string> { mission.Id });
            }

            return mission;
        }

        /// <inheritdoc />
        public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            _DispatchHold?.ThrowIfActive(mission.VesselId);
            if (mission.SelectedPlaybooks != null && mission.SelectedPlaybooks.Count > 0 && !String.IsNullOrEmpty(mission.TenantId))
            {
                await _Playbooks.ResolveSelectionsAsync(mission.TenantId, mission.SelectedPlaybooks, token).ConfigureAwait(false);
            }

            // Validate prestaged files at the dispatch boundary before the mission is persisted.
            if (mission.PrestagedFiles != null && mission.PrestagedFiles.Count > 0)
            {
                List<string> prestageErrors = PrestagedFileValidator.Validate(mission.PrestagedFiles);
                if (prestageErrors.Count > 0)
                {
                    throw new ArgumentException(
                        "Invalid prestagedFiles for mission '" + mission.Title + "': " +
                        String.Join("; ", prestageErrors),
                        nameof(mission));
                }
            }

            if (!String.IsNullOrEmpty(mission.DependsOnMissionId))
            {
                Mission? referenced = await _Database.Missions.ReadAsync(mission.DependsOnMissionId, token).ConfigureAwait(false);
                if (referenced == null)
                {
                    throw new InvalidOperationException("dependsOnMissionId not found: " + mission.DependsOnMissionId);
                }
            }

            Vessel? capacityVessel = String.IsNullOrWhiteSpace(mission.VesselId)
                ? null
                : await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(mission.VesselId) && capacityVessel == null)
                throw new InvalidOperationException("Vessel not found: " + mission.VesselId);
            if (capacityVessel != null && !String.IsNullOrWhiteSpace(mission.StartFromRef))
            {
                MissionDescription start = new MissionDescription(mission.Title, mission.Description ?? String.Empty)
                {
                    StartFromRef = mission.StartFromRef,
                    DependsOnMissionId = mission.DependsOnMissionId
                };
                await ValidateStartFromRefsOrThrowAsync(capacityVessel, new List<MissionDescription> { start }, token).ConfigureAwait(false);
                mission.StartFromRef = String.IsNullOrEmpty(mission.DependsOnMissionId) ? start.StartFromRef : null;
            }
            await using FleetCapacityReservation? capacityAdmission = capacityVessel == null
                ? null
                : await _FleetCapacityAdmission.AcquireAsync(capacityVessel, mission.VoyageId, token).ConfigureAwait(false);

            await InheritEvaluationFromVoyageAsync(mission, token).ConfigureAwait(false);

            Mission? createdMission = null;
            try
            {
                createdMission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                await RecordStartRefAsync(createdMission, token).ConfigureAwait(false);
                await PersistMissionPlaybooksAsync(createdMission, createdMission.SelectedPlaybooks, token).ConfigureAwait(false);
                if (capacityAdmission != null)
                    await capacityAdmission.VerifyOwnershipAsync(token).ConfigureAwait(false);
                mission = createdMission;
            }
            catch
            {
                if (createdMission != null)
                    await CancelPartiallyCreatedMissionAsync(createdMission).ConfigureAwait(false);
                throw;
            }
            _Logging.Info(_Header + "created mission " + mission.Id + ": " + mission.Title);

            if (!String.IsNullOrEmpty(mission.VesselId))
            {
                Vessel? vessel = capacityVessel;
                if (vessel != null)
                {
                    bool assigned = await _Missions.TryAssignAsync(mission, vessel, token).ConfigureAwait(false);

                    // Re-read mission from database to get updated status
                    Mission? refreshed = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                    if (refreshed != null)
                        mission = refreshed;

                    if (!assigned || mission.Status == MissionStatusEnum.Pending)
                    {
                        _Logging.Warn(_Header + "mission " + mission.Id + " created but could not be assigned to any captain");
                        _RetryDispatchNeeded = true;
                    }
                }
                else
                {
                    _Logging.Warn(_Header + "mission " + mission.Id + " created but vessel " + mission.VesselId + " not found for assignment");
                }
            }

            return mission;
        }

        private async Task CancelPartiallyCreatedVoyageAsync(Voyage voyage)
        {
            try
            {
                await VoyageCancellation.CancelVoyageAsync(
                    _Database,
                    voyage,
                    "Voyage cancelled because its initial work graph was not created completely.",
                    CancellationToken.None,
                    RecallCaptainAsync).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                _Logging.Error(_Header + "could not cancel partially created voyage " + voyage.Id
                    + ": " + cleanupEx.Message);
            }
        }

        private async Task CancelPartiallyCreatedMissionAsync(Mission mission)
        {
            try
            {
                mission.Status = MissionStatusEnum.Cancelled;
                mission.FailureReason = "Mission cancelled because its initial durable creation did not complete.";
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                _Logging.Error(_Header + "could not cancel partially created mission " + mission.Id
                    + ": " + cleanupEx.Message);
            }
        }

        private void QueueVoyageAssignments(string? voyageId, string vesselId, List<string> missionIds)
        {
            if (String.IsNullOrEmpty(vesselId) || missionIds == null || missionIds.Count == 0) return;

            List<string> queuedMissionIds = missionIds
                .Where(id => !String.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (queuedMissionIds.Count == 0) return;

            string voyageLabel = String.IsNullOrEmpty(voyageId) ? "standalone" : voyageId!;
            _Logging.Info(_Header + "queued assignment for " + queuedMissionIds.Count +
                " mission(s) on voyage " + voyageLabel + " vessel " + vesselId);

            long queuedAssignmentId = Interlocked.Increment(ref _QueuedAssignmentSequence);
            Task queuedAssignment = Task.Run(async () =>
            {
                try
                {
                    Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, CancellationToken.None).ConfigureAwait(false);
                    if (vessel == null)
                    {
                        _Logging.Warn(_Header + "queued assignment skipped because vessel was not found: " + vesselId);
                        return;
                    }

                    bool anyAssigned = false;
                    foreach (string missionId in queuedMissionIds)
                    {
                        Mission? mission = await _Database.Missions.ReadAsync(missionId, CancellationToken.None).ConfigureAwait(false);
                        if (mission == null)
                        {
                            _Logging.Warn(_Header + "queued assignment skipped missing mission " + missionId);
                            continue;
                        }

                        bool assigned = await _Missions.TryAssignAsync(mission, vessel, CancellationToken.None).ConfigureAwait(false);
                        anyAssigned |= assigned;
                    }

                    if (anyAssigned && !String.IsNullOrEmpty(voyageId))
                    {
                        Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId!, CancellationToken.None).ConfigureAwait(false);
                        if (voyage != null && voyage.Status == VoyageStatusEnum.Open)
                        {
                            voyage.Status = VoyageStatusEnum.InProgress;
                            voyage.LastUpdateUtc = DateTime.UtcNow;
                            await _Database.Voyages.UpdateAsync(voyage, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "queued assignment failed for voyage " + voyageLabel + ": " + ex.Message);
                }
            });

            _QueuedAssignments[queuedAssignmentId] = queuedAssignment;
            // A continuation on an already-completed task runs at once, so the entry is removed
            // even when the work finished before it was recorded.
            _ = queuedAssignment.ContinueWith(
                _ => _QueuedAssignments.TryRemove(queuedAssignmentId, out Task? _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Completes when no queued background assignment work is running. Dispatch returns before
        /// its queued assignments finish, and that work reads and writes mission rows and holds
        /// sibling-lane leases, so a caller that must act on the settled result waits here instead
        /// of polling for state that can change again. Work queued while waiting is awaited too.
        /// </summary>
        /// <returns>Task that completes when the queue is empty.</returns>
        internal async Task WhenQueuedAssignmentsDrainedAsync()
        {
            while (true)
            {
                Task[] running = _QueuedAssignments.Values.ToArray();
                if (running.Length == 0) return;
                await Task.WhenAll(running).ConfigureAwait(false);
            }
        }

        private List<SelectedPlaybook> ClonePlaybookSelections(List<SelectedPlaybook>? selections)
        {
            if (selections == null || selections.Count == 0) return new List<SelectedPlaybook>();

            return selections.Select(s => new SelectedPlaybook
            {
                PlaybookId = s.PlaybookId,
                DeliveryMode = s.DeliveryMode,
                InlineFullContent = s.InlineFullContent
            }).ToList();
        }

        private static List<PrestagedFile>? ClonePrestagedFiles(List<PrestagedFile>? entries)
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

        private static void ValidatePrestagedFilesOrThrow(List<MissionDescription> missionDescriptions)
        {
            if (missionDescriptions == null) return;
            for (int i = 0; i < missionDescriptions.Count; i++)
            {
                MissionDescription md = missionDescriptions[i];
                if (md == null || md.PrestagedFiles == null || md.PrestagedFiles.Count == 0) continue;
                List<string> errors = PrestagedFileValidator.Validate(md.PrestagedFiles);
                if (errors.Count > 0)
                {
                    throw new ArgumentException(
                        "Invalid prestagedFiles for mission[" + i + "] '" + md.Title + "': " +
                        String.Join("; ", errors),
                        nameof(missionDescriptions));
                }
            }
        }

        private async Task RecordStartRefAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.StartFromRef)) return;
            ArmadaEvent resolved = new ArmadaEvent("mission.start_ref_resolved", "Mission starts from verified commit " + mission.StartFromRef)
            {
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                EntityType = "mission",
                EntityId = mission.Id,
                MissionId = mission.Id,
                VesselId = mission.VesselId,
                VoyageId = mission.VoyageId
            };
            await _Database.Events.CreateAsync(resolved, token).ConfigureAwait(false);
        }

        private static string? NormalizeStartFromRef(string? startFromRef)
        {
            return String.IsNullOrWhiteSpace(startFromRef) ? null : startFromRef.Trim();
        }

        /// <summary>
        /// Refuse a dispatch whose start ref does not resolve in the vessel repository. The check
        /// runs before the voyage row exists, so a bad ref leaves no half-created voyage behind and
        /// the scheduler can report it as a named skip rather than a dispatch error. A dependent
        /// description is not checked: it continues its predecessor's branch and its ref is ignored.
        /// </summary>
        private async Task ValidateStartFromRefsOrThrowAsync(Vessel vessel, List<MissionDescription> missionDescriptions, CancellationToken token)
        {
            string repoPath = vessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");

            foreach (MissionDescription md in missionDescriptions)
            {
                string? startFromRef = NormalizeStartFromRef(md.StartFromRef);
                if (startFromRef == null || !String.IsNullOrEmpty(md.DependsOnMissionId)) continue;

                string? sha = await _Git.GetRevisionCommitShaAsync(repoPath, startFromRef, token).ConfigureAwait(false);
                if (String.IsNullOrEmpty(sha))
                {
                    throw new StartFromRefMissingException(
                        "start_from_ref_missing: ref '" + startFromRef + "' does not resolve to a commit in '" + repoPath
                        + "' for vessel " + vessel.Name + ". Fetch that repository before retrying.");
                }

                // Persist the verified immutable commit as the assignment base. This prevents a
                // moving branch or tag from changing the accepted start point after dispatch.
                md.StartFromRef = sha;
                _Logging.Info(_Header + "start ref " + startFromRef + " resolves to " + sha + " in vessel " + vessel.Name);
            }
        }

        private async Task ValidateDependsOnReferencesOrThrowAsync(string? ownerTenantId, List<MissionDescription> missionDescriptions, CancellationToken token)
        {
            if (missionDescriptions == null) return;
            for (int i = 0; i < missionDescriptions.Count; i++)
            {
                MissionDescription md = missionDescriptions[i];
                if (md == null || String.IsNullOrEmpty(md.DependsOnMissionId)) continue;
                await ReadDependencyOrThrowAsync(ownerTenantId, md.DependsOnMissionId, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Read the mission a new mission depends on. A dependency is honoured only inside the tenant of the
        /// vessel the dependent mission runs in, so a mission of another tenant reads as absent: its branch
        /// and commit would otherwise become the dependent mission's starting point.
        /// </summary>
        private async Task<Mission> ReadDependencyOrThrowAsync(string? ownerTenantId, string dependsOnMissionId, CancellationToken token)
        {
            Mission? referenced = await _Database.Missions.ReadAsync(dependsOnMissionId, token).ConfigureAwait(false);
            if (referenced == null || !OwnershipPolicy.SameTenant(referenced.TenantId, ownerTenantId))
                throw new InvalidOperationException("dependsOnMissionId not found: " + dependsOnMissionId);
            return referenced;
        }

        private async Task PersistMissionPlaybooksAsync(Mission mission, List<SelectedPlaybook>? selections, CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            // v2-F3 four-way merge: fleet -> vessel -> persona -> captain, with later layers
            // winning on playbookId collision (more specific wins). Inbound `selections`
            // already encodes the vessel.DefaultPlaybooks + voyage.SelectedPlaybooks +
            // mission.SelectedPlaybooks chain (call sites bake vessel defaults into voyage
            // selections via PlaybookMerge.MergeWithVesselDefaults before reaching here);
            // F3 prepends the fleet layer (least specific) and F2 appends persona then
            // captain (most specific). Fleet/persona/captain entries with novel playbookIds
            // are appended; collisions land captain last.
            List<SelectedPlaybook> caller = ClonePlaybookSelections(selections);
            List<SelectedPlaybook> fleetLayer = await ResolveFleetLayerAsync(mission, token).ConfigureAwait(false);
            (List<SelectedPlaybook> personaLayer, List<SelectedPlaybook> captainLayer)
                = await ResolveIdentityLayersAsync(mission, token).ConfigureAwait(false);
            List<SelectedPlaybook> withFleet = PlaybookMerge.MergeWithVesselDefaults(fleetLayer, caller);
            List<SelectedPlaybook> withPersona = PlaybookMerge.MergeWithVesselDefaults(withFleet, personaLayer);
            mission.SelectedPlaybooks = PlaybookMerge.MergeWithVesselDefaults(withPersona, captainLayer);

            if (mission.SelectedPlaybooks.Count == 0 || String.IsNullOrEmpty(mission.TenantId))
            {
                mission.PlaybookSnapshots = new List<MissionPlaybookSnapshot>();
                return;
            }

            List<MissionPlaybookSnapshot> snapshots = await _Playbooks.CreateSnapshotsAsync(
                mission.TenantId,
                mission.SelectedPlaybooks,
                token).ConfigureAwait(false);
            mission.PlaybookSnapshots = snapshots;
            await _Database.Playbooks.SetMissionSnapshotsAsync(mission.Id, snapshots, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve the fleet-default playbook layer for the v2-F3 four-way merge. Returns an
        /// empty list when the mission has no vessel pin, the vessel has no fleet, the fleet
        /// is not found, or the fleet carries no <see cref="Fleet.DefaultPlaybooks"/> JSON.
        /// Fleet layer is the LEAST specific (applied first) per the v2-F3 spec.
        /// </summary>
        /// <param name="mission">Mission whose vessel resolves the fleet membership.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Fleet layer playbooks; empty when not applicable.</returns>
        private async Task<List<SelectedPlaybook>> ResolveFleetLayerAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.VesselId)) return new List<SelectedPlaybook>();
            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
            if (vessel == null || String.IsNullOrEmpty(vessel.FleetId)) return new List<SelectedPlaybook>();
            Fleet? fleet = await _Database.Fleets.ReadAsync(vessel.FleetId!, token).ConfigureAwait(false);
            if (fleet == null) return new List<SelectedPlaybook>();
            // A fleet layer applies only to a vessel of the fleet's own tenant: default playbooks can carry
            // inline content, and a vessel naming another tenant's fleet must not ship that content.
            if (!OwnershipPolicy.SameTenant(fleet.TenantId, vessel.TenantId))
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " names fleet " + fleet.Id + " of another tenant -- fleet playbooks not merged");
                return new List<SelectedPlaybook>();
            }
            return fleet.GetDefaultPlaybooks();
        }

        /// <summary>
        /// Resolve the persona-default and captain-default playbook layers for the v2-F2
        /// three-way merge. Either layer may be empty when the corresponding identity is not
        /// set or carries no DefaultPlaybooks JSON.
        /// </summary>
        /// <param name="mission">Mission to resolve persona and captain for.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Tuple of (personaLayer, captainLayer); captain layer is intended to apply last.</returns>
        private async Task<(List<SelectedPlaybook> Persona, List<SelectedPlaybook> Captain)> ResolveIdentityLayersAsync(Mission mission, CancellationToken token)
        {
            List<SelectedPlaybook> personaLayer = new List<SelectedPlaybook>();
            if (!String.IsNullOrEmpty(mission.Persona))
            {
                // A persona's default playbooks enter this mission only when the mission's owner may use it.
                OwnedRecordLookup<Persona> personaLookup = await OwnedRecordScope.ReadUsableByNameAsync(
                    mission.TenantId,
                    mission.UserId,
                    mission.Persona!,
                    () => _Database.Personas.EnumerateAsync(token),
                    record => record.Name).ConfigureAwait(false);
                if (personaLookup.WasRefused)
                    _Logging.Warn(_Header + "mission " + mission.Id + " names persona '" + mission.Persona + "' that its owner may not use -- persona playbooks not merged");
                Persona? persona = personaLookup.Record;
                if (persona != null)
                {
                    personaLayer = persona.GetDefaultPlaybooks();
                }
            }

            List<SelectedPlaybook> captainLayer = new List<SelectedPlaybook>();
            if (!String.IsNullOrEmpty(mission.CaptainId))
            {
                Captain? captain = await _Database.Captains.ReadAsync(mission.CaptainId!, token).ConfigureAwait(false);
                // A captain's default playbooks enter only a mission of the captain's own tenant.
                if (captain != null && MissionService.CaptainServesTenant(captain, mission.TenantId))
                {
                    captainLayer = captain.GetDefaultPlaybooks();
                }
            }

            return (personaLayer, captainLayer);
        }

        /// <inheritdoc />
        public async Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
        {
            ArmadaStatus status = new ArmadaStatus();
            status.DispatchHold = _DispatchHold?.Snapshot();

            // Captain counts
            List<Captain> allCaptains = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            status.TotalCaptains = allCaptains.Count;
            status.IdleCaptains = allCaptains.Count(c => c.State == CaptainStateEnum.Idle);
            status.WorkingCaptains = allCaptains.Count(c => c.State == CaptainStateEnum.Working);
            status.StalledCaptains = allCaptains.Count(c => c.State == CaptainStateEnum.Stalled);

            // Mission counts by status (lightweight: GROUP BY status without hydrating
            // description/diff_snapshot/agent_output payload columns).
            Dictionary<MissionStatusEnum, int> missionCounts = await _Database.Missions.CountByStatusAsync(token).ConfigureAwait(false);
            foreach (MissionStatusEnum missionStatus in Enum.GetValues<MissionStatusEnum>())
            {
                if (missionCounts.TryGetValue(missionStatus, out int count) && count > 0)
                    status.MissionsByStatus[missionStatus.ToString()] = count;
            }

            // Pending missions the last admission evaluation deferred for resource pressure. Read from stored
            // assignment state through lightweight summaries, so the value is current and survives restarts.
            if (missionCounts.TryGetValue(MissionStatusEnum.Pending, out int pendingCount) && pendingCount > 0)
                status.MissionsWaitingForResourcePressure = await CountPendingWaitingForResourcePressureAsync(token).ConfigureAwait(false);

            // Active voyages
            List<Voyage> activeVoyages = await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false);
            List<Voyage> openVoyages = await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Open, token).ConfigureAwait(false);
            status.ActiveVoyages = activeVoyages.Count + openVoyages.Count;

            foreach (Voyage voyage in activeVoyages.Concat(openVoyages))
            {
                VoyageProgress? progress = await _Voyages.GetProgressAsync(voyage.Id, token: token).ConfigureAwait(false);
                if (progress != null) status.Voyages.Add(progress);
            }

            // Recent signals
            status.RecentSignals = await _Database.Signals.EnumerateRecentAsync(10, token).ConfigureAwait(false);
            status.StructuredDelivery = await BuildStructuredDeliveryStatusAsync(token).ConfigureAwait(false);

            if (OnGetRemoteTunnelStatus != null)
            {
                status.RemoteTunnel = OnGetRemoteTunnelStatus();
            }

            if (OnGetSchedulerStatus != null)
            {
                status.Scheduler = OnGetSchedulerStatus();
            }

            if (OnGetCodeIndexStaleness != null)
            {
                try
                {
                    status.CodeIndexStaleness = await OnGetCodeIndexStaleness().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "code index staleness summary failed, status continues without it: " + ex.Message);
                }
            }

            if (_BuildDrift != null)
            {
                try
                {
                    status.BuildDrift = await _BuildDrift.GetReportAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "build drift report failed, status continues without it: " + ex.Message);
                }
            }

            return status;
        }

        private async Task<StructuredDeliveryStatus> BuildStructuredDeliveryStatusAsync(CancellationToken token)
        {
            StructuredDeliveryStatus status = new StructuredDeliveryStatus();
            AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Status");
            ObjectiveService objectives = new ObjectiveService(_Database);
            IncidentService incidents = new IncidentService(_Database);

            ObjectiveCountSummary objectiveCounts = await objectives.CountAsync(auth, token).ConfigureAwait(false);
            foreach (ObjectiveStatusEnum value in Enum.GetValues<ObjectiveStatusEnum>())
            {
                if (objectiveCounts.ByStatus.TryGetValue(value, out int count) && count > 0) status.ObjectivesByStatus[value.ToString()] = count;
            }
            foreach (ObjectiveBacklogStateEnum value in Enum.GetValues<ObjectiveBacklogStateEnum>())
            {
                if (objectiveCounts.ByBacklogState.TryGetValue(value, out int count) && count > 0) status.BacklogByState[value.ToString()] = count;
            }

            foreach (CheckRunStatusEnum value in Enum.GetValues<CheckRunStatusEnum>())
            {
                EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    Status = value,
                    PageNumber = 1,
                    PageSize = 1,
                    IncludeOutput = false
                }, token).ConfigureAwait(false);
                if (page.TotalRecords > 0) status.CheckRunsByStatus[value.ToString()] = ClampStatusCount(page.TotalRecords);
            }
            status.PendingChecksRequired = status.CheckRunsByStatus.TryGetValue(CheckRunStatusEnum.Pending.ToString(), out int pendingChecks)
                ? pendingChecks
                : 0;
            status.PendingChecksOptional = 0;

            foreach (ReleaseStatusEnum value in Enum.GetValues<ReleaseStatusEnum>())
            {
                EnumerationResult<Release> page = await _Database.Releases.EnumerateAsync(new ReleaseQuery
                {
                    Status = value,
                    PageNumber = 1,
                    PageSize = 1
                }, token).ConfigureAwait(false);
                if (page.TotalRecords > 0) status.ReleasesByStatus[value.ToString()] = ClampStatusCount(page.TotalRecords);
            }
            status.InFlightReleasesCount = status.ReleasesByStatus
                .Where(kvp => !String.Equals(kvp.Key, ReleaseStatusEnum.Shipped.ToString(), StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(kvp.Key, ReleaseStatusEnum.Failed.ToString(), StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(kvp.Key, ReleaseStatusEnum.RolledBack.ToString(), StringComparison.OrdinalIgnoreCase))
                .Sum(kvp => kvp.Value);

            foreach (DeploymentStatusEnum value in Enum.GetValues<DeploymentStatusEnum>())
            {
                EnumerationResult<Deployment> page = await _Database.Deployments.EnumerateAsync(new DeploymentQuery
                {
                    Status = value,
                    PageNumber = 1,
                    PageSize = 1
                }, token).ConfigureAwait(false);
                if (page.TotalRecords > 0) status.DeploymentsByStatus[value.ToString()] = ClampStatusCount(page.TotalRecords);
            }
            status.UnverifiedDeploymentsCount = status.DeploymentsByStatus
                .Where(kvp => !String.Equals(kvp.Key, DeploymentStatusEnum.Succeeded.ToString(), StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(kvp.Key, DeploymentStatusEnum.RolledBack.ToString(), StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(kvp.Key, DeploymentStatusEnum.Denied.ToString(), StringComparison.OrdinalIgnoreCase))
                .Sum(kvp => kvp.Value);

            IncidentCountSummary incidentCounts = await incidents.CountAsync(auth, token).ConfigureAwait(false);
            foreach (IncidentStatusEnum value in Enum.GetValues<IncidentStatusEnum>())
            {
                if (incidentCounts.ByStatus.TryGetValue(value, out int count) && count > 0) status.IncidentsByStatus[value.ToString()] = count;
            }
            foreach (IncidentSeverityEnum value in Enum.GetValues<IncidentSeverityEnum>())
            {
                if (incidentCounts.OpenBySeverity.TryGetValue(value, out int count) && count > 0) status.OpenIncidentsBySeverity[value.ToString()] = count;
            }

            return status;
        }

        private static int ClampStatusCount(long value)
        {
            if (value <= 0) return 0;
            if (value > Int32.MaxValue) return Int32.MaxValue;
            return (int)value;
        }

        /// <inheritdoc />
        public async Task RecallCaptainAsync(string captainId, CancellationToken token = default)
        {
            await _Captains.RecallAsync(captainId, token: token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task RecallAllAsync(CancellationToken token = default)
        {
            _Logging.Warn(_Header + "RECALLING ALL CAPTAINS");

            List<Captain> workingCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);

            foreach (Captain captain in workingCaptains)
            {
                try
                {
                    await _Captains.RecallAsync(captain.Id, token: token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error recalling captain " + captain.Id + ": " + ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public async Task StopAllAgentProcessesAsync(CancellationToken token = default)
        {
            if (_Captains.OnStopAgent == null) return;

            List<Captain> workingCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);
            if (workingCaptains.Count == 0) return;

            _Logging.Warn(_Header + "shutdown: stopping " + workingCaptains.Count + " agent process(es) so none are orphaned");
            foreach (Captain captain in workingCaptains)
            {
                if (!captain.ProcessId.HasValue) continue;
                try
                {
                    await _Captains.OnStopAgent.Invoke(captain).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error stopping agent for captain " + captain.Id + " on shutdown: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Failure count per health-check step name since the admiral started. A step that throws is
        /// logged and counted here, and the steps after it still run in the same cycle.
        /// </summary>
        public IReadOnlyDictionary<string, long> HealthCheckStepFailures
        {
            get => new Dictionary<string, long>(_HealthCheckStepFailures, StringComparer.Ordinal);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Each sub-step runs in isolation: one that throws is logged and counted by name, and the
        /// steps after it (dispatch, captain pool, voyage completion, escalation) still run. Only
        /// cancellation stops the cycle.
        /// </remarks>
        public async Task HealthCheckAsync(CancellationToken token = default)
        {
            await HealthLoopMaintenanceRunner.RunDueStepsAsync(
                1,
                _HealthCheckSteps,
                RecordHealthCheckStepFailure,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns a captain to Idle once it has been Stalled for the stall threshold with no current
        /// mission and no live process. A requeue after a transient failure and a non-retryable
        /// runtime failure both leave the captain Stalled with its mission already requeued or failed,
        /// and nothing else ever moves it back, so the captain dropped out of routing until an operator
        /// intervened. A captain that fails again is stalled again, so the threshold bounds the cost of
        /// a captain that keeps failing. Quarantined captains are a separate state and are not touched.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <param name="nowUtc">The current time; supplied by tests.</param>
        /// <returns>The number of captains returned to Idle.</returns>
        internal async Task<int> RecoverStalledCaptainsAsync(CancellationToken token = default, DateTime? nowUtc = null)
        {
            List<Captain> stalled = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Stalled, token).ConfigureAwait(false);
            if (stalled.Count == 0) return 0;

            DateTime now = nowUtc ?? DateTime.UtcNow;
            TimeSpan threshold = TimeSpan.FromMinutes(_Settings.StallThresholdMinutes);
            int recovered = 0;
            foreach (Captain captain in stalled)
            {
                if (!String.IsNullOrEmpty(captain.CurrentMissionId)) continue;
                if (captain.ProcessId.HasValue && ProcessSupervisor.IsTrackedProcessAlive(captain.ProcessId.Value, null)) continue;

                TimeSpan stalledFor = now - captain.LastUpdateUtc;
                if (stalledFor < threshold) continue;

                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                recovered++;
                string minutes = Math.Floor(stalledFor.TotalMinutes).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                _Logging.Info(_Header + "captain " + captain.Id + " returned to Idle after " + minutes + " minute(s) Stalled with no mission");
                await EmitEventAsync("captain.stall_recovered",
                    "Captain " + captain.Name + " returned to Idle after " + minutes + " minute(s) Stalled with no mission and no process.",
                    entityType: "captain", entityId: captain.Id, captainId: captain.Id, token: token).ConfigureAwait(false);
            }

            return recovered;
        }

        /// <inheritdoc />
        public async Task CleanupStaleCaptainsAsync(CancellationToken token = default)
        {
            List<Captain> workingCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);
            if (workingCaptains.Count == 0) return;

            int resetCount = 0;

            foreach (Captain captain in workingCaptains)
            {
                // Verify liveness with PID identity: a process whose start time is later than the
                // mission launch is a recycled PID, not our agent, so the captain is treated as
                // stale rather than left stuck in Working forever.
                Mission? currentMission = null;
                if (!String.IsNullOrEmpty(captain.CurrentMissionId))
                {
                    currentMission = await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                }

                bool processAlive = captain.ProcessId.HasValue &&
                    ProcessSupervisor.IsTrackedProcessAlive(captain.ProcessId.Value, currentMission?.StartedUtc);

                if (processAlive) continue;

                // Reset active missions back to Pending for re-dispatch
                List<Mission> captainMissions = await _Database.Missions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false);
                List<Mission> activeMissions = captainMissions.Where(m =>
                    m.Status == MissionStatusEnum.InProgress ||
                    m.Status == MissionStatusEnum.Assigned).ToList();

                foreach (Mission mission in activeMissions)
                {
                    mission.Status = MissionStatusEnum.Pending;
                    mission.CaptainId = null;
                    mission.ProcessId = null;
                    mission.StartedUtc = null;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    // Preserve DockId and BranchName so the next captain can continue
                    // from partial work in the existing worktree

                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                    if (!String.IsNullOrEmpty(mission.DockId))
                    {
                        _Logging.Info(_Header + "preserved dock " + mission.DockId + " for mission " + mission.Id + " (branch: " + (mission.BranchName ?? "none") + ")");

                        await EmitEventAsync("mission.dock_preserved", "Dock preserved for re-dispatch: " + mission.Id,
                            entityType: "mission", entityId: mission.Id,
                            missionId: mission.Id, token: token).ConfigureAwait(false);
                    }
                    else
                    {
                        _Logging.Info(_Header + "reset stale mission " + mission.Id + " to Pending (no dock to preserve)");
                    }
                }

                // Reset captain to Idle
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                resetCount++;
                _Logging.Info(_Header + "reset stale captain " + captain.Id + " to Idle on startup (process " + (captain.ProcessId?.ToString() ?? "null") + " not running)");
            }

            if (resetCount > 0)
            {
                _Logging.Info(_Header + "startup cleanup: reset " + resetCount + " stale captain(s) to Idle");

                // Dispatch any pending missions that were freed up
                await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
        {
            await HandleProcessExitAsync(processId, exitCode, captainId, missionId, null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, RuntimeProviderError? providerError, CancellationToken token)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            // A process the admiral superseded (a stalled captain it stopped to recover or defer) exited because
            // it was stopped. The admiral already decided the mission's next state, so the exit is not its outcome.
            bool superseded = _IntentionalStops.TryTake(processId, captainId, missionId, IntentionalStopKindEnum.Superseded);

            // A process a captain stop stopped (an operator stop or recall, a cancel, a stall-recovery exhaustion, a
            // shutdown) exits with the stop's kill code. That exit is the stop, never a crash or an out-of-memory kill,
            // and the path that requested the stop decides the mission's next state. A clean exit is still the work's own.
            bool stopRequested = _IntentionalStops.TryTake(processId, captainId, missionId, IntentionalStopKindEnum.Requested, out string? stopReason);

            // An exited process can no longer run or release a suite, so its lease ends with it. A superseded
            // process is the exception: the admiral already moved the mission on, and its next run may hold the lease.
            if (!superseded) CaptainSuiteLeaseRegistry.ReleaseForMission(missionId);

            // System-scoped: process exit handler receives only IDs with no tenant context;
            // tenant is unknown until the entity is read.
            Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null)
            {
                RuntimeExitDiagnostics.Try(() => _Logging.Warn(_Header + "captain " + captainId + " not found during process exit handling"));
                return;
            }

            // Use captain's tenant to scope the mission read when available.
            // Fall back to unscoped read if tenant-scoped read fails (tenant mismatch).
            Mission? mission = null;
            if (!String.IsNullOrEmpty(captain.TenantId))
            {
                mission = await _Database.Missions.ReadAsync(captain.TenantId, missionId, token).ConfigureAwait(false);
            }
            if (mission == null)
            {
                mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            }
            if (mission == null)
            {
                RuntimeExitDiagnostics.Try(() => _Logging.Warn(_Header + "mission " + missionId + " not found during process exit handling"));
                return;
            }

            // Only the captain's current process speaks for the mission. A superseded process, or any process
            // that is no longer the captain's recorded process, exited after the mission moved on without it.
            // A captain with no recorded process is inside a launch whose process id is not written yet, so
            // that exit is still the mission's.
            string? ignoredReason = null;
            if ((!String.IsNullOrEmpty(mission.ResearchReplayId) || mission.GeneratedArtifactReview != null)
                && (mission.ProcessId != processId || captain.ProcessId != processId))
                ignoredReason = "the exit does not identify the current replacement launch";
            else if (superseded)
                ignoredReason = "the admiral stopped and superseded it";
            else if (stopRequested && exitCode != 0)
                ignoredReason = "Armada stopped it on request (" + (stopReason ?? "captain stop")
                    + "); the stop's requester decides the mission's next state";
            else if (captain.ProcessId.HasValue && captain.ProcessId.Value != processId)
                ignoredReason = "captain " + captain.Id + " now runs process " + captain.ProcessId.Value;
            if (ignoredReason != null)
            {
                RuntimeExitDiagnostics.Try(() => _Logging.Info(_Header + "process " + processId + " exit (code " + (exitCode?.ToString() ?? "unknown") + ") for mission "
                    + missionId + " ignored: " + ignoredReason));
                await EmitEventAsync("captain.process_exit_ignored",
                    "Process " + processId + " exit (code " + (exitCode?.ToString() ?? "unknown") + ") ignored: " + ignoredReason,
                    entityType: "captain", entityId: captain.Id,
                    captainId: captain.Id, missionId: missionId,
                    vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
                return;
            }

            // If the mission is already in a terminal state, nothing to do — the health check
            // or another handler already processed this completion/failure.
            if (IsPostWorkOrTerminal(mission.Status))
            {
                RuntimeExitDiagnostics.Try(() => _Logging.Debug(_Header + "mission " + missionId + " already in terminal/post-work state " + mission.Status + " — skipping process exit handling"));
                return;
            }

            // If the captain is no longer working on this mission, skip
            if (captain.CurrentMissionId != missionId)
            {
                RuntimeExitDiagnostics.Try(() => _Logging.Debug(_Header + "captain " + captainId + " is no longer assigned to mission " + missionId + " — skipping process exit handling"));
                return;
            }

            await EmitEventAsync("captain.process_exited",
                "Agent process " + processId + " exited with code " + (exitCode?.ToString() ?? "unknown") + " for captain " + captain.Name,
                entityType: "captain", entityId: captain.Id,
                captainId: captain.Id, missionId: missionId,
                vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);

            RuntimeProcessExitEvidence? exitEvidence = ProcessSupervisor.GetRuntimeExitEvidence(processId, captain.ProcessStartedUtc);
            bool logCaptureFailed = exitEvidence != null && exitEvidence.TryGetOutcome(out _, out bool captureFailed) && captureFailed;
            if (exitCode == 0 && !logCaptureFailed)
            {
                // Clean exit — delegate to the normal completion flow
                RuntimeExitDiagnostics.Try(() => _Logging.Info(_Header + "agent process " + processId + " exited cleanly for mission " + missionId + " — handling completion"));
                await ClearUsageAccountHoldAfterServedRunAsync(captain, mission, token).ConfigureAwait(false);
                await _Missions.HandleCompletionAsync(captain, missionId, token).ConfigureAwait(false);
            }
            else if (!logCaptureFailed && await AgentDeclaredExplicitSuccessAsync(missionId, token).ConfigureAwait(false))
            {
                // A non-zero/lost exit code alone must not override the agent's EXPLICIT terminal
                // success verdict. When the agent declared [ARMADA:RESULT] COMPLETE, honor it and run
                // the normal completion flow -- the DoD gates then verify the work -- rather than
                // mislabeling good work as Failed and cascade-cancelling the voyage.
                RuntimeExitDiagnostics.Try(() => _Logging.Info(_Header + "agent process " + processId + " exited with code " + (exitCode?.ToString() ?? "unknown") + " for mission " + missionId + " but declared [ARMADA:RESULT] COMPLETE — honoring the explicit success verdict and handling completion"));
                await ClearUsageAccountHoldAfterServedRunAsync(captain, mission, token).ConfigureAwait(false);
                await _Missions.HandleCompletionAsync(captain, missionId, token).ConfigureAwait(false);
            }
            else
            {
                // Non-zero or unknown exit code with no explicit success verdict.
                RuntimeExitDiagnostics.Try(() => _Logging.Warn(_Header + "agent process " + processId + " exited with code " + (exitCode?.ToString() ?? "unknown") + " for mission " + missionId));

                // Treat exit 137 as a probable resource kill. SIGKILL is not definitive OOM
                // evidence, but a cooldown plus a live capacity probe prevents a tight retry loop
                // while preserving the voyage for transient host-pressure failures.
                if (IsExitCodeOom(exitCode))
                {
                    string oomReason = BuildOomFailureReason(exitCode);
                    _ResourcePressureAdmission.MarkOom();
                    RuntimeExitDiagnostics.Try(() => _Logging.Warn(_Header + "mission " + missionId + " classified as probable OOM/resource kill " + oomReason));
                    await RequeueTransientMissionFailureAsync(captain, mission, missionId, oomReason, token).ConfigureAwait(false);
                    await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
                    return;
                }

                // Exit code -1 reported by the runtime is an interruption (a stop, shutdown or restart cancelled
                // the run), not a failure of the work. Other negative codes, such as a native crash status, fail. The mission is re-dispatched within its budget
                // and never marked Failed, so autonomous recovery opens no rescue for the same exit.
                if (IsInterruptedExitCode(exitCode)
                    && await TryRedispatchInterruptedMissionAsync(captain, mission, missionId, exitCode!.Value, token).ConfigureAwait(false))
                {
                    await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
                    return;
                }

                // Fail the mission deterministically. Process exits should not bounce between
                // recovery paths; preserve the captured runtime error, halt the voyage, and only
                // stall the captain when the failure indicates the runtime itself is unavailable.
                string failureReason = logCaptureFailed
                    ? "Runtime log persistence failed; the launch outcome cannot be accepted as successful."
                    : await BuildProcessExitFailureReasonAsync(missionId, exitCode, captain.Runtime, token).ConfigureAwait(false);
                if (logCaptureFailed) providerError = null;
                RuntimeFailureKindEnum failureKind = RuntimeFailureClassifier.Classify(exitCode, failureReason, providerError);
                await HandleTerminalProcessExitFailureAsync(captain, mission, missionId, exitCode, failureReason, failureKind, token, providerError).ConfigureAwait(false);
                failureKind = await RefineRuntimeFailureAsync(mission, captain, exitCode, failureReason, failureKind, token).ConfigureAwait(false);
                if (_Settings.CrashLoopDetection.Enabled
                    && !logCaptureFailed
                    && failureKind == RuntimeFailureKindEnum.Crash
                    && !ProviderQuotaLimitDetector.IsProviderSafeguardBlockSignal(failureReason)
                    && !IsTestOrDefinitionOfDoneFailure(failureReason))
                {
                    bool shouldQuarantine = _CrashLoopTracker.Record(
                        captain.Id,
                        processId.ToString() + ":" + captain.Id + ":" + mission.Id,
                        DateTime.UtcNow,
                        out int count,
                        out long generation);
                    if (shouldQuarantine)
                    {
                        DateTime untilUtc = DateTime.UtcNow.AddSeconds(_Settings.CrashLoopDetection.CooldownSeconds);
                        Captain? currentCaptain = await _Database.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
                        bool canApplyCrashHold = currentCaptain != null
                            && currentCaptain.ProcessId == null
                            && String.IsNullOrEmpty(currentCaptain.CurrentMissionId)
                            && String.IsNullOrEmpty(currentCaptain.CurrentDockId)
                            && (currentCaptain.State != CaptainStateEnum.Quarantined
                                || (currentCaptain.QuarantineUntilUtc.HasValue && currentCaptain.QuarantineUntilUtc.Value < untilUtc));
                        if (canApplyCrashHold && currentCaptain != null)
                        {
                            bool applied = await _CaptainQuarantine.TryQuarantineCrashLoopAsync(
                                currentCaptain.Id,
                                "Crash loop detected after " + count + " runtime failures in the configured window.",
                                untilUtc,
                                token).ConfigureAwait(false);
                            if (applied)
                            {
                                _CrashLoopTracker.ResetIfGeneration(captain.Id, generation);
                            }
                        }
                    }
                }
            }

            // Try to dispatch any pending missions now that capacity may have freed up
            await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
        }

        #endregion

        private static bool IsTestOrDefinitionOfDoneFailure(string failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) return false;
            return failureReason.StartsWith("DoD gate failed:", StringComparison.OrdinalIgnoreCase)
                || failureReason.StartsWith("Tests failed after merge", StringComparison.OrdinalIgnoreCase)
                || failureReason.StartsWith("Tests failed before merge", StringComparison.OrdinalIgnoreCase);
        }

        #region Private-Methods

        /// <summary>
        /// Appends a pipeline stage's own description to the mission description, so the captain of
        /// that stage reads its role in the pipeline. Two stages can share a persona (a Worker that
        /// writes a report and a later Worker that revises it), and only the stage description tells
        /// them apart. A stage without a description leaves the mission description unchanged.
        /// </summary>
        /// <param name="description">The dispatched mission description.</param>
        /// <param name="stage">The pipeline stage the mission runs.</param>
        /// <param name="stageCount">Number of stages in the pipeline.</param>
        /// <returns>The mission description for this stage.</returns>
        internal static string AppendPipelineStageSection(string? description, PipelineStage stage, int stageCount)
        {
            string body = description ?? String.Empty;
            if (stage == null || String.IsNullOrWhiteSpace(stage.Description)) return body;
            return body.TrimEnd() + "\n\n## Pipeline Stage\n" +
                "Stage " + stage.Order + " of " + stageCount + " (" + stage.PersonaName + "): " + stage.Description!.Trim() + "\n";
        }


        /// <summary>
        /// Build the voyage row a dispatch creates. The per-persona captain assignments are part of the row
        /// from its first write: assignment of a voyage's first missions is queued the moment they exist and
        /// resolves each mission's requested captain from this row, so an assignment written after the
        /// missions would miss their first assignment. Every dispatch path builds its voyage here.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vessel">Target vessel; the voyage takes its owner.</param>
        /// <param name="captainOverrides">Per-persona captain assignments, or null.</param>
        /// <returns>The unsaved voyage.</returns>
        public static Voyage NewDispatchVoyage(string title, string description, Vessel vessel, List<CaptainAssignmentOverride>? captainOverrides)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            Voyage voyage = new Voyage(title, description);
            voyage.TenantId = vessel.TenantId;
            voyage.UserId = vessel.UserId;
            voyage.Status = VoyageStatusEnum.Open;
            voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(captainOverrides);
            return voyage;
        }

        /// <summary>
        /// The persona a single-stage dispatch stamps on each mission: the one stage's persona, or Worker when
        /// no pipeline resolves. Captain assignment and a persona's default captain resolve only for a mission
        /// that names its persona, and the dispatch preview reads the same dispatch as that role.
        /// </summary>
        /// <param name="pipeline">The resolved pipeline, or null.</param>
        /// <returns>The persona name.</returns>
        public static string SingleStagePersona(Pipeline? pipeline)
        {
            if (pipeline != null && pipeline.Stages != null && pipeline.Stages.Count == 1
                && !String.IsNullOrWhiteSpace(pipeline.Stages[0].PersonaName))
                return pipeline.Stages[0].PersonaName;
            return PersonaCatalog.Worker;
        }

        /// <summary>
        /// Resolve which pipeline to use for a dispatch.
        /// Resolution order: explicit pipelineId > vessel default > fleet default > null (WorkerOnly).
        /// The first usable reference decides: when it names a pipeline the operator has deactivated, the
        /// dispatch is refused with <see cref="PipelineInactiveException"/> and no later reference is tried.
        /// </summary>
        /// <exception cref="PipelineInactiveException">The resolved pipeline is inactive.</exception>
        public async Task<Pipeline?> ResolvePipelineAsync(string? pipelineId, Vessel vessel, CancellationToken token = default)
        {
            // A dispatch runs on behalf of the vessel's owner. A pipeline that owner may not use is
            // refused wherever the reference comes from: an explicit id or name, the vessel default or
            // the fleet default. A refused reference is logged and kept; only a missing one is cleared.

            // Explicit pipeline ID takes priority
            if (!String.IsNullOrEmpty(pipelineId))
            {
                Pipeline? explicit_ = await _Database.Pipelines.ReadAsync(pipelineId, token).ConfigureAwait(false);
                if (explicit_ != null)
                {
                    if (Armada.Core.Authorization.OwnershipPolicy.CanUseFor(vessel.TenantId, vessel.UserId, explicit_))
                        return RequireActive(explicit_, PipelineSourceRequested);
                    _Logging.Warn(_Header + "vessel " + vessel.Id + " requested pipeline " + explicit_.Id + " that its owner may not use -- request refused");
                }
                else
                {
                    // Try by name if not found by ID
                    Pipeline? named = await ReadUsablePipelineByNameAsync(pipelineId, vessel, token).ConfigureAwait(false);
                    if (named != null) return RequireActive(named, PipelineSourceRequested);
                }
            }

            // Vessel default
            if (!String.IsNullOrEmpty(vessel.DefaultPipelineId))
            {
                Pipeline? vesselPipeline = await _Database.Pipelines.ReadAsync(vessel.DefaultPipelineId, token).ConfigureAwait(false);
                if (vesselPipeline != null)
                {
                    if (Armada.Core.Authorization.OwnershipPolicy.CanUseFor(vessel.TenantId, vessel.UserId, vesselPipeline))
                        return RequireActive(vesselPipeline, PipelineReferenceRule.VesselDefaultSource);
                    _Logging.Warn(_Header + "vessel " + vessel.Id + " default pipeline " + vesselPipeline.Id + " is private to another owner -- not inherited");
                }
                else
                {
                    // Pipeline no longer exists -- clear the stale reference
                    _Logging.Warn(_Header + "vessel " + vessel.Id + " references missing pipeline " + vessel.DefaultPipelineId + " -- clearing");
                    vessel.DefaultPipelineId = null;
                    await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);
                }
            }

            // Fleet default
            if (!String.IsNullOrEmpty(vessel.FleetId))
            {
                Fleet? fleet = await _Database.Fleets.ReadAsync(vessel.FleetId, token).ConfigureAwait(false);
                if (fleet != null && !String.IsNullOrEmpty(fleet.DefaultPipelineId))
                {
                    Pipeline? fleetPipeline = await _Database.Pipelines.ReadAsync(fleet.DefaultPipelineId, token).ConfigureAwait(false);
                    if (fleetPipeline != null)
                    {
                        if (Armada.Core.Authorization.OwnershipPolicy.CanUseFor(vessel.TenantId, vessel.UserId, fleetPipeline))
                            return RequireActive(fleetPipeline, PipelineReferenceRule.FleetDefaultSource);
                        _Logging.Warn(_Header + "fleet " + fleet.Id + " default pipeline " + fleetPipeline.Id + " is private to another owner -- not inherited by vessel " + vessel.Id);
                    }
                    else
                    {
                        // Pipeline no longer exists -- clear the stale reference
                        _Logging.Warn(_Header + "fleet " + fleet.Id + " references missing pipeline " + fleet.DefaultPipelineId + " -- clearing");
                        fleet.DefaultPipelineId = null;
                        await _Database.Fleets.UpdateAsync(fleet, token).ConfigureAwait(false);
                    }
                }
            }

            return null;
        }

        private const string PipelineSourceRequested = "requested pipeline";

        private Pipeline RequireActive(Pipeline pipeline, string source)
        {
            if (pipeline.Active) return pipeline;
            _Logging.Warn(_Header + "dispatch refused: " + source + " " + pipeline.Name + " (" + pipeline.Id + ") is inactive");
            throw new PipelineInactiveException(pipeline.Id, pipeline.Name, source);
        }

        /// <summary>
        /// Find a pipeline by name that the vessel's owner may use: the owner's own tenant first, then a
        /// shared record of the same name. Same-name records the owner may not use are logged and skipped.
        /// </summary>
        private async Task<Pipeline?> ReadUsablePipelineByNameAsync(string name, Vessel vessel, CancellationToken token)
        {
            OwnedRecordLookup<Pipeline> lookup = await OwnedRecordScope.ReadUsableByNameAsync(
                vessel.TenantId,
                vessel.UserId,
                name,
                () => _Database.Pipelines.EnumerateAsync(token),
                pipeline => pipeline.Name).ConfigureAwait(false);
            if (lookup.WasRefused)
                _Logging.Warn(_Header + "vessel " + vessel.Id + " requested pipeline '" + name + "', but every record of that name belongs to another owner -- request refused");
            return lookup.Record;
        }

        /// <summary>
        /// Resolve the pipeline for a dispatch, with the read-only guard: when every mission in the
        /// dispatch is Audit or Research mode and no pipeline was explicitly requested, the vessel or
        /// fleet default pipeline is NOT inherited. A read-only voyage must stay single-stage --
        /// inheriting a multi-stage default silently multiplied a four-mission diagnostic probe into
        /// sixteen missions. An explicit pipelineId is always honored.
        /// </summary>
        /// <param name="pipelineId">Explicitly requested pipeline id or name, or null.</param>
        /// <param name="vessel">Target vessel.</param>
        /// <param name="missions">Mission descriptions being dispatched.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolved pipeline, or null for a single-stage Worker dispatch.</returns>
        private async Task<Pipeline?> ResolvePipelineAsync(string? pipelineId, Vessel vessel, List<MissionDescription> missions, CancellationToken token = default)
        {
            if (SkipsDefaultPipeline(pipelineId, missions))
            {
                _Logging.Info(_Header + "read-only dispatch (" + missions!.Count + " mission(s)) skips the vessel/fleet default pipeline -- single-stage Worker dispatch");
                return null;
            }

            return await ResolvePipelineAsync(pipelineId, vessel, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether a dispatch runs as a single-stage Worker dispatch without inheriting the vessel or fleet
        /// default pipeline: no pipeline was requested and every mission is Audit or Research.
        /// </summary>
        /// <param name="pipelineId">Explicitly requested pipeline id or name, or null.</param>
        /// <param name="missions">Mission descriptions being dispatched.</param>
        /// <returns>True when the default pipeline is not inherited.</returns>
        public static bool SkipsDefaultPipeline(string? pipelineId, IReadOnlyList<MissionDescription>? missions)
        {
            return String.IsNullOrEmpty(pipelineId)
                && missions != null
                && missions.Count > 0
                && missions.All(m => m != null && IsReadOnlyMissionMode(m.Mode));
        }

        /// <summary>
        /// Whether a mission-mode string is read-only (Audit or Research).
        /// </summary>
        private static bool IsReadOnlyMissionMode(string? mode)
        {
            MissionModeEnum parsed = MissionModes.Parse(mode);
            return parsed == MissionModeEnum.Audit || parsed == MissionModeEnum.Research;
        }

        private CaptainStallEvaluator? _StallEvaluator;

        private const string _InterruptedRedispatchEventType = "mission.interrupted_redispatched";

        // The exit code a runtime reports for a cancelled run.
        private const int _InterruptedExitCode = -1;

        // Upper bound on the mission events read to count interrupted re-dispatches. Far above any budget the
        // setting allows, so older unrelated events cannot hide a re-dispatch from the count.
        private const int _InterruptedRedispatchHistoryLimit = 1000;

        // Created on first use so the stall rule reads the same database and git service the
        // admiral was built with.
        private CaptainStallEvaluator StallEvaluator => _StallEvaluator ??= new CaptainStallEvaluator(_Database, _Git, _Logging, processTree: _ProcessTree);

        /// <summary>
        /// Process health check for a single captain. Isolated so exceptions in one captain
        /// do not prevent processing of other captains.
        /// </summary>
        private async Task HealthCheckCaptainAsync(Captain captain, CancellationToken token)
        {
            // Look up the captain's single active mission
            Mission? mission = null;
            if (!String.IsNullOrEmpty(captain.CurrentMissionId))
            {
                mission = await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
            }

            if (mission != null && mission.Status == MissionStatusEnum.WorkProduced)
            {
                TimeSpan workProducedAge = DateTime.UtcNow - mission.LastUpdateUtc;
                if (workProducedAge < _WorkProducedReleaseGracePeriod)
                {
                    _Logging.Debug(_Header + "captain " + captain.Id + " mission " + captain.CurrentMissionId +
                        " is freshly WorkProduced (" + workProducedAge.TotalSeconds.ToString("F1") +
                        "s) - deferring captain release while handoff/landing completes");
                    return;
                }
            }

            // Check for terminal mission state (e.g. server restart between completion and release)
            if (mission != null && IsPostWorkOrTerminal(mission.Status))
            {
                _Logging.Warn(_Header + "captain " + captain.Id + " has terminal/post-work mission " + captain.CurrentMissionId + " (status: " + mission.Status + ") - releasing to Idle");
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                return;
            }

            bool isActive = mission != null &&
                (mission.Status == MissionStatusEnum.InProgress ||
                 mission.Status == MissionStatusEnum.Assigned ||
                 mission.Status == MissionStatusEnum.Review ||
                 mission.Status == MissionStatusEnum.Testing);

            if (!isActive && captain.ProcessId == null)
            {
                // Orphaned captain - Working state but no active mission and no process.
                _Logging.Warn(_Header + "captain " + captain.Id + " is Working but has no active mission or process - releasing to Idle");
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                return;
            }

            // Determine the process ID to check
            int? processId = captain.ProcessId ?? mission?.ProcessId;
            if (processId == null)
            {
                if (isActive && mission != null)
                {
                    DateTime launchReferenceUtc = mission.StartedUtc ?? mission.LastUpdateUtc;
                    TimeSpan launchAge = DateTime.UtcNow - launchReferenceUtc;
                    TimeSpan launchGrace = TimeSpan.FromSeconds(_Settings.LaunchProcessIdGraceSeconds);
                    if (launchAge < launchGrace)
                    {
                        _Logging.Debug(_Header + "captain " + captain.Id + " has active mission " + mission.Id +
                            " without a process id inside launch grace (" + launchAge.TotalSeconds.ToString("F1") +
                            "s of " + launchGrace.TotalSeconds.ToString("F0") + "s) - deferring missing-PID failure");
                        return;
                    }

                    string failureReason = "Captain is marked Working on active mission " + mission.Id +
                        " but no process ID is recorded; agent completion cannot be verified.";
                    _Logging.Warn(_Header + "captain " + captain.Id + " has active mission " + mission.Id +
                        " without a process id -- failing loudly instead of treating it as success");
                    await HandleTerminalProcessExitFailureAsync(captain, mission, mission.Id, null, failureReason, RuntimeFailureClassifier.Classify(null, failureReason), token).ConfigureAwait(false);
                    return;
                }

                return;
            }

            bool isAlive = false;
            int exitCode = -1;
            bool processGone = false;
            RuntimeProcessExitEvidence? observedExitEvidence = null;
            if (ProcessSupervisor.IsTrackedProcessAlive(processId.Value))
            {
                isAlive = true;
            }
            else if (ProcessSupervisor.ProbeLaunchedProcess(processId.Value) == LaunchedProcessIdentityEnum.Reused)
            {
                // A live process holds the identifier but is not the launched agent: the agent is gone.
                _Logging.Warn(_Header + "captain " + captain.Id + " process " + processId
                    + " identifier is held by an unrelated process (process_identifier_reused); treating the agent as gone");
                processGone = true;
            }
            else try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId.Value))
                {
                    if (process.HasExited)
                    {
                        isAlive = false;
                        try { exitCode = process.ExitCode; }
                        catch (Exception exitCodeEx)
                        {
                            _Logging.Warn(_Header + "exit code of exited process " + processId + " for captain " + captain.Id
                                + " is unreadable; recording -1: " + exitCodeEx.Message);
                        }
                    }
                    else
                    {
                        isAlive = true;
                    }
                }
            }
            catch (ArgumentException)
            {
                processGone = true;
            }

            if (!isAlive)
            {
                if (OnIsProcessExitHandled != null && OnIsProcessExitHandled(processId.Value)) return;
                RuntimeProcessExitEvidence? evidence = ProcessSupervisor.GetRuntimeExitEvidence(processId.Value, captain.ProcessStartedUtc);
                observedExitEvidence = evidence;
                if (evidence != null)
                {
                    if (evidence.TryGetOutcome(out int? effectiveExitCode, out bool captureFailed))
                    {
                        if (captureFailed)
                        {
                            await HandleProcessExitAsync(processId.Value, effectiveExitCode, captain.Id, captain.CurrentMissionId!, token).ConfigureAwait(false);
                            ProcessSupervisor.ReleaseRuntimeExitEvidence(processId.Value, evidence);
                            return;
                        }
                        exitCode = effectiveExitCode ?? -1;
                        processGone = false;
                    }
                    else if (evidence.ShouldDefer(DateTime.UtcNow)) return;
                    else
                    {
                        // An unfinished drain cannot be a successful raw OS exit, even after grace.
                        exitCode = -1;
                        processGone = false;
                    }
                }
            }

            if (processGone)
            {
                // Process no longer exists in process table.
                // Check if the process exit callback already fired — if so, the async
                // completion handler is in progress and we should not race it with recovery.
                if (OnIsProcessExitHandled != null && OnIsProcessExitHandled(processId.Value))
                {
                    _Logging.Debug(_Header + "captain " + captain.Id + " process " + processId +
                        " no longer exists but exit callback already fired — skipping health check to avoid race");
                    return;
                }

                // A missing PID could mean the agent crashed, was killed externally, or the OS
                // recycled the PID. Only a confirmed clean exit should be treated as success.
                isAlive = false;
                exitCode = -1;
            }

            string missionId = captain.CurrentMissionId ?? "unknown";

            if (!isAlive)
            {
                // The process is gone. Re-read the mission before acting on that: the exit
                // callback's completion handling may have finished since this check loaded the
                // captain, and a mission that already produced its work must not be failed by a
                // stale observation of its process.
                Mission? currentMission = String.IsNullOrEmpty(captain.CurrentMissionId)
                    ? null
                    : await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                if (currentMission != null && IsPostWorkOrTerminal(currentMission.Status))
                {
                    _Logging.Warn(_Header + "captain " + captain.Id + " process " + processId + " is gone but mission " + missionId
                        + " is already " + currentMission.Status + " -- ignoring the stale process observation and releasing the captain");
                    await EmitEventAsync("captain.process_exit_ignored",
                        "Process " + processId + " observation ignored: mission " + missionId + " is already " + currentMission.Status,
                        entityType: "captain", entityId: captain.Id,
                        captainId: captain.Id, missionId: missionId,
                        vesselId: currentMission.VesselId, voyageId: currentMission.VoyageId, token: token).ConfigureAwait(false);
                    await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                    if (observedExitEvidence != null) ProcessSupervisor.ReleaseRuntimeExitEvidence(processId.Value, observedExitEvidence);
                    return;
                }

                if (exitCode == 0)
                {
                    // Clean exit = mission complete
                    _Logging.Info(_Header + "captain " + captain.Id + " process " + processId + " completed successfully for mission " + missionId);

                    // Emit captain.completed event
                    await EmitEventAsync("captain.completed", "Captain " + captain.Id + " process " + processId + " exited successfully",
                        entityType: "captain", entityId: captain.Id,
                        captainId: captain.Id, missionId: missionId, token: token).ConfigureAwait(false);

                    await _Missions.HandleCompletionAsync(captain, missionId, token).ConfigureAwait(false);
                }
                else
                {
                    _Logging.Warn(_Header + "captain " + captain.Id + " process " + processId + " exited with code " + exitCode + " for mission " + missionId);

                    // Emit captain.completed event (process exited, even if non-zero)
                    await EmitEventAsync("captain.completed", "Captain " + captain.Id + " process " + processId + " exited with code " + exitCode,
                        entityType: "captain", entityId: captain.Id,
                        captainId: captain.Id, missionId: missionId, token: token).ConfigureAwait(false);

                    string failureReason = await BuildProcessExitFailureReasonAsync(missionId, exitCode, captain.Runtime, token).ConfigureAwait(false);
                    await HandleTerminalProcessExitFailureAsync(captain, mission, missionId, exitCode, failureReason, RuntimeFailureClassifier.Classify(exitCode, failureReason), token).ConfigureAwait(false);
                }
                if (observedExitEvidence != null) ProcessSupervisor.ReleaseRuntimeExitEvidence(processId.Value, observedExitEvidence);
            }
            else
            {
                // Process is alive — do NOT update heartbeat here.
                // Heartbeat should only be updated when the agent produces actual output
                // (handled by HandleAgentOutput in ArmadaServer). Updating it here from the
                // health check would mask stalled agents that are technically running but
                // producing no output.

                // Check for stall. Quiet output alone is not a stall: some runtimes stream nothing
                // between tool calls. The shared evaluator also reads the dock worktree and the
                // branch tip, and records the decision with its evidence.
                if (captain.LastHeartbeatUtc.HasValue)
                {
                    DateTime nowUtc = DateTime.UtcNow;
                    DateTime? lastProviderProgressUtc = null;
                    _ProviderProgress?.TryGet(captain.Id, out lastProviderProgressUtc);
                    ProviderStallKind stallKind = CaptainStallEvaluator.ClassifyOutput(
                        captain, lastProviderProgressUtc, _Settings.StallThresholdMinutes, nowUtc);
                    if (stallKind == ProviderStallKind.ProviderSilentStall
                        && mission != null
                        && ProviderStallClassifier.IsWithinStartupGrace(mission.StartedUtc, nowUtc, _Settings.StallThresholdMinutes))
                    {
                        _Logging.Debug(_Header + "provider stall check skipped for captain " + captain.Id + " on mission " + mission.Id + " inside startup grace");
                        return;
                    }

                    if (stallKind != ProviderStallKind.None
                        && mission != null
                        && _TerminalMarkers != null
                        && _TerminalMarkers.TryGet(mission.Id, out TerminalMarkerRecord? terminalMarker)
                        && terminalMarker != null)
                    {
                        _Logging.Debug(_Header + "provider stall check skipped for captain " + captain.Id + " on mission " + mission.Id
                            + " after terminal marker [" + terminalMarker.MarkerType + "] " + terminalMarker.Value);
                        return;
                    }

                    if (stallKind != ProviderStallKind.None)
                    {
                        CaptainStallDecision stall = await StallEvaluator.EvaluateAsync(
                            captain, mission, _Settings.StallThresholdMinutes, nowUtc, lastProviderProgressUtc, _Settings.StallChildProcessCeilingMinutes, token).ConfigureAwait(false);
                        await StallEvaluator.RecordAsync(stall, "admiral_heartbeat_stall", captain, mission, token).ConfigureAwait(false);
                        if (!stall.IsStalled) return;

                        _Logging.Warn(_Header + "captain " + captain.Id + " appears stalled: " + stall.DescribeEvidence());

                        // Attempt auto-recovery if under the limit
                        if (captain.RecoveryAttempts < _Settings.MaxRecoveryAttempts)
                        {
                            // Kill the stalled process first. It is registered as superseded before the stop, so
                            // its exit, whenever it arrives, is not read as the mission's failure or an OOM kill.
                            await StopSupersededProcessAsync(captain, processId.Value, "before recovery", token).ConfigureAwait(false);
                            // A restarted process must establish its own provider-progress baseline.
                            _ProviderProgress?.Clear(captain.Id);

                            // The last relaunch is the one whose failure reclaims the dock, so its work is saved first.
                            if (captain.RecoveryAttempts + 1 >= _Settings.MaxRecoveryAttempts)
                                await SnapshotStalledDockWorkAsync(captain, mission, "the final recovery attempt", token).ConfigureAwait(false);

                            // A relaunch continues an in-flight mission in its own dock; it is not a new dispatch,
                            // so an engaged dispatch hold, which refuses only new dispatches, does not stop it.
                            await _Captains.TryRecoverAsync(captain, token).ConfigureAwait(false);
                        }
                        else
                        {
                            // Mark the active mission as Failed
                            if (mission != null &&
                                mission.Status != MissionStatusEnum.Complete &&
                                mission.Status != MissionStatusEnum.WorkProduced &&
                                mission.Status != MissionStatusEnum.LandingFailed &&
                                mission.Status != MissionStatusEnum.PullRequestOpen)
                            {
                                mission.Status = MissionStatusEnum.Failed;
                                mission.FailureReason = "Captain stalled, recovery exhausted";
                                mission.ProcessId = null;
                                mission.CompletedUtc = DateTime.UtcNow;
                                mission.LastUpdateUtc = DateTime.UtcNow;
                                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                                _Logging.Warn(_Header + "mission " + mission.Id + " marked failed (captain stalled, recovery exhausted)");

                                // Emit mission.failed event
                                await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Failed, "mission_failed", _Logging, token).ConfigureAwait(false);
                                await EmitEventAsync("mission.failed", "Mission failed: " + mission.Title + " (captain stalled, recovery exhausted)",
                                    entityType: "mission", entityId: mission.Id,
                                    captainId: captain.Id, missionId: mission.Id, token: token).ConfigureAwait(false);

                                // The voyage is kept alive so autonomous recovery can rescue the failed stage from
                                // its saved work, so the stages waiting behind it would otherwise park forever and
                                // keep the voyage counting as an active lane. Cancel them through the one dependent
                                // walker, exactly as every other voyage-preserving failure path does.
                                await CancelDirectDependentsAsync(mission, mission.FailureReason ?? "Captain stalled, recovery exhausted", token).ConfigureAwait(false);
                            }

                            // Kill the stalled process
                            if (_Captains.OnStopAgent != null)
                            {
                                try { await _Captains.OnStopAgent.Invoke(captain).ConfigureAwait(false); }
                                catch (Exception stopEx)
                                {
                                    _Logging.Warn(_Header + "could not stop stalled captain " + captain.Id + " after recovery exhaustion: " + stopEx.Message);
                                }
                            }

                            // Save the dock's uncommitted work before the dock can be reclaimed, and name the
                            // branch in the failure so the rescue and the operator find it.
                            DockWorkSnapshot? snapshot = DockRetentionRule.MissionStillUsesDock(mission)
                                ? null
                                : await SnapshotStalledDockWorkAsync(captain, mission, "stall recovery exhaustion", token).ConfigureAwait(false);
                            if (snapshot != null && snapshot.HasRef && mission != null && mission.Status == MissionStatusEnum.Failed)
                            {
                                mission.FailureReason = (mission.FailureReason ?? "Captain stalled, recovery exhausted") + "; uncommitted work saved to " + snapshot.RefName;
                                mission.LastUpdateUtc = DateTime.UtcNow;
                                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                            }

                            // Reclaim the dock worktree so it doesn't leak -- unless the mission still lands
                            // or is approved from it (its work was produced before the stall), or its
                            // uncommitted work could not be saved, which the dock is then the only copy of.
                            if (snapshot != null && snapshot.Status == DockWorkSnapshotStatusEnum.Failed)
                            {
                                string keptDockId = mission?.DockId ?? captain.CurrentDockId ?? "";
                                _Logging.Warn(_Header + "keeping dock " + keptDockId + " after stall recovery exhaustion: its uncommitted work was not saved (" + snapshot.Message + ")");
                                if (mission != null && mission.Status == MissionStatusEnum.Failed)
                                {
                                    mission.FailureReason = (mission.FailureReason ?? "Captain stalled, recovery exhausted")
                                        + "; uncommitted work was NOT saved (" + snapshot.Message + "), so dock " + keptDockId + " is kept on disk -- copy the work out before removing it";
                                    mission.LastUpdateUtc = DateTime.UtcNow;
                                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                                }

                                if (!String.IsNullOrEmpty(captain.CurrentDockId))
                                {
                                    captain.CurrentDockId = null;
                                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                                }
                            }
                            else if (DockRetentionRule.MissionStillUsesDock(mission))
                            {
                                _Logging.Info(_Header + "keeping dock of mission " + mission!.Id + " (" + mission.Status + ") after stall recovery exhaustion; its landing or review still reads it");
                                if (!String.IsNullOrEmpty(captain.CurrentDockId))
                                {
                                    captain.CurrentDockId = null;
                                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                await ReclaimDockAsync(captain, mission, token).ConfigureAwait(false);
                            }

                            // Release the captain to Idle instead of Stalled so it can pick up new work.
                            // The mission is already marked Failed -- no need to also block the captain.
                            await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                            _Logging.Info(_Header + "captain " + captain.Id + " released to Idle after stall recovery exhaustion");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Save a stalled captain's uncommitted dock work to a <c>recover/{missionId}-{n}</c> branch in the vessel
        /// repository. The dock's files, index and branch are not changed. Every outcome except "nothing to save" is
        /// recorded as an event. A failure does not stop a relaunch, which keeps the dock; it does stop the dock
        /// being reclaimed at recovery exhaustion.
        /// </summary>
        /// <returns>The snapshot, or null when there was no mission or dock to read.</returns>
        private async Task<DockWorkSnapshot?> SnapshotStalledDockWorkAsync(Captain captain, Mission? mission, string stage, CancellationToken token)
        {
            if (mission == null) return null;

            string? dockId = !String.IsNullOrEmpty(mission.DockId) ? mission.DockId : captain.CurrentDockId;
            Dock? dock = String.IsNullOrEmpty(dockId) ? null : await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            if (dock == null || String.IsNullOrWhiteSpace(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath))
            {
                _Logging.Info(_Header + "no dock worktree to snapshot for mission " + mission.Id + " before " + stage);
                return null;
            }

            DockWorkSnapshot snapshot;
            try
            {
                snapshot = await _Git.SnapshotDockWorkAsync(dock.WorktreePath, mission.Id,
                    "Armada recovery snapshot of mission " + mission.Id + " before " + stage, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot = DockWorkSnapshot.Failed(ex.Message);
            }

            if (snapshot.Status == DockWorkSnapshotStatusEnum.NoChanges)
            {
                _Logging.Info(_Header + "dock " + dock.Id + " of mission " + mission.Id + " has no uncommitted work to snapshot before " + stage);
                return snapshot;
            }

            if (snapshot.HasRef)
            {
                _Logging.Info(_Header + "mission " + mission.Id + " before " + stage + ": " + snapshot.Message + " (" + snapshot.CommitSha + ")");
                await EmitEventAsync("mission.work_snapshot_saved",
                    "Uncommitted work of mission " + mission.Id + " saved to " + snapshot.RefName + " (" + snapshot.CommitSha + ") before " + stage,
                    entityType: "mission", entityId: mission.Id,
                    captainId: captain.Id, missionId: mission.Id, vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
            }
            else
            {
                _Logging.Warn(_Header + "could not snapshot the dock of mission " + mission.Id + " before " + stage + ": " + snapshot.Message);
                await EmitEventAsync("mission.work_snapshot_failed",
                    "Uncommitted work of mission " + mission.Id + " was not saved before " + stage + ": " + snapshot.Message,
                    entityType: "mission", entityId: mission.Id,
                    captainId: captain.Id, missionId: mission.Id, vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
            }

            return snapshot;
        }

        /// <summary>
        /// Stop a captain's process that the admiral is replacing, registering it as superseded first so its
        /// exit is not handled as the mission's outcome.
        /// </summary>
        private async Task StopSupersededProcessAsync(Captain captain, int processId, string context, CancellationToken token)
        {
            if (!String.IsNullOrEmpty(captain.CurrentMissionId))
                _IntentionalStops.TryRegister(processId, captain.Id, captain.CurrentMissionId, IntentionalStopKindEnum.Superseded);

            if (_Captains.OnStopAgent == null) return;
            try
            {
                await _Captains.OnStopAgent.Invoke(captain).ConfigureAwait(false);
            }
            catch (Exception stopEx)
            {
                _Logging.Warn(_Header + "could not stop stalled captain " + captain.Id + " " + context + ": " + stopEx.Message);
            }
        }

        /// <summary>
        /// Count pending missions whose stored assignment state is WaitingForResourcePressure. Reads lightweight
        /// summaries page by page, so no description, diff or agent output is loaded.
        /// </summary>
        private async Task<int> CountPendingWaitingForResourcePressureAsync(CancellationToken token)
        {
            const int pageSize = 1000;
            int waiting = 0;
            int pageNumber = 1;
            while (true)
            {
                EnumerationQuery query = new EnumerationQuery
                {
                    Status = MissionStatusEnum.Pending.ToString(),
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };
                EnumerationResult<MissionSummary> page = await _Database.Missions.EnumerateMissionSummariesAsync(query, token).ConfigureAwait(false);
                List<MissionSummary> objects = page.Objects ?? new List<MissionSummary>();
                waiting += objects.Count(summary =>
                    summary.Status == MissionStatusEnum.Pending
                    && summary.AssignmentState == MissionAssignmentStateEnum.WaitingForResourcePressure);
                if (objects.Count < pageSize || pageNumber >= page.TotalPages) break;
                pageNumber++;
            }

            return waiting;
        }

        private List<HealthLoopMaintenanceStep> BuildHealthCheckSteps()
        {
            return new List<HealthLoopMaintenanceStep>
            {
                HealthLoopMaintenanceStep.EveryCycles("quarantine restore", () => 1,
                    async token => await _CaptainQuarantine.RestoreExpiredQuarantinesAsync(token).ConfigureAwait(false)),

                HealthLoopMaintenanceStep.EveryCycles("stalled captain recovery", () => 1,
                    async token => await RecoverStalledCaptainsAsync(token).ConfigureAwait(false)),

                HealthLoopMaintenanceStep.EveryCycles("working captain health checks", () => 1,
                    async token => await HealthCheckWorkingCaptainsAsync(token).ConfigureAwait(false)),

                // Safety net: detect orphaned InProgress missions whose captain has moved on.
                // This catches any mission that was left InProgress due to a captain being
                // reassigned before the health check could detect the old process exit.
                HealthLoopMaintenanceStep.EveryCycles("orphaned mission recovery", () => 1,
                    async token => await RecoverOrphanedMissionsAsync(token).ConfigureAwait(false)),

                HealthLoopMaintenanceStep.EveryCycles("stage watchdog recovery", () => 1,
                    async token => await RecoverStageWatchdogMissionsAsync(token).ConfigureAwait(false)),

                HealthLoopMaintenanceStep.EveryCycles("voyage completion check", () => 1,
                    async token => await CheckVoyageCompletionsAsync(token).ConfigureAwait(false)),

                // Reconcile PullRequestOpen missions: check whether their PRs have been merged.
                HealthLoopMaintenanceStep.EveryCycles("pull request reconciliation", () => 1,
                    async token => await ReconcilePullRequestMissionsAsync(token).ConfigureAwait(false)),

                // PR-fallback merge-entry reconciliation: after the mission reconciler above
                // flips a mission to Complete, the linked merge entry still sits in
                // PullRequestOpen. The merge-queue reconciler walks those and lands the entry
                // so subsequent cleanup (CleanupLandedBranchesAsync) can prune the captain
                // branch per Vessel.BranchCleanupPolicy.
                HealthLoopMaintenanceStep.EveryCycles("merge entry reconciliation", () => 1, async token =>
                {
                    if (OnReconcileMergeEntries != null)
                        await OnReconcileMergeEntries.Invoke().ConfigureAwait(false);
                }),

                // A LandingFailed mission whose work reached the default branch by another path stays LandingFailed
                // until someone notices. The sweep proposes each such mission to the operator; it never completes one.
                HealthLoopMaintenanceStep.EveryCycles("landing failure reconciliation", () => 1, async token =>
                {
                    if (OnReconcileLandingFailures != null)
                        await OnReconcileLandingFailures.Invoke().ConfigureAwait(false);
                }),

                // Audit-queue depth notification: when the count of AuditDeepPicked entries
                // pending review crosses the configured threshold AND the debounce window has
                // expired, ping the human via the existing NotificationService. Judge's
                // suggested-follow-ups wiring is the primary feeder of this queue.
                HealthLoopMaintenanceStep.EveryCycles("audit queue depth notification", () => 1,
                    async token => await MaybeNotifyAuditQueueDepthAsync(token).ConfigureAwait(false)),

                // Reclaim docks stuck in Provisioned state with no active captain.
                HealthLoopMaintenanceStep.EveryCycles("orphaned dock reclaim", () => 1,
                    async token => await ReclaimOrphanedDocksAsync(token).ConfigureAwait(false)),

                // A restart kills every running or queued definition-of-done gate; the held missions get the gate again.
                HealthLoopMaintenanceStep.EveryCycles("interrupted gate re-run", () => 1,
                    async token => await _Missions.StartInterruptedDefinitionOfDoneGateRerunsAsync(token).ConfigureAwait(false)),

                HealthLoopMaintenanceStep.EveryCycles("pending mission dispatch", () => 1,
                    async token => await DispatchPendingMissionsAsync(token).ConfigureAwait(false)),

                // Captain pool management: auto-spawn if below minimum idle count.
                HealthLoopMaintenanceStep.EveryCycles("captain pool maintenance", () => 1, async token =>
                {
                    if (_Settings.MinIdleCaptains > 0)
                        await MaintainCaptainPoolAsync(token).ConfigureAwait(false);
                }),

                HealthLoopMaintenanceStep.EveryCycles("escalation evaluation", () => 1, async token =>
                {
                    if (_Escalation != null)
                        await _Escalation.EvaluateAsync(token).ConfigureAwait(false);
                })
            };
        }

        private void RecordHealthCheckStepFailure(string stepName, Exception ex)
        {
            long failures = _HealthCheckStepFailures.AddOrUpdate(stepName, 1, (name, count) => count + 1);
            _Logging.Warn(_Header + "health check step " + stepName + " failed (" + failures + " failure"
                + (failures == 1 ? "" : "s") + " since start): " + ex.GetType().Name + ": " + ex.Message);
        }

        private async Task HealthCheckWorkingCaptainsAsync(CancellationToken token)
        {
            List<Captain> workingCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);
            if (workingCaptains.Count == 0) return;

            _Logging.Info(_Header + "starting parallel health checks for " + workingCaptains.Count + " working captain(s)");

            List<Task> healthCheckTasks = workingCaptains.Select(captain =>
                Task.Run(async () =>
                {
                    try
                    {
                        await HealthCheckCaptainAsync(captain, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "error processing health check for captain " + captain.Id + ": " + ex.Message);
                    }
                }, token)).ToList();

            await Task.WhenAll(healthCheckTasks).ConfigureAwait(false);

            _Logging.Info(_Header + "completed parallel health checks for " + workingCaptains.Count + " working captain(s)");
        }

        private async Task CheckVoyageCompletionsAsync(CancellationToken token)
        {
            // The completion rule raises OnVoyageComplete for each voyage it ends.
            await _Voyages.CheckCompletionsAsync(token, OnVoyageComplete, _Missions.IsCompletionInFlight).ConfigureAwait(false);
        }

        /// <summary>
        /// Emit an event to the event log database.
        /// </summary>
        private async Task EmitEventAsync(string eventType, string message,
            string? entityType = null, string? entityId = null,
            string? captainId = null, string? missionId = null,
            string? vesselId = null, string? voyageId = null,
            CancellationToken token = default)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message);
                evt.EntityType = entityType;
                evt.EntityId = entityId;
                evt.CaptainId = captainId;
                evt.MissionId = missionId;
                evt.VesselId = vesselId;
                evt.VoyageId = voyageId;
                EventOwnerScopeResult scope = await EventOwnerScope.ApplyAsync(_Database, evt, token).ConfigureAwait(false);
                if (scope.Outcome == EventOwnerScopeOutcomeEnum.LookupFailed)
                    _Logging.Warn(_Header + "event " + eventType + " written without owner scope: " + scope.Detail);
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error emitting event " + eventType + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Reclaim docks that have been active for too long without an associated working captain.
        /// This catches docks leaked by failed launches or agent crashes.
        /// </summary>
        internal async Task ReclaimOrphanedDocksAsync(CancellationToken token = default)
        {
            try
            {
                List<Dock> allDocks = await _Database.Docks.EnumerateAsync(token).ConfigureAwait(false);
                DateTime threshold = DateTime.UtcNow.AddMinutes(-5);

                foreach (Dock dock in allDocks)
                {
                    if (!dock.Active) continue;

                    // Skip recently created docks (may still be in the provisioning process)
                    if (dock.CreatedUtc > threshold) continue;

                    // Check if any captain is actively using this dock
                    bool inUse = false;
                    if (!String.IsNullOrEmpty(dock.CaptainId))
                    {
                        Captain? captain = await _Database.Captains.ReadAsync(dock.CaptainId, token).ConfigureAwait(false);
                        if (captain != null && captain.State == CaptainStateEnum.Working && captain.CurrentDockId == dock.Id)
                        {
                            inUse = true;
                        }
                    }

                    if (!inUse)
                    {
                        // Check if a Pending mission is preserving this dock for re-dispatch.
                        List<Mission> pendingMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Pending, token).ConfigureAwait(false);

                        // A restart-preserved root mission continues on its committed branch from a retained
                        // active dock, and the assignment path authorizes that continuation by the dock's EXACT
                        // identity -- tenant, vessel and branch -- read from the live dock records, not from the
                        // mission's DockId pointer. Every assignment-failure cleanup can clear that pointer, so a
                        // dock preserved only by DockId is reclaimed the moment a failed provision clears the
                        // pointer, which later strands the mission on root_continuation_dock_missing. Preserve the
                        // dock when a Pending root mission still claims its branch by that same identity; a dead
                        // dock no root mission claims is still reclaimed.
                        bool preservedByPointer = pendingMissions.Any(m => m.DockId == dock.Id);
                        bool preservedByBranchIdentity = !String.IsNullOrEmpty(dock.BranchName)
                            && pendingMissions.Any(m =>
                                String.IsNullOrEmpty(m.DependsOnMissionId)
                                && !String.IsNullOrEmpty(m.BranchName)
                                && String.Equals(m.BranchName, dock.BranchName, StringComparison.Ordinal)
                                && String.Equals(m.VesselId, dock.VesselId, StringComparison.Ordinal)
                                && String.Equals(m.TenantId, dock.TenantId, StringComparison.Ordinal));
                        if (preservedByPointer || preservedByBranchIdentity)
                        {
                            _Logging.Info(_Header + "skipping reclaim of dock " + dock.Id + " -- preserved for a pending mission re-dispatch");
                            continue;
                        }

                        // A held Judge PASS lands from its dock when the hold is released, so the dock
                        // outlives the captain that produced it.
                        List<Mission> producedMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.WorkProduced, token).ConfigureAwait(false);
                        if (producedMissions.Any(m => m.HeldForOperatorReview && m.DockId == dock.Id))
                        {
                            _Logging.Info(_Header + "skipping reclaim of dock " + dock.Id + " -- preserved for a held Judge PASS");
                            continue;
                        }

                        // A produced mission whose gate has no result yet is gated from its dock. A restart kills a
                        // running or queued gate before the mission is held for it, and the gate re-run tests the
                        // dock, so the dock is kept until the gate records a result.
                        bool awaitsGate = false;
                        foreach (Mission produced in producedMissions.Where(m => m.DockId == dock.Id))
                        {
                            if (await _Missions.AwaitsDefinitionOfDoneResultAsync(produced, token).ConfigureAwait(false))
                            {
                                awaitsGate = true;
                                break;
                            }
                        }
                        if (awaitsGate)
                        {
                            _Logging.Info(_Header + "skipping reclaim of dock " + dock.Id + " -- preserved for a mission whose definition-of-done gate has no result");
                            continue;
                        }

                        // A mission awaiting operator review keeps its dock on purpose and releases its
                        // captain; approving it lands from that dock, so the dock is not an orphan.
                        List<Mission> reviewMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Review, token).ConfigureAwait(false);
                        if (reviewMissions.Any(m => m.DockId == dock.Id))
                        {
                            _Logging.Info(_Header + "skipping reclaim of dock " + dock.Id + " -- preserved for a mission awaiting review");
                            continue;
                        }

                        _Logging.Info(_Header + "reclaiming orphaned dock " + dock.Id + " (created " + dock.CreatedUtc + ", no active captain)");
                        try
                        {
                            await _Docks.ReclaimAsync(dock.Id, token: token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _Logging.Warn(_Header + "error reclaiming orphaned dock " + dock.Id + ": " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error in orphaned dock reclamation: " + ex.Message);
            }
        }

        /// <summary>
        /// Reconcile missions in PullRequestOpen status by checking if their PRs have been merged.
        /// Audit-queue depth notification: when the count of merge entries with
        /// <c>AuditDeepPicked = true</c> AND no <c>AuditDeepCompletedUtc</c> crosses
        /// the configured threshold AND we haven't fired a notification within the
        /// debounce window, send a desktop notification telling the human to drain
        /// the audit queue. Judge's suggested-follow-ups path (Judge hybrid #1) is
        /// the primary feeder. Best-effort: failures don't break the health check.
        /// </summary>
        private async Task MaybeNotifyAuditQueueDepthAsync(CancellationToken token)
        {
            int threshold = _Settings.AuditQueueNotifyThreshold;
            if (threshold <= 0) return;

            try
            {
                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                int pendingCount = 0;
                foreach (MergeEntry e in entries)
                {
                    if (e.AuditDeepPicked != true || e.AuditDeepCompletedUtc.HasValue) continue;
                    JudgeFollowUp? canonical = await _Database.JudgeFollowUps
                        .ReadByMergeEntryAsync(e.Id, token)
                        .ConfigureAwait(false);
                    if (canonical == null) pendingCount++;
                }
                List<JudgeFollowUp> followUps = await _Database.JudgeFollowUps
                    .EnumeratePendingAsync(token: token)
                    .ConfigureAwait(false);
                pendingCount += followUps.Count;
                if (pendingCount < threshold) return;

                int debounceMinutes = Math.Max(1, _Settings.AuditQueueNotifyDebounceMinutes);
                DateTime now = DateTime.UtcNow;
                lock (_AuditNotifyLock)
                {
                    if (_LastAuditNotifyUtc.HasValue && (now - _LastAuditNotifyUtc.Value) < TimeSpan.FromMinutes(debounceMinutes))
                        return;
                    _LastAuditNotifyUtc = now;
                }

                NotificationService.Send(
                    "Armada audit queue: " + pendingCount + " entr" + (pendingCount == 1 ? "y" : "ies") + " pending review",
                    "Drain via armada_drain_audit_queue. Judge flagged work for orchestrator review.");
                _Logging.Info(_Header + "audit-queue notification fired (depth=" + pendingCount + ", threshold=" + threshold + ")");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "audit-queue notification check failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Reconcile PullRequestOpen missions: poll for PR merge completion via the
        /// OnReconcilePullRequest delegate. Replaces the fire-and-forget 5-minute poller
        /// with a persistent, restart-safe check. Rate-limited to at most 10 missions
        /// per health check cycle.
        /// </summary>
        private async Task ReconcilePullRequestMissionsAsync(CancellationToken token)
        {
            if (OnReconcilePullRequest == null) return;

            try
            {
                List<Mission> prMissions = await _Database.Missions.EnumerateByStatusAsync(
                    MissionStatusEnum.PullRequestOpen, token).ConfigureAwait(false);

                if (prMissions.Count == 0) return;

                // Rate-limit: check at most 10 per cycle to avoid hammering the git host API
                int checked_count = 0;
                foreach (Mission mission in prMissions)
                {
                    if (checked_count >= 10) break;

                    try
                    {
                        await OnReconcilePullRequest.Invoke(mission).ConfigureAwait(false);
                        checked_count++;
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "error reconciling PR for mission " + mission.Id + ": " + ex.Message);
                    }
                }

                if (checked_count > 0)
                    _Logging.Info(_Header + "reconciled " + checked_count + " PullRequestOpen mission(s)");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error in PR reconciliation: " + ex.Message);
            }
        }

        /// <summary>
        /// Detect and recover orphaned missions — missions stuck in InProgress, Assigned,
        /// or Testing whose captain has moved on to a different mission. This handles
        /// the edge case where a captain was reassigned before the health check could detect
        /// the old process exit. A Review mission is never a candidate: it waits for an operator
        /// decision with its captain released and its process cleared on purpose, so that shape
        /// is not an unreported agent exit.
        /// </summary>
        private async Task RecoverOrphanedMissionsAsync(CancellationToken token)
        {
            List<Mission> inProgressMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.InProgress, token).ConfigureAwait(false);
            List<Mission> assignedMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Assigned, token).ConfigureAwait(false);
            List<Mission> testingMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Testing, token).ConfigureAwait(false);
            List<Mission> activeMissions = inProgressMissions.Concat(assignedMissions).Concat(testingMissions).ToList();

            foreach (Mission mission in activeMissions)
            {
                if (String.IsNullOrEmpty(mission.CaptainId)) continue;

                Captain? captain = await _Database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false);
                if (captain == null) continue;

                // If the captain's current mission is different, this mission is orphaned
                if (captain.CurrentMissionId != mission.Id)
                {
                    bool assignmentLaunchStillPending =
                        mission.Status == MissionStatusEnum.Assigned &&
                        !mission.ProcessId.HasValue &&
                        !mission.StartedUtc.HasValue;

                    if (assignmentLaunchStillPending)
                    {
                        TimeSpan assignmentAge = DateTime.UtcNow - mission.LastUpdateUtc;
                        if (assignmentAge < _AssignedOrphanRecoveryGracePeriod)
                        {
                            _Logging.Debug(_Header + "skipping orphan recovery for freshly assigned mission " + mission.Id +
                                " (" + assignmentAge.TotalSeconds.ToString("F1") + "s since assignment)");
                            continue;
                        }
                    }

                    _Logging.Warn(_Header + "orphaned mission detected: " + mission.Id + " is " + mission.Status +
                        " but captain " + captain.Id + " is working on " + (captain.CurrentMissionId ?? "nothing") +
                        " — checking if work was completed");

                    // Only recover missions that actually launched an agent process.
                    // Assigned-but-never-started missions can be legitimately re-routed while still pending.
                    if (!mission.ProcessId.HasValue && !mission.StartedUtc.HasValue)
                    {
                        _Logging.Warn(_Header + "orphaned mission " + mission.Id +
                            " never started an agent process -- reverting to Pending instead of completing");
                        mission.Status = MissionStatusEnum.Pending;
                        mission.CaptainId = null;
                        mission.DockId = null;
                        mission.ProcessId = null;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        continue;
                    }

                    // Check if the mission's process exited (work may already be done)
                    bool processAlive = false;
                    if (mission.ProcessId.HasValue)
                    {
                        // The shared identity-checked lookup: a reused identifier does not keep the mission alive.
                        processAlive = ProcessSupervisor.IsTrackedProcessAlive(mission.ProcessId.Value);
                    }

                    if (!processAlive)
                    {
                        // Process exited — check if there are commits on the branch (work was done)
                        Dock? dock = null;
                        if (!String.IsNullOrEmpty(mission.DockId))
                        {
                            dock = await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                        }

                        // Complete the mission — the agent finished its work but the status was never updated
                        _Logging.Info(_Header + "completing orphaned mission " + mission.Id + " (agent process exited, captain moved on)");
                        await _Missions.HandleCompletionAsync(captain, mission.Id, token).ConfigureAwait(false);

                        await EmitEventAsync("mission.orphan_recovered",
                            "Orphaned mission recovered: " + mission.Title + " (captain " + captain.Id + " had moved on)",
                            entityType: "mission", entityId: mission.Id,
                            captainId: captain.Id, missionId: mission.Id,
                            vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task RecoverStageWatchdogMissionsAsync(CancellationToken token)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-_Settings.StageWatchdogTimeoutMinutes);
            List<Mission> assigned = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Assigned, token).ConfigureAwait(false);
            foreach (Mission mission in assigned)
            {
                if (mission.LastUpdateUtc > cutoff) continue;
                if (!String.IsNullOrWhiteSpace(mission.CaptainId) && mission.ProcessId.HasValue) continue;

                mission.Status = MissionStatusEnum.Failed;
                mission.FailureReason = "stage_watchdog_no_captain_heartbeat";
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                mission.CaptainId = null;
                mission.ProcessId = null;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                await _Database.Signals.CreateAsync(new Signal
                {
                    FromCaptainId = null,
                    ToCaptainId = null,
                    Type = SignalTypeEnum.Error,
                    Payload = "Mission " + mission.Id + " failed: stage_watchdog_no_captain_heartbeat",
                    CreatedUtc = DateTime.UtcNow
                }, token).ConfigureAwait(false);

                await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Failed,
                    "stage_watchdog_no_captain_heartbeat", _Logging, token).ConfigureAwait(false);
                await EmitEventAsync("mission.failed",
                    "Mission failed by stage watchdog: " + mission.Title,
                    entityType: "mission", entityId: mission.Id,
                    missionId: mission.Id, vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
                _Logging.Warn(_Header + "stage watchdog failed mission " + mission.Id + " after " + _Settings.StageWatchdogTimeoutMinutes + " minute(s) without captain/process heartbeat");

                // The watchdog fails the stage but leaves the voyage alive for autonomous recovery, so the
                // stages behind it would otherwise park forever and keep the voyage counting as an active lane.
                // Cancel them through the one dependent walker, exactly as every other voyage-preserving failure
                // path does.
                await CancelDirectDependentsAsync(mission, mission.FailureReason ?? "stage_watchdog_no_captain_heartbeat", token).ConfigureAwait(false);
            }
        }

        private static bool IsExpectedAssignmentWait(MissionAssignmentStateEnum assignmentState)
        {
            return assignmentState == MissionAssignmentStateEnum.WaitingForDependency
                || assignmentState == MissionAssignmentStateEnum.WaitingForVesselMutex
                || assignmentState == MissionAssignmentStateEnum.WaitingForIdleCaptain
                || assignmentState == MissionAssignmentStateEnum.WaitingForResourcePressure
                || assignmentState == MissionAssignmentStateEnum.WaitingForProviderUsage;
        }

        private async Task DispatchPendingMissionsAsync(CancellationToken token)
        {
            // Tier, preference rank and persona minimums live on records; read them fresh every pass, so a record
            // edit reaches dispatch, stage creation and previews within one pass even while nothing is pending.
            await TierRoutingRecords.RefreshAsync(_Settings.ModelTier, _Database, token).ConfigureAwait(false);

            List<Mission> pendingMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Pending, token).ConfigureAwait(false);
            if (pendingMissions.Count == 0)
            {
                _RetryDispatchNeeded = false;
                return;
            }

            if (_RetryDispatchNeeded)
            {
                _Logging.Info(_Header + "retrying dispatch for pending missions that previously could not be assigned");
            }

            // Check for any idle captains with available capacity
            bool hasCapacity = await HasAvailableCapacityAsync(token).ConfigureAwait(false);
            if (!hasCapacity) return;

            // Resolve vessels for each candidate mission up front so we can fan out the
            // assignment attempts. TryAssignAsync internally serializes on the captain
            // claim (atomic UPDATE ... WHERE state='Idle') and on its per-mission
            // _InFlightAssignments guard, so parallel calls are safe.
            //
            // This matters because TryAssignAsync's dock-provisioning step (git worktree
            // creation, etc.) is slow -- previously we could only assign 1-3 missions per
            // 30s heartbeat tick because each iteration awaited the prior one's provisioning.
            // With Task.WhenAll, dock provisioning runs concurrently across captains.
            List<(Mission mission, Vessel vessel)> assignable = new List<(Mission, Vessel)>();
            foreach (Mission mission in pendingMissions)
            {
                if (string.IsNullOrEmpty(mission.VesselId)) continue;

                Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
                if (vessel == null) continue;

                assignable.Add((mission, vessel));
            }

            if (assignable.Count == 0)
            {
                _RetryDispatchNeeded = false;
                return;
            }

            // Split candidates by tier requirement. Premium-floor stages dispatch first so they are not
            // outbid for scarce Premium captains by missions with a lower floor. Those missions then
            // dispatch subject to a high-tier capacity reservation so produced work
            // does not outrun review+landing capacity (the observed starvation symptom).
            List<(Mission mission, Vessel vessel)> premiumFloorMissions = new List<(Mission, Vessel)>();
            List<(Mission mission, Vessel vessel)> otherFloorMissions = new List<(Mission, Vessel)>();
            foreach ((Mission mission, Vessel vessel) pair in assignable)
            {
                if (_Settings.ModelTier.MinimumTierForPersona(pair.mission.Persona) == CaptainTierEnum.Premium)
                    premiumFloorMissions.Add(pair);
                else
                    otherFloorMissions.Add(pair);
            }

            List<(Mission mission, bool result)> dispatchResults = new List<(Mission, bool)>();

            // Phase 1: Premium-floor missions claim idle high-tier captains before Workers.
            if (premiumFloorMissions.Count > 0)
            {
                Task<bool>[] premiumFloorTasks = premiumFloorMissions
                    .Select(p => _Missions.TryAssignAsync(p.mission, p.vessel, token))
                    .ToArray();
                bool[] premiumFloorResults = await Task.WhenAll(premiumFloorTasks).ConfigureAwait(false);
                for (int i = 0; i < premiumFloorResults.Length; i++)
                    dispatchResults.Add((premiumFloorMissions[i].mission, premiumFloorResults[i]));
            }

            // Phase 2: Other-floor missions. When the only idle capacity left after Phase 1 is
            // Premium and it is at or below the configured reservation, defer these missions
            // for one cycle so the held-back captain stays free for the next incoming Premium-floor
            // stage. Lower minimum tiers can use Standard and Economy captains, so this gate only
            // engages when no lower-tier capacity remains -- non-Premium
            // capacity is never withheld.
            bool otherFloorDeferred = false;
            if (otherFloorMissions.Count > 0)
            {
                bool canDispatchOtherFloor = true;
                int reservedHighTierSlots = _Settings.ModelTier.ReservedHighTierSlots;
                if (reservedHighTierSlots > 0)
                {
                    // Re-query idle capacity AFTER Phase 1 so captains claimed by
                    // Premium-floor missions are excluded from the reservation accounting.
                    List<Captain> remainingIdle = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle, token).ConfigureAwait(false);
                    int idleHighTier = 0;
                    int idleNonHighTier = 0;
                    foreach (Captain c in remainingIdle)
                    {
                        if (CaptainTierSelector.EffectiveTier(c) == CaptainTierEnum.Premium)
                            idleHighTier++;
                        else
                            idleNonHighTier++;
                    }

                    // Engage the reservation only when (a) the remaining idle pool is
                    // Premium-only and within the reserve, AND (b) there is in-flight
                    // work that could soon produce a downstream Premium-floor stage. The
                    // second condition is the deadlock guard: with nothing in flight no
                    // Premium-floor mission is coming, so holding the last idle Premium captain
                    // would starve other work forever on a Premium-only fleet. Priming one lower-floor
                    // mission is safe -- once it is in flight the reserve re-engages.
                    if (idleHighTier <= reservedHighTierSlots
                        && idleNonHighTier == 0
                        && await HasInFlightPremiumDemandAsync(token).ConfigureAwait(false))
                    {
                        canDispatchOtherFloor = false;
                        otherFloorDeferred = true;
                        _Logging.Info(_Header + "deferring " + otherFloorMissions.Count +
                            " mission(s) to reserve Premium capacity: idle Premium=" + idleHighTier +
                            " reserved=" + reservedHighTierSlots + " idle other-tier=" + idleNonHighTier);
                    }
                }

                if (canDispatchOtherFloor)
                {
                    Task<bool>[] otherFloorTasks = otherFloorMissions
                        .Select(p => _Missions.TryAssignAsync(p.mission, p.vessel, token))
                        .ToArray();
                    bool[] otherFloorResults = await Task.WhenAll(otherFloorTasks).ConfigureAwait(false);
                    for (int i = 0; i < otherFloorResults.Length; i++)
                        dispatchResults.Add((otherFloorMissions[i].mission, otherFloorResults[i]));
                }
            }

            bool anyFailed = false;
            foreach ((Mission mission, bool result) entry in dispatchResults)
            {
                if (!entry.result)
                {
                    if (IsExpectedAssignmentWait(entry.mission.AssignmentState))
                    {
                        _Logging.Debug(_Header + "pending mission " + entry.mission.Id + " not assigned yet (AssignmentState=" + entry.mission.AssignmentState + ")");
                    }
                    else
                    {
                        _Logging.Warn(_Header + "could not assign pending mission " + entry.mission.Id + " (AssignmentState=" + entry.mission.AssignmentState + ") - will retry on next health check cycle");
                        anyFailed = true;
                    }
                }
            }

            _RetryDispatchNeeded = anyFailed || otherFloorDeferred;
        }

        /// <summary>
        /// Returns true when at least one mission is in a non-terminal active state
        /// (Assigned, InProgress, WorkProduced, Testing, Review, or PullRequestOpen) --
        /// i.e. work is running or awaiting a downstream review/landing stage that will
        /// soon need a Premium captain. Used to decide whether reserving Premium
        /// capacity is justified; with no such work in flight there is no imminent
        /// Premium-floor demand and the reservation is suppressed to avoid starving other work.
        /// </summary>
        private async Task<bool> HasInFlightPremiumDemandAsync(CancellationToken token)
        {
            Dictionary<MissionStatusEnum, int> counts = await _Database.Missions.CountByStatusAsync(token).ConfigureAwait(false);
            return CountForStatus(counts, MissionStatusEnum.Assigned)
                + CountForStatus(counts, MissionStatusEnum.InProgress)
                + CountForStatus(counts, MissionStatusEnum.WorkProduced)
                + CountForStatus(counts, MissionStatusEnum.Testing)
                + CountForStatus(counts, MissionStatusEnum.Review)
                + CountForStatus(counts, MissionStatusEnum.PullRequestOpen) > 0;
        }

        private static int CountForStatus(Dictionary<MissionStatusEnum, int> counts, MissionStatusEnum status)
        {
            return counts.TryGetValue(status, out int value) ? value : 0;
        }

        private async Task<bool> HasAvailableCapacityAsync(CancellationToken token)
        {
            // Only idle captains have capacity for new assignments
            List<Captain> idleCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle, token).ConfigureAwait(false);
            return idleCaptains.Count > 0;
        }

        /// <summary>
        /// Reclaim the dock associated with a captain and/or mission.
        /// Clears CurrentDockId on the captain and DockId on the mission after reclaim.
        /// Handles gracefully if the dock or worktree is already gone.
        /// </summary>
        private async Task ReclaimDockAsync(Captain captain, Mission? mission, CancellationToken token)
        {
            string? dockId = captain.CurrentDockId;
            if (String.IsNullOrEmpty(dockId) && mission != null)
            {
                dockId = mission.DockId;
            }

            if (String.IsNullOrEmpty(dockId)) return;

            try
            {
                await _Docks.ReclaimAsync(dockId, token: token).ConfigureAwait(false);
                _Logging.Info(_Header + "reclaimed dock " + dockId + " for captain " + captain.Id);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error reclaiming dock " + dockId + " for captain " + captain.Id + ": " + ex.Message);
            }

            // Clear dock references
            if (!String.IsNullOrEmpty(captain.CurrentDockId))
            {
                captain.CurrentDockId = null;
                await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
            }

            if (mission != null && !String.IsNullOrEmpty(mission.DockId))
            {
                mission.DockId = null;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Whether a mission-log line is a genuine failure signal rather than a line that merely
        /// CONTAINS the substring "error". An agent's own success report frequently includes text
        /// like "0 Errors" / "no errors" (often a markdown build-summary table row); scraping such a
        /// line as the exit failure reason produced false-Failed missions that cascade-cancelled good
        /// work. Strong, unambiguous markers are always accepted; a bare "error"/"errors"
        /// is rejected when the immediately preceding token indicates a zero/none count.
        /// </summary>
        internal static bool IsGenuineErrorSignal(string line)
        {
            if (String.IsNullOrEmpty(line)) return false;

            if (line.Contains("API Error:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("overloaded_error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[stderr]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("exception", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int idx = line.IndexOf("error", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            // Inspect the token immediately before "error"/"errors". "0 errors", "no errors",
            // "zero errors", "without errors" are success/informational -- not a failure.
            string before = line.Substring(0, idx).TrimEnd();
            int lastSpace = before.LastIndexOf(' ');
            string token = (lastSpace >= 0 ? before.Substring(lastSpace + 1) : before)
                .Trim('*', '`', '|', ':', ',', '-', '.', '(', ')', '_', ' ')
                .ToLowerInvariant();

            return token != "0" && token != "no" && token != "zero" && token != "without";
        }

        /// <summary>
        /// Whether a mission-log line is an <c>[ARMADA:ACTIVITY]</c> record that does NOT speak for the
        /// captain's own runtime. An activity record is runtime telemetry, not the captain's declared
        /// failure. A <c>tool</c> record reports a tool the captain invoked, and a record whose runtime
        /// label is not the captain's own runtime is a SUB-TOOL line (e.g. an <c>opencode</c> call made
        /// by a Cursor captain); neither is the mission's failure, so an error it carries -- a credential
        /// rejection, an HTTP status -- must never become the mission failure reason or bench the captain.
        /// A record whose label matches the captain's own runtime is kept: that is the captain's own
        /// provider telemetry.
        /// </summary>
        /// <param name="line">Trimmed mission-log line.</param>
        /// <param name="ownRuntimeLabel">The captain runtime's activity label, or null when unknown.</param>
        /// <returns>True when the line is a foreign-runtime or tool activity record.</returns>
        internal static bool IsForeignRuntimeActivityLine(string line, string? ownRuntimeLabel)
        {
            if (String.IsNullOrEmpty(line)) return false;
            const string prefix = "[ARMADA:ACTIVITY] ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;

            string rest = line.Substring(prefix.Length);
            int space = rest.IndexOf(' ');
            string label = space < 0 ? rest : rest.Substring(0, space);
            if (String.Equals(label, "tool", StringComparison.Ordinal)) return true;
            return String.IsNullOrEmpty(ownRuntimeLabel)
                || !String.Equals(label, ownRuntimeLabel, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The activity-record runtime label for a captain runtime. The labels are written by the runtime
        /// adapters in Armada.Runtimes (ClaudeCode "claude", Codex "codex", OpenCode "opencode",
        /// ApiEndpoint "api", Cursor "cursor", Gemini "gemini", Mux "mux"), so a sub-tool's activity line
        /// carries a different label and is told apart from the captain's own provider telemetry. A runtime
        /// with no fixed label returns null, which treats every activity record as foreign (conservative).
        /// </summary>
        /// <param name="runtime">Captain runtime.</param>
        /// <returns>The lower-case runtime label, or null when the runtime has none.</returns>
        private static string? ActivityRuntimeLabel(AgentRuntimeEnum runtime)
        {
            switch (runtime)
            {
                case AgentRuntimeEnum.ClaudeCode: return "claude";
                case AgentRuntimeEnum.Codex: return "codex";
                case AgentRuntimeEnum.Gemini: return "gemini";
                case AgentRuntimeEnum.Cursor: return "cursor";
                case AgentRuntimeEnum.OpenCode: return "opencode";
                case AgentRuntimeEnum.ApiEndpoint: return "api";
                case AgentRuntimeEnum.Mux: return "mux";
                default: return null;
            }
        }

        /// <summary>
        /// Whether the agent output declared an explicit terminal success verdict: a Worker's
        /// <c>[ARMADA:RESULT] COMPLETE</c> or a reviewer's <c>[ARMADA:VERDICT] PASS</c>. Scans the tail
        /// of the output (where the terminal marker appears). Pure/testable core of
        /// <see cref="AgentDeclaredExplicitSuccessAsync"/>. NEEDS_REVISION/FAILED are intentionally not
        /// success -- they are handled by the existing reviewer-chain / failure logic.
        /// </summary>
        internal static bool HasExplicitSuccessVerdict(string[] lines)
        {
            if (lines == null || lines.Length == 0) return false;
            for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 200; i--)
            {
                foreach (ProgressParser.ProgressSignal signal in ProgressParser.ParseAll(lines[i]))
                {
                    if (signal.Value == null) continue;
                    string value = signal.Value.Trim();
                    if (String.Equals(signal.Type, "result", StringComparison.Ordinal)
                        && String.Equals(value, "COMPLETE", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    if (String.Equals(signal.Type, "verdict", StringComparison.Ordinal)
                        && String.Equals(value, "PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Whether the mission log records an explicit <c>[ARMADA:RESULT] COMPLETE</c> verdict. Used to
        /// avoid mislabeling good work as Failed when the agent process exits non-zero/lost after
        /// declaring success.
        /// </summary>
        private async Task<bool> AgentDeclaredExplicitSuccessAsync(string missionId, CancellationToken token)
        {
            Mission? logMission = await _Database.Missions.ReadSummaryAsync(missionId, token).ConfigureAwait(false);
            if (logMission == null) return false;
            string logPath = MissionAttemptArtifacts.LogPath(_Settings.LogDirectory, logMission);
            if (!File.Exists(logPath)) return false;
            try
            {
                string[] lines = await File.ReadAllLinesAsync(logPath, token).ConfigureAwait(false);
                return HasExplicitSuccessVerdict(lines);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not inspect mission log for explicit success verdict on " + missionId + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Whether the captured process exit code is a probable resource kill. Exit 137
        /// means SIGKILL and commonly indicates OOM, but it can also be an explicit kill.
        /// A kill Armada requested is registered in <see cref="IntentionalProcessStops"/> and
        /// handled before this classification, so only an exit no Armada stop explains reaches it.
        /// A null code is intentionally not classified because it is still more ambiguous.
        /// </summary>
        /// <param name="exitCode">Process exit code.</param>
        /// <returns>True when the exit code indicates a probable OOM/resource kill.</returns>
        internal static bool IsExitCodeOom(int? exitCode)
        {
            return exitCode.HasValue && exitCode.Value == 137;
        }

        /// <summary>
        /// Build the distinct failure reason recorded for a probable OOM/resource kill.
        /// The leading <c>OOM:</c> marker is stable so telemetry/tests can detect it.
        /// </summary>
        /// <param name="exitCode">Process exit code.</param>
        /// <returns>OOM failure reason string.</returns>
        internal static string BuildOomFailureReason(int? exitCode)
        {
            return "OOM: captain process received SIGKILL (exit "
                + (exitCode?.ToString() ?? "unknown")
                + "), consistent with host OOM/resource pressure; mission requeued and retry deferred until resource capacity returns.";
        }

        /// <summary>
        /// Select the mission failure reason from the tail of a captain's own-runtime log, or null when the
        /// tail names no provider or genuine-error signal (the caller then records the generic exit reason).
        /// Pure, testable core of <see cref="BuildProcessExitFailureReasonAsync"/>.
        ///
        /// Provider signals are inspected in priority order BEFORE the generic genuine-error pass, because a
        /// provider failure line is frequently followed by a generic runtime result-error record; scanning the
        /// generic record first hides the provider signal, and the captain is then released to Idle and
        /// re-offered the mission it cannot run. A provider SAFEGUARD block is the most specific; a usage or
        /// quota limit next; a credit or credential rejection next. All three bench the captain and share that
        /// hazard, so each gets its own early pass. A foreign-runtime or tool activity record never speaks for
        /// the captain and is skipped in every pass.
        /// </summary>
        /// <param name="lines">Mission-log lines, oldest first.</param>
        /// <param name="ownRuntimeLabel">The captain runtime's activity label, or null when unknown.</param>
        /// <returns>The selected failure reason, or null when the tail names no signal.</returns>
        internal static string? SelectProcessExitFailureReason(string[] lines, string? ownRuntimeLabel)
        {
            if (lines == null)
            {
                return null;
            }

            string? safeguard = ScanTailForFailureSignal(lines, ownRuntimeLabel, ProviderQuotaLimitDetector.IsProviderSafeguardBlockSignal);
            if (safeguard != null)
            {
                return safeguard;
            }

            string? quota = ScanTailForFailureSignal(lines, ownRuntimeLabel, ProviderQuotaLimitDetector.IsQuotaLimitSignal);
            if (quota != null)
            {
                return quota;
            }

            // A credit or credential rejection (HTTP 402/401/403, invalid key, billing hold) benches the
            // captain exactly as a quota limit does, and a later generic result-error record hides it the same
            // way. Inspect it before the generic pass, or an authentication failure a generic record follows is
            // scraped as an ordinary failure, the captain is released, and the requeued mission is re-offered to
            // the same captain whose credential the provider rejects -- a retry loop on a dead captain.
            string? creditAuth = ScanTailForFailureSignal(lines, ownRuntimeLabel, ProviderQuotaLimitDetector.IsCreditAuthBenchSignal);
            if (creditAuth != null)
            {
                return creditAuth;
            }

            return ScanTailForFailureSignal(lines, ownRuntimeLabel, IsGenuineErrorSignal);
        }

        /// <summary>
        /// Scan the last forty log lines newest-first and return the first line the predicate accepts,
        /// normalized, skipping blank lines and foreign-runtime or tool activity records. Returns null when no
        /// line matches.
        /// </summary>
        /// <param name="lines">Mission-log lines, oldest first.</param>
        /// <param name="ownRuntimeLabel">The captain runtime's activity label, or null when unknown.</param>
        /// <param name="predicate">The signal test applied to each candidate line.</param>
        /// <returns>The first matching line, normalized, or null when none matches.</returns>
        private static string? ScanTailForFailureSignal(string[] lines, string? ownRuntimeLabel, Func<string, bool> predicate)
        {
            for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 40; i--)
            {
                string line = lines[i].Trim();
                if (String.IsNullOrEmpty(line)) continue;
                if (IsForeignRuntimeActivityLine(line, ownRuntimeLabel)) continue;
                if (predicate(line))
                {
                    return NormalizeProcessExitFailureReason(line);
                }
            }

            return null;
        }

        private async Task<string> BuildProcessExitFailureReasonAsync(string missionId, int? exitCode, AgentRuntimeEnum captainRuntime, CancellationToken token)
        {
            // Telemetry a SUB-TOOL wrote (a tool the captain invoked, e.g. an opencode call made by a
            // Cursor captain) is never the mission's failure: its errors, including a credential or HTTP
            // status, are the sub-tool's. Only the captain's own-runtime provider-failure telemetry may
            // stand in for the mission's failure reason, so a foreign-runtime activity record is skipped
            // in every pass below.
            string? ownRuntimeLabel = ActivityRuntimeLabel(captainRuntime);

            Mission? logMission = await _Database.Missions.ReadSummaryAsync(missionId, token).ConfigureAwait(false);
            if (logMission == null) return null;
            string logPath = MissionAttemptArtifacts.LogPath(_Settings.LogDirectory, logMission);
            if (File.Exists(logPath))
            {
                try
                {
                    string[] lines = await File.ReadAllLinesAsync(logPath, token).ConfigureAwait(false);
                    string? selected = SelectProcessExitFailureReason(lines, ownRuntimeLabel);
                    if (selected != null)
                    {
                        return selected;
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not inspect mission log for failure reason on " + missionId + ": " + ex.Message);
                }
            }

            return "Agent process exited with code " + (exitCode?.ToString() ?? "unknown");
        }

        /// <summary>
        /// Refine a deterministic runtime-failure classification with the D3 <c>runtime_failure</c>
        /// adapter, when wired. Only a bare Crash is offered a change; every recognised signature is
        /// authoritative inside the adapter's Combine. Never throws into the caller.
        /// </summary>
        private async Task<RuntimeFailureKindEnum> RefineRuntimeFailureAsync(
            Mission? mission,
            Captain captain,
            int? exitCode,
            string failureReason,
            RuntimeFailureKindEnum ruleVerdict,
            CancellationToken token)
        {
            if (RuntimeFailureAdapter == null) return ruleVerdict;

            try
            {
                RuntimeFailureDecisionInput input = new RuntimeFailureDecisionInput
                {
                    Mission = mission,
                    ExitCode = exitCode,
                    Tail = LastOutputLines(failureReason, 40),
                    Runtime = captain.Runtime.ToString(),
                    ModelId = captain.Model ?? String.Empty,
                    KeyFamily = TypedRuntimeFailureAdapter.KeyFamilyOf(captain)
                };
                return await RuntimeFailureAdapter.DecideAsync(input, ruleVerdict, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "runtime_failure refinement failed for mission " + (mission?.Id ?? "<none>") + ", rule stands: " + ex.Message);
                return ruleVerdict;
            }
        }

        /// <summary>
        /// Refine a deterministic refusal classification with the D2 <c>refusal</c> adapter, when
        /// wired. A provider safeguard block and the structured marker stay authoritative inside the
        /// adapter's Combine. Never throws into the caller.
        /// </summary>
        private async Task<CaptainRefusal> RefineRefusalAsync(Mission mission, string failureReason, CaptainRefusal ruleVerdict, CancellationToken token)
        {
            if (RefusalAdapter == null) return ruleVerdict;

            try
            {
                RefusalDecisionInput input = new RefusalDecisionInput
                {
                    Mission = mission,
                    AgentOutputTail = LastOutputLines(failureReason, 40),
                    MissionTitle = mission.Title ?? String.Empty,
                    MarkerPresent = CaptainRefusalClassifier.HasRefusalMarker(failureReason)
                };
                return await RefusalAdapter.DecideAsync(input, ruleVerdict, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "refusal refinement failed for mission " + mission.Id + ", rule stands: " + ex.Message);
                return ruleVerdict;
            }
        }

        private static string LastOutputLines(string? text, int count)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int first = Math.Max(0, lines.Length - count);
            return String.Join("\n", lines[first..]);
        }

        private async Task HandleTerminalProcessExitFailureAsync(
            Captain captain,
            Mission? mission,
            string missionId,
            int? exitCode,
            string failureReason,
            RuntimeFailureKindEnum failureKind,
            CancellationToken token,
            RuntimeProviderError? providerError = null)
        {
            // A PROVIDER SAFEGUARD BLOCK (the provider's content/cyber safety gate refused the request) follows the
            // one refusal rule: it is recorded, and the mission gets at most one continuation on an approved captain
            // of a different runtime. It is never retried on the runtime that blocked. When the continuation is
            // spent or no alternate exists, the mission fails through the terminal path below with the reason.
            if (mission != null && ProviderQuotaLimitDetector.IsProviderSafeguardBlockSignal(failureReason))
            {
                CaptainRefusal refusal = CaptainRefusalClassifier.Classify(failureReason);
                refusal = await RefineRefusalAsync(mission, failureReason, refusal, token).ConfigureAwait(false);
                if (refusal.Kind != CaptainRefusalKindEnum.ProviderSafeguardBlock)
                {
                    refusal = new CaptainRefusal
                    {
                        Kind = CaptainRefusalKindEnum.ProviderSafeguardBlock,
                        Reason = failureReason.Length <= CaptainRefusalClassifier.MaxReasonChars ? failureReason : failureReason.Substring(0, CaptainRefusalClassifier.MaxReasonChars),
                        Evidence = failureReason.Length <= CaptainRefusalClassifier.MaxReasonChars ? failureReason : failureReason.Substring(0, CaptainRefusalClassifier.MaxReasonChars)
                    };
                }

                PolicyRefusalContinuationDecision decision = await new PolicyRefusalContinuationService(_Database, _Settings, _Logging)
                    .HandleAsync(mission, captain, refusal, policyPresent: false, token).ConfigureAwait(false);
                if (decision.Outcome == PolicyRefusalContinuationOutcomeEnum.Continue)
                {
                    // The continuation resumes the saved branch, so the dock that authorizes it stays active.
                    await RetainRetryDockAsync(captain, mission, token).ConfigureAwait(false);
                    await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                    if (!String.IsNullOrEmpty(mission.VesselId))
                    {
                        QueueVoyageAssignments(mission.VoyageId, mission.VesselId!, new List<string> { mission.Id });
                    }
                    else
                    {
                        _Logging.Warn(_Header + "mission " + missionId + " continued after a provider safeguard block but has no vessel id; relying on health-check retry sweep");
                        _RetryDispatchNeeded = true;
                    }
                    return;
                }

                failureReason = mission.FailureReason ?? failureReason;
            }

            // A provider QUOTA / CREDIT / BALANCE limit (usage cap, insufficient balance, billing/auth) is
            // recoverable the same way: bench the affected captain and re-route the mission to a compatible peer
            // that still has quota, instead of cascade-cancelling the voyage. Provider-neutral by construction.
            if (mission != null)
            {
                TimeSpan quotaRuntime = mission.StartedUtc.HasValue ? DateTime.UtcNow - mission.StartedUtc.Value : TimeSpan.Zero;
                bool isCreditAuth = ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(failureReason)
                    || ProviderQuotaLimitDetector.IsProviderCreditError(providerError)
                    || ProviderQuotaLimitDetector.IsProviderAuthError(providerError);
                bool isAccountSpendLimit = ProviderQuotaLimitDetector.IsProviderAccountSpendLimitSignal(failureReason);
                bool isCodexUsageCrash = ProviderQuotaLimitDetector.IsCodexUsageLimitCrash(exitCode, quotaRuntime, failureReason);

                // An authentication failure that names no quota, credit or spend limit is a rejected login, not a spent
                // allowance: waiting does not restore it, so a captain on a usage account refuses the whole account
                // until a usage read succeeds or the account signs in again, instead of a timed quota hold that expires
                // into the same failure. A captain on no account keeps the captain bench below.
                UsageAccountSettings? loginAccount = !isCodexUsageCrash
                    && RuntimeFailureClassifier.Classify(exitCode, failureReason, providerError) == RuntimeFailureKindEnum.AuthFailure
                    ? CaptainAccountLaunch.FindAccount(_Settings.ModelTier.UsageRouting, captain.Id)
                    : null;
                if (loginAccount != null)
                {
                    await RejectUsageAccountLoginAsync(captain, loginAccount, mission, token).ConfigureAwait(false);
                    await RerouteRecoverableFailureAsync(captain, mission, missionId, failureReason,
                        label: "provider authentication failure",
                        benchReason: "Usage account " + loginAccount.Id + " login was rejected by the provider; sign the account in again.",
                        benchUntilUtc: null,
                        rerouteEventKind: "mission.auth_rerouted",
                        exhaustedReason: "Every compatible captain failed provider authentication after re-routes; " +
                            "sign the affected accounts in again, then re-dispatch.",
                        token).ConfigureAwait(false);
                    return;
                }

                bool isQuota = ProviderQuotaLimitDetector.IsQuotaLimitSignal(failureReason) ||
                    ProviderQuotaLimitDetector.IsProviderQuotaError(providerError) ||
                    isCreditAuth ||
                    isAccountSpendLimit ||
                    isCodexUsageCrash;
                if (isQuota)
                {
                    DateTime? retryAfterUtc = ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(failureReason, captain.Runtime, DateTime.UtcNow, providerError);

                    // A provider spend cap kills every captain on the CAPPED MODEL, not the whole provider
                    // account: cheaper models on the same provider keep answering. Benching them one-per-re-route
                    // would exhaust the re-route budget before reaching a peer that still works, so bench the
                    // model group at once, until well past the daily reset (25h). Everything else stays eligible
                    // and benches only if it returns the cap itself.
                    if (isAccountSpendLimit)
                    {
                        DateTime benchUntil = DateTime.UtcNow.AddHours(25);
                        retryAfterUtc = benchUntil;
                        await BenchProviderGroupAsync(captain,
                            "Provider daily spend cap reached; benched until the daily reset (~25h).",
                            benchUntil, token).ConfigureAwait(false);
                    }

                    // A credit or billing limit that names no usage, rate or spend limit is a credit hold; the rest are quota holds.
                    string holdKind = (ProviderQuotaLimitDetector.IsCreditSignal(failureReason)
                        || ProviderQuotaLimitDetector.IsProviderCreditError(providerError))
                        && !ProviderQuotaLimitDetector.IsQuotaLimitSignal(failureReason)
                        && !ProviderQuotaLimitDetector.IsProviderQuotaError(providerError)
                        && !isAccountSpendLimit && !isCodexUsageCrash
                        ? UsageRoutingService.HoldKindCredit
                        : UsageRoutingService.HoldKindQuota;
                    await HoldUsageAccountAsync(captain, retryAfterUtc, holdKind, token).ConfigureAwait(false);
                    await HandleQuotaFailureRerouteAsync(captain, mission, missionId, failureReason, isCreditAuth, retryAfterUtc, token).ConfigureAwait(false);
                    return;
                }
            }

            // A self-hosted model behind a gateway can need longer to load cold than the gateway waits,
            // and the gateway then answers 502/504 before the captain did any work. The load goes on
            // after the gateway gives up, so one re-run usually finds the model warm. One per mission:
            // a gateway that keeps failing is a real outage and fails the mission as before.
            bool modelGatewayTimeout = IsModelGatewayTimeoutFailureReason(failureReason);
            if (modelGatewayTimeout)
            {
                failureReason = "[provider_timeout; endpoint=" + GetModelGatewayEndpointLabel(captain) + "] " + failureReason;
            }
            if (mission != null
                && modelGatewayTimeout
                && await CountModelGatewayRetriesAsync(mission.Id, token).ConfigureAwait(false) < 1)
            {
                await EmitEventAsync(ModelGatewayRetryEvent, "Mission " + mission.Id + " re-run once after its model gateway timed out before the model answered: " + failureReason,
                    entityType: "mission", entityId: mission.Id,
                    captainId: captain.Id, missionId: mission.Id,
                    vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
                await RequeueTransientMissionFailureAsync(captain, mission, missionId, failureReason, token, "provider_timeout").ConfigureAwait(false);
                await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
                return;
            }

            // Transient captain-unavailable / completion-verification failures are recoverable:
            // requeue the mission for reassignment instead of marking it Failed and halting the
            // voyage. Only genuine unrecoverable failures fall through to HaltVoyageAsync below.
            if (mission != null && !modelGatewayTimeout && IsCaptainUnavailableFailureReason(failureReason))
            {
                await RequeueTransientMissionFailureAsync(captain, mission, missionId, failureReason, token).ConfigureAwait(false);
                return;
            }

            if (mission != null)
            {
                // FD#3 (recoverable-work guard): when the failed mission produced committed work on its
                // branch, the branch is recoverable evidence. The branch must not be reaped and the voyage
                // must not be cascade-cancelled; only the missions that directly depend on the failed
                // mission are cancelled, so independent and parallel work keeps running and the operator
                // or autonomous recovery can rescue the committed work instead of losing it to the halt.
                bool hasRecoverableWork = await MissionHasRecoverableWorkAsync(mission, token).ConfigureAwait(false);

                // Preserve the partial stdout/stderr the stage produced before it failed. The successful
                // completion path drains this capture onto the mission; a terminal process-exit failure is
                // the only other terminal writer, so without this the failed stage finalizes with no output
                // and a downstream stage or rescue falls back to the weaker stored description. Drain the
                // capture once, through the same hook the completion path uses, and keep it only when the
                // mission has no output yet and the capture is non-empty, so a stage that truly produced
                // nothing stays distinguishable from one whose output was lost.
                if (String.IsNullOrEmpty(mission.AgentOutput) && _Missions.OnGetMissionOutput != null)
                {
                    string? capturedOutput = _Missions.OnGetMissionOutput(mission.Id);
                    if (!String.IsNullOrEmpty(capturedOutput))
                    {
                        mission.AgentOutput = RuntimeLogFormatter.RedactSecrets(capturedOutput);
                    }
                }

                mission.Status = MissionStatusEnum.Failed;
                mission.FailureReason = failureReason;
                if (hasRecoverableWork)
                {
                    mission.FailureReason = failureReason
                        + " [recoverable work preserved on mission branch " + mission.BranchName + "]";
                }
                mission.ProcessId = null;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                string? failureCause = modelGatewayTimeout ? "provider_timeout"
                    : failureKind == RuntimeFailureKindEnum.Crash && !exitCode.HasValue ? null
                    : MissionAttemptFactReasonCode.FromRuntimeFailure(failureKind);
                await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Failed, failureCause, _Logging, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "mission " + missionId + " marked failed after process exit"
                    + (hasRecoverableWork ? " (recoverable work preserved)" : ""));

                if (hasRecoverableWork)
                {
                    // Preserve the branch (no reap) and keep the voyage alive. Direct dependents
                    // cannot run without the failed upstream, so they are cancelled explicitly.
                    await CancelDirectDependentsAsync(mission, failureReason, token).ConfigureAwait(false);

                    await EmitEventAsync("mission.failed_recoverable_work",
                        "Mission failed but its committed work was preserved on branch " + mission.BranchName
                        + ": " + mission.Title + " (" + failureReason + ")",
                        entityType: "mission", entityId: mission.Id,
                        captainId: captain.Id, missionId: mission.Id,
                        vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
                }
                else
                {
                    // A non-zero process exit is a terminal transition that never flows through
                    // MissionService.HandleCompletionCoreAsync, so route through the single reap
                    // implementation to clean up the captain branch (honoring policy + rescue guard).
                    await _Missions.ReapTerminalMissionBranchAsync(mission, token).ConfigureAwait(false);

                    await EmitEventAsync("mission.failed", "Mission failed: " + mission.Title + " (" + failureReason + ")",
                        entityType: "mission", entityId: mission.Id,
                        captainId: captain.Id, missionId: mission.Id,
                        vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);

                    if (!String.IsNullOrEmpty(mission.VoyageId))
                    {
                        await HaltVoyageAsync(mission.VoyageId, mission.Id, failureReason, token).ConfigureAwait(false);

                        // Autonomous recovery never selects a mission whose voyage is Cancelled, so after the
                        // halt nothing else will ever open an incident for this failure.
                        await OpenHaltedVoyageIncidentAsync(mission, failureReason, token).ConfigureAwait(false);
                    }
                }
            }

            await ReclaimDockAsync(captain, mission, token).ConfigureAwait(false);

            // Quota/credit/balance and provider-safeguard failures were re-routed above (early return), and a
            // transient captain-unavailable failure was requeued above. Anything reaching here is a genuine work
            // failure -- release the captain (Stalled if it reported unavailable, otherwise Idle).
            if (IsCaptainUnavailableFailureReason(failureReason))
            {
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                await _Database.Captains.UpdateStateAsync(captain.Id, CaptainStateEnum.Stalled, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "captain " + captain.Id + " marked Stalled after non-retryable runtime failure on mission " + missionId);
            }
            else
            {
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                _Logging.Info(_Header + "captain " + captain.Id + " released to Idle after mission failure " + missionId);
            }

            if (_Escalation != null)
            {
                await _Escalation.FireAsync(EscalationTriggerEnum.RecoveryExhausted, captain.Id,
                    "Captain " + captain.Id + " failed mission " + missionId + " (" + failureReason + ")", token).ConfigureAwait(false);
            }

            string signalMessage = "Mission " + missionId + " failed: " + failureReason;
            if (IsCaptainUnavailableFailureReason(failureReason))
            {
                signalMessage += " (captain stalled)";
            }

            Signal signal = new Signal(SignalTypeEnum.Error, signalMessage);
            signal.FromCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Open one High incident for a mission whose terminal failure halted its voyage. Recovery skips
        /// missions of cancelled voyages, so this is the only place such a failure reaches incident triage.
        /// An active incident already linked to the mission is kept instead of duplicated.
        /// </summary>
        private async Task OpenHaltedVoyageIncidentAsync(Mission mission, string failureReason, CancellationToken token)
        {
            try
            {
                IncidentService incidents = new IncidentService(_Database);
                AuthContext auth = AuthContext.Authenticated(
                    mission.TenantId ?? Constants.DefaultTenantId,
                    mission.UserId ?? Constants.DefaultUserId,
                    false,
                    true,
                    "Admiral",
                    principalDisplay: "Armada Admiral");

                List<Incident> existing = await incidents.EnumerateActiveAsync(auth, new IncidentQuery
                {
                    MissionId = mission.Id
                }, token).ConfigureAwait(false);
                if (existing.Count > 0)
                    return;

                string title = mission.Title ?? mission.Id;
                if (title.Length > 96) title = title.Substring(0, 96);
                Incident created = await incidents.CreateAutomaticallyAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Mission failed: " + title,
                    Summary = "Mission " + mission.Id + " failed on process exit and halted voyage " + mission.VoyageId + ". Reason: " + failureReason,
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VesselId = mission.VesselId,
                    MissionId = mission.Id,
                    VoyageId = mission.VoyageId,
                    Impact = "The voyage was cancelled; autonomous recovery does not act on missions of a cancelled voyage.",
                    RootCause = failureReason,
                    RecoveryNotes = "Opened by the terminal process-exit path. No autonomous rescue will run for this failure: read the mission log, then re-dispatch the work or close this incident with the reason.",
                    DetectedUtc = mission.CompletedUtc ?? DateTime.UtcNow
                }, token).ConfigureAwait(false);

                await EmitEventAsync("mission.failed_incident_opened",
                    "Incident " + created.Id + " opened for mission " + mission.Id + " whose failure halted voyage " + mission.VoyageId,
                    entityType: "incident", entityId: created.Id,
                    missionId: mission.Id, vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not open an incident for failed mission " + mission.Id + " of halted voyage " + mission.VoyageId + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Whether a failed mission left committed work behind on its captain branch. A branch that
        /// still exists in the vessel's bare repository is recoverable evidence and must be preserved
        /// rather than reaped, and the voyage must not be cascade-cancelled around it.
        /// </summary>
        private async Task<bool> MissionHasRecoverableWorkAsync(Mission mission, CancellationToken token)
        {
            if (mission == null || String.IsNullOrWhiteSpace(mission.BranchName))
            {
                return false;
            }

            try
            {
                Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
                if (vessel == null || String.IsNullOrWhiteSpace(vessel.LocalPath))
                {
                    return false;
                }
                return await _Git.BranchExistsAsync(vessel.LocalPath, mission.BranchName, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not determine whether mission " + mission.Id + " has recoverable work: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Cancel the missions that directly depend on a failed mission, without cancelling the rest
        /// of the voyage. Used when the failed mission's work is recoverable and the voyage must stay
        /// alive; the dependent stages cannot run without the failed upstream, so they are cancelled
        /// explicitly instead of waiting forever.
        /// </summary>
        internal async Task CancelDirectDependentsAsync(Mission failedMission, string failureReason, CancellationToken token)
        {
            if (failedMission == null || String.IsNullOrEmpty(failedMission.VoyageId))
            {
                return;
            }

            // One fault costs the chain behind it, not the voyage's liveness: the shared walker cancels every
            // stage stranded behind the failed mission, and each one is logged and reported here.
            List<Mission> cancelled = await MissionDependentCancellation.CancelBlockedAsync(
                _Database,
                failedMission,
                MissionDependentCancellation.BlockedByFailedDependencyPrefix + " " + failedMission.Id + ": " + failureReason,
                token).ConfigureAwait(false);
            foreach (Mission dependent in cancelled)
            {
                _Logging.Info(_Header + "cancelled dependent mission " + dependent.Id
                    + " because upstream mission " + failedMission.Id + " failed with recoverable work");

                await EmitEventAsync("mission.cancelled_dependency",
                    "Mission cancelled: blocked by failed dependency " + failedMission.Id,
                    entityType: "mission", entityId: dependent.Id,
                    missionId: dependent.Id, voyageId: dependent.VoyageId, token: token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// True when a runtime-reported exit code means the run was interrupted rather than failed. A runtime
        /// reports a cancelled run as exactly -1. Every other code fails, including other negative values: a
        /// native crash status reads as a large negative number on Windows and on Harbor runners.
        /// </summary>
        /// <param name="exitCode">Exit code reported by the runtime, or null when unknown.</param>
        /// <returns>True only for exit code -1.</returns>
        internal static bool IsInterruptedExitCode(int? exitCode)
        {
            return exitCode.HasValue && exitCode.Value == _InterruptedExitCode;
        }

        /// <summary>
        /// Re-dispatch a mission whose run was interrupted, when its re-dispatch budget allows. The budget is
        /// counted from the mission's <c>mission.interrupted_redispatched</c> events, so it survives an Admiral
        /// restart and is separate from the autonomous-rescue budget. The captain is released to Idle because an
        /// interruption is not a captain fault. Returns false, changing nothing, when the mission was cancelled or
        /// the budget is spent; the caller then fails the mission through the normal terminal path.
        /// </summary>
        /// <param name="captain">Captain whose process exited.</param>
        /// <param name="mission">Mission the process was running.</param>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="exitCode">Interrupted exit code reported by the runtime.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the mission was re-dispatched.</returns>
        private async Task<bool> TryRedispatchInterruptedMissionAsync(
            Captain captain,
            Mission mission,
            string missionId,
            int exitCode,
            CancellationToken token)
        {
            if (mission.Status == MissionStatusEnum.Cancelled) return false;

            int budget = _Settings.MaxInterruptedExitRedispatchAttempts;
            List<ArmadaEvent> history = await _Database.Events.EnumerateByMissionAsync(mission.Id, _InterruptedRedispatchHistoryLimit, token).ConfigureAwait(false);
            int used = history.Count(item => String.Equals(item.EventType, _InterruptedRedispatchEventType, StringComparison.Ordinal));
            if (used >= budget)
            {
                _Logging.Warn(_Header + "mission " + missionId + " was interrupted (exit code " + exitCode + ") after " + used
                    + " interrupted re-dispatches; the budget of " + budget + " is spent, so the exit fails the mission");
                return false;
            }

            int attempt = used + 1;
            string reason = "Agent process was interrupted (exit code " + exitCode + "); mission re-dispatched, interrupted attempt "
                + attempt + " of " + budget + ".";

            if (!String.IsNullOrWhiteSpace(mission.HarborJobId))
            {
                if (!await BeginInterruptedHarborCompletionAsync(captain, mission, reason, token).ConfigureAwait(false))
                    throw new InvalidOperationException("The confirmed Harbor interruption could not complete its durable re-dispatch intent.");
                _Logging.Warn(_Header + "mission " + missionId + " " + reason);
                return true;
            }

            mission.Status = MissionStatusEnum.Pending;
            mission.AssignmentState = MissionAssignmentStateEnum.Pending;
            mission.FailureReason = reason;
            mission.CaptainId = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + missionId + " " + reason);
            await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Retried, "interrupted_process_exit", _Logging, token).ConfigureAwait(false);

            await EmitEventAsync(_InterruptedRedispatchEventType, reason + " " + mission.Title,
                entityType: "mission", entityId: mission.Id,
                captainId: captain.Id, missionId: mission.Id,
                vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);

            await ReclaimDockAsync(captain, mission, token).ConfigureAwait(false);
            await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(mission.VesselId))
            {
                QueueVoyageAssignments(mission.VoyageId, mission.VesselId!, new List<string> { mission.Id });
            }
            else
            {
                _Logging.Warn(_Header + "mission " + missionId + " re-dispatched after an interruption but has no vessel id; relying on health-check retry sweep");
                _RetryDispatchNeeded = true;
            }

            return true;
        }

        /// <summary>
        /// Requeue a mission whose captain hit a transient (captain-unavailable or completion-verification)
        /// failure. The mission is reset to Pending and re-dispatched; the voyage is left running and sibling
        /// missions are untouched. The failing captain is reclaimed and marked Stalled so the immediate retry
        /// does not select the same broken captain.
        /// </summary>
        private async Task RequeueTransientMissionFailureAsync(
            Captain captain,
            Mission mission,
            string missionId,
            string failureReason,
            CancellationToken token,
            string reasonCode = "transient_captain_failure")
        {
            if (!String.IsNullOrWhiteSpace(mission.HarborJobId))
            {
                if (!await BeginTransientHarborCompletionAsync(captain, mission, failureReason, reasonCode, token).ConfigureAwait(false))
                    throw new InvalidOperationException("The confirmed Harbor failure could not complete its durable requeue intent.");
                _Logging.Warn(_Header + "mission " + missionId + " requeued after transient captain failure: " + failureReason);
                return;
            }

            string? vesselId = mission.VesselId;
            string? voyageId = mission.VoyageId;

            // Reset the mission so it can be re-dispatched. Keep the failure reason recorded for
            // operator visibility, but clear the captain/process binding and run timestamps.
            mission.Status = MissionStatusEnum.Pending;
            mission.AssignmentState = MissionAssignmentStateEnum.Pending;
            mission.FailureReason = failureReason;
            mission.CaptainId = null;
            mission.ProcessId = null;
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + missionId + " requeued after transient captain failure: " + failureReason);
            await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Retried, reasonCode, _Logging, token).ConfigureAwait(false);

            // Non-terminal event so the retry is visible without emitting mission.failed.
            await EmitEventAsync("mission.retry_requeued", "Mission requeued after transient captain failure: " + mission.Title + " (" + failureReason + ")",
                entityType: "mission", entityId: mission.Id,
                captainId: captain.Id, missionId: mission.Id,
                vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);

            await RetainRetryDockAsync(captain, mission, token).ConfigureAwait(false);
            bool isCreditAuthFailure = ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(failureReason);
            bool shouldQuarantine = ProviderQuotaLimitDetector.IsQuotaLimitSignal(failureReason) || isCreditAuthFailure;
            if (shouldQuarantine)
            {
                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(failureReason, captain.Runtime, DateTime.UtcNow);
                string quarantineReason = isCreditAuthFailure ? _CreditAuthQuarantineReason : failureReason;
                await _CaptainQuarantine.QuarantineAsync(captain, quarantineReason, retryAfterUtc, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "captain " + captain.Id + " quarantined after quota failure on requeued mission " + missionId);
            }
            else
            {
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                await _Database.Captains.UpdateStateAsync(captain.Id, CaptainStateEnum.Stalled, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "captain " + captain.Id + " marked Stalled after transient failure on requeued mission " + missionId);
            }

            Signal signal = new Signal(SignalTypeEnum.Progress,
                "Mission " + missionId + " requeued after transient captain failure: " + failureReason +
                (shouldQuarantine ? " (captain quarantined)" : " (captain stalled)"));
            signal.FromCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            // Queue reassignment using the existing helper rather than a new scheduler.
            if (!String.IsNullOrEmpty(vesselId))
            {
                QueueVoyageAssignments(voyageId, vesselId!, new List<string> { mission.Id });
            }
            else
            {
                _Logging.Warn(_Header + "mission " + missionId + " requeued but has no vessel id; relying on health-check retry sweep");
                _RetryDispatchNeeded = true;
            }
        }

        // A pending retry still owns its continuation. Release the failed captain without
        // destroying the active dock that authorizes the saved branch on reassignment.
        private async Task RetainRetryDockAsync(Captain captain, Mission mission, CancellationToken token)
        {
            string? retainedDockId = mission.DockId ?? captain.CurrentDockId;
            if (!String.IsNullOrEmpty(retainedDockId))
            {
                Dock? retainedDock = await _Database.Docks.ReadAsync(retainedDockId, token).ConfigureAwait(false);
                if (retainedDock != null && retainedDock.Active
                    && retainedDock.VesselId == mission.VesselId
                    && OwnershipPolicy.SameTenant(retainedDock.TenantId, mission.TenantId)
                    && OwnershipPolicy.UserOfRecord(retainedDock.UserId) == OwnershipPolicy.UserOfRecord(mission.UserId)
                    && retainedDock.BranchName == mission.BranchName
                    && retainedDock.CaptainId == captain.Id)
                {
                    retainedDock.CaptainId = null;
                    retainedDock.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Docks.UpdateAsync(retainedDock, token).ConfigureAwait(false);
                    mission.DockId = retainedDock.Id;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                }
                else if (mission.DockId != null)
                {
                    mission.DockId = null;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                }
            }
        }

        // A provider QUOTA / CREDIT / BALANCE limit (usage cap, insufficient balance, billing/auth): bench the
        // out-of-quota captain until its retry window and re-route to a compatible peer that still has quota. Thin
        // wrapper over the shared recoverable-failure re-route.
        /// <summary>
        /// Bench every captain running the SAME MODEL as the failing captain. Used for a provider spend cap: the
        /// cap is scoped to the model (or its price tier), NOT to the whole provider account -- a cap reached on
        /// an expensive frontier model leaves the cheap models on the same provider answering normally. Benching
        /// the whole provider prefix therefore disabled captains that still worked. Benching the model group at
        /// once still avoids walking (and exhausting) the per-re-route budget one identical sibling at a time.
        /// Model-neutral: the model comes from the failing captain, never a hard-coded name. Every other model --
        /// same provider or native runtime -- is left untouched, and benches only when it returns the cap itself.
        /// <paramref name="untilUtc"/> is when the benched captains auto-clear (past the provider's daily reset).
        /// </summary>
        private async Task BenchProviderGroupAsync(Captain failingCaptain, string reason, DateTime untilUtc, CancellationToken token)
        {
            string? model = failingCaptain.Model;
            if (String.IsNullOrEmpty(model)) return;

            // Only the failing captain's process has exited. A sibling may still be running a mission on the capped
            // model, so siblings go through the conditional hold: an idle sibling is held now, and a busy sibling keeps
            // its mission, dock and process and is held when its own run returns the cap.
            AuthContext siblingScope = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Internal");
            List<Captain> all = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            int benched = 0;
            int skippedBusy = 0;
            foreach (Captain c in all)
            {
                if (String.IsNullOrEmpty(c.Model)) continue;
                if (!String.Equals(c.Model, model, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (String.Equals(c.Id, failingCaptain.Id, StringComparison.Ordinal))
                    {
                        await _CaptainQuarantine.QuarantineAsync(c, reason, untilUtc, token).ConfigureAwait(false);
                        benched++;
                        continue;
                    }

                    CaptainQuarantineResult result = await _CaptainQuarantine.QuarantineCaptainAsync(siblingScope, c.Id, reason, untilUtc, token).ConfigureAwait(false);
                    if (result.Outcome == CaptainQuarantineOutcomeEnum.Quarantined) benched++;
                    else if (result.Outcome == CaptainQuarantineOutcomeEnum.Busy) skippedBusy++;
                    else _Logging.Warn(_Header + "provider-group bench: captain " + c.Id + " not benched (" + result.Outcome + "): " + result.Message);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "provider-group bench: could not bench captain " + c.Id + ": " + ex.Message);
                }
            }
            _Logging.Warn(_Header + "provider spend cap: benched " + benched + " captain(s) on model '" +
                model + "' until " + untilUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture) +
                "; " + skippedBusy + " busy sibling(s) keep running and bench when their own run returns the cap" +
                "; other models on this provider stay available until they hit the cap themselves");
        }

        /// <summary>
        /// Refuse the failing captain's whole usage account because the provider rejected its login. Every captain on
        /// the account shares the login, so routing and launch refuse each of them until a usage read succeeds or the
        /// account signs in again. Sibling captains are not quarantined: the refusal lives on the account and ends the
        /// moment the login works, where a timed quarantine would outlast the fix. The operator sees an event and an
        /// error signal naming the account and that it needs sign-in.
        /// </summary>
        private async Task RejectUsageAccountLoginAsync(Captain failingCaptain, UsageAccountSettings account, Mission mission, CancellationToken token)
        {
            UsageRoutingService.For(_Settings).MarkAccountLoginRejected(account.Id);
            string message = "Usage account " + account.Id + " needs sign-in: the provider rejected its login when captain " +
                failingCaptain.Id + " ran mission " + mission.Id + ". No captain on the account receives work until a usage " +
                "read succeeds or the account is signed in again.";
            _Logging.Warn(_Header + message);
            await EmitEventAsync(UsageAccountLoginRejectedEvent, message,
                entityType: "usage_account", entityId: account.Id,
                captainId: failingCaptain.Id, missionId: mission.Id,
                vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
            Signal signal = new Signal(SignalTypeEnum.Error, message);
            signal.FromCaptainId = failingCaptain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);
        }

        /// <summary>Event recorded when a captain's authentication failure refuses its usage account until sign-in.</summary>
        internal const string UsageAccountLoginRejectedEvent = "usage_account.login_rejected";

        /// <summary>
        /// Hold the failing captain's whole usage account Exhausted until the provider's retry time, capped at
        /// <see cref="UsageRoutingSettings.AccountHoldMaxMinutes"/>. Every captain on one account shares its allowance,
        /// so benching only the failed captain lets the re-route land on a sibling that fails the same way. One error
        /// is weak evidence for the whole account, so the cap bounds how long it holds the siblings, and a later run
        /// the provider serves on the account ends the hold (<see cref="ClearUsageAccountHoldAfterServedRunAsync"/>).
        /// Idle siblings are held now; a busy sibling keeps its running mission and routing refuses it new work while
        /// the account is Exhausted. A captain on no account is unaffected.
        /// </summary>
        private async Task HoldUsageAccountAsync(Captain failingCaptain, DateTime? retryAfterUtc, string holdKind, CancellationToken token)
        {
            UsageAccountSettings? account = CaptainAccountLaunch.FindAccount(_Settings.ModelTier.UsageRouting, failingCaptain.Id);
            if (account == null) return;

            DateTime nowUtc = DateTime.UtcNow;
            DateTime untilUtc = retryAfterUtc.HasValue && retryAfterUtc.Value.ToUniversalTime() > nowUtc
                ? retryAfterUtc.Value.ToUniversalTime()
                : nowUtc.AddSeconds(_Settings.CaptainQuarantine.DefaultBackoffSeconds);
            DateTime capUtc = nowUtc.AddMinutes(_Settings.ModelTier.UsageRouting.AccountHoldMaxMinutes);
            bool capped = untilUtc > capUtc;
            if (capped) untilUtc = capUtc;
            UsageRoutingService.For(_Settings).MarkAccountExhausted(account.Id, untilUtc, holdKind);

            string reason = UsageRoutingService.AccountHoldReason(account.Id, failingCaptain.Id, holdKind);
            AuthContext siblingScope = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Internal");
            int held = 0;
            int busy = 0;
            foreach (string siblingId in account.CaptainIds)
            {
                if (String.Equals(siblingId, failingCaptain.Id, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    CaptainQuarantineResult result = await _CaptainQuarantine.QuarantineCaptainAsync(siblingScope, siblingId, reason, untilUtc, token).ConfigureAwait(false);
                    if (result.Outcome == CaptainQuarantineOutcomeEnum.Quarantined) held++;
                    else if (result.Outcome == CaptainQuarantineOutcomeEnum.Busy) busy++;
                    else _Logging.Warn(_Header + "usage account hold: captain " + siblingId + " not held (" + result.Outcome + "): " + result.Message);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "usage account hold: could not hold captain " + siblingId + ": " + ex.Message);
                }
            }
            _Logging.Warn(_Header + "usage account " + account.Id + " held Exhausted (" + holdKind + ") until " +
                untilUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + (capped ? " (capped by accountHoldMaxMinutes)" : String.Empty) +
                " after captain " + failingCaptain.Id +
                " failed; held " + held + " idle sibling(s), " + busy + " busy sibling(s) keep their running mission");
        }

        /// <summary>Event recorded when a run the provider served ends a usage hold on its account.</summary>
        internal const string UsageAccountHoldClearedEvent = "usage_account.hold_cleared";

        /// <summary>
        /// End the usage hold on a captain's account after the captain finished a run the provider served: the
        /// account's timed quota or credit hold ends, and every captain on the account that a usage hold quarantined
        /// (the source captain included) returns to Idle. A served run proves the allowance a single error said was
        /// gone. Manual, crash-loop and indefinite holds stay. Never throws into completion handling.
        /// </summary>
        internal async Task ClearUsageAccountHoldAfterServedRunAsync(Captain captain, Mission mission, CancellationToken token)
        {
            try
            {
                UsageAccountSettings? account = CaptainAccountLaunch.FindAccount(_Settings.ModelTier.UsageRouting, captain.Id);
                if (account == null) return;
                bool holdCleared = UsageRoutingService.For(_Settings).ClearAccountExhausted(account.Id);
                List<string> released = new List<string>();
                List<Captain> quarantined = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Quarantined, token).ConfigureAwait(false);
                foreach (Captain held in quarantined)
                {
                    if (String.Equals(held.Id, captain.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!account.CaptainIds.Contains(held.Id, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!held.QuarantineUntilUtc.HasValue) continue;
                    if (!UsageRoutingService.IsAccountUsageHoldReason(held.QuarantineReason, account.Id)) continue;
                    if (await _Database.Captains.TryReleaseQuarantineAsync(held.Id, token).ConfigureAwait(false)) released.Add(held.Id);
                }
                if (!holdCleared && released.Count == 0) return;
                string message = "Usage account " + account.Id + " hold ended: captain " + captain.Id + " finished mission " + mission.Id +
                    " with a run the provider served" + (holdCleared ? "; the account hold is cleared" : String.Empty) +
                    (released.Count > 0 ? "; released " + String.Join(", ", released) : String.Empty) + ".";
                _Logging.Info(_Header + message);
                await EmitEventAsync(UsageAccountHoldClearedEvent, message,
                    entityType: "usage_account", entityId: account.Id,
                    captainId: captain.Id, missionId: mission.Id,
                    vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not clear the usage hold for captain " + captain.Id + "'s account: " + ex.Message);
            }
        }

        private Task HandleQuotaFailureRerouteAsync(
            Captain captain, Mission mission, string missionId, string failureReason,
            bool isCreditAuthFailure, DateTime? retryAfterUtc, CancellationToken token)
            => RerouteRecoverableFailureAsync(captain, mission, missionId, failureReason,
                label: "provider quota/credit/balance limit",
                benchReason: isCreditAuthFailure ? _CreditAuthQuarantineReason : failureReason,
                benchUntilUtc: retryAfterUtc,
                rerouteEventKind: "mission.quota_rerouted",
                exhaustedReason: "Every compatible captain hit a quota/credit/balance limit after re-routes; " +
                    "top up balance or add a captain with remaining quota, then re-dispatch.",
                token);

        /// <summary>
        /// Shared re-route for a RECOVERABLE provider-side mission failure (a content/cyber safeguard block, or a
        /// quota/credit/balance limit). Bench the affected captain and requeue the mission so it re-dispatches to a
        /// compatible peer -- the voyage stays alive instead of cascade-cancelling. Bounded by
        /// <see cref="Mission.RecoveryAttempts"/>: once every compatible captain has hit the same wall, the mission
        /// fails with an operator-actionable reason (never silence, never loop). Provider/model-neutral: whichever
        /// captain failed is the one benched -- no model name in the logic.
        /// </summary>
        private async Task RerouteRecoverableFailureAsync(
            Captain captain,
            Mission mission,
            string missionId,
            string failureReason,
            string label,
            string benchReason,
            DateTime? benchUntilUtc,
            string rerouteEventKind,
            string exhaustedReason,
            CancellationToken token)
        {
            string? vesselId = mission.VesselId;
            string? voyageId = mission.VoyageId;

            // Re-route budget exhausted: every compatible captain has hit the same wall. Fail with an
            // operator-actionable reason instead of looping.
            if (mission.RecoveryAttempts >= _RerouteMaxAttempts)
            {
                mission.Status = MissionStatusEnum.Failed;
                mission.FailureReason = exhaustedReason + " Last failure: " + failureReason;
                mission.ProcessId = null;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "mission " + missionId + " failed: " + label + " persisted after " +
                    mission.RecoveryAttempts + " re-routes");

                await _Missions.ReapTerminalMissionBranchAsync(mission, token).ConfigureAwait(false);
                await ReclaimDockAsync(captain, mission, token).ConfigureAwait(false);
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Failed, "mission_failed", _Logging, token).ConfigureAwait(false);
                await EmitEventAsync("mission.failed",
                    "Mission failed (" + label + ", all compatible captains exhausted): " + mission.Title,
                    entityType: "mission", entityId: mission.Id, captainId: captain.Id, missionId: mission.Id,
                    vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(voyageId))
                {
                    await HaltVoyageAsync(voyageId!, mission.Id, mission.FailureReason, token).ConfigureAwait(false);
                }
                return;
            }

            // Requeue the mission for re-routing to a compatible peer.
            if (!String.IsNullOrWhiteSpace(mission.HarborJobId))
            {
                int plannedRecoveryAttempts = mission.RecoveryAttempts + 1;
                if (!await BeginReroutedHarborCompletionAsync(captain, mission, failureReason, label, benchReason,
                    benchUntilUtc, rerouteEventKind, plannedRecoveryAttempts, token).ConfigureAwait(false))
                    throw new InvalidOperationException("The confirmed Harbor failure could not complete its durable reroute intent.");
                _Logging.Warn(_Header + "mission " + missionId + " re-routing after " + label + " (attempt "
                    + plannedRecoveryAttempts + "/" + _RerouteMaxAttempts + ")");
                return;
            }

            mission.Status = MissionStatusEnum.Pending;
            mission.AssignmentState = MissionAssignmentStateEnum.Pending;
            mission.FailureReason = failureReason;
            mission.CaptainId = null;
            mission.ProcessId = null;
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.RecoveryAttempts = mission.RecoveryAttempts + 1;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + missionId + " re-routing after " + label + " (attempt " +
                mission.RecoveryAttempts + "/" + _RerouteMaxAttempts + ")");

            await EmitEventAsync(rerouteEventKind, "Mission re-routed after " + label + ": " + mission.Title,
                entityType: "mission", entityId: mission.Id, captainId: captain.Id, missionId: mission.Id,
                vesselId: mission.VesselId, voyageId: mission.VoyageId, token: token).ConfigureAwait(false);

            // Keep retry authority while benching the affected captain; terminal exhaustion above
            // still reclaims its dock through the ordinary terminal path.
            await RetainRetryDockAsync(captain, mission, token).ConfigureAwait(false);
            await _CaptainQuarantine.QuarantineAsync(captain, benchReason, benchUntilUtc, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "captain " + captain.Id + " quarantined after " + label + " on mission " + missionId);

            Signal signal = new Signal(SignalTypeEnum.Progress,
                "Mission " + missionId + " re-routed after " + label + " (captain quarantined, attempt " +
                mission.RecoveryAttempts + ")");
            signal.FromCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(vesselId))
            {
                QueueVoyageAssignments(voyageId, vesselId!, new List<string> { mission.Id });
            }
            else
            {
                _Logging.Warn(_Header + "mission " + missionId + " re-routed but has no vessel id; relying on health-check retry sweep");
                _RetryDispatchNeeded = true;
            }
        }

        /// <summary>Event recorded when a mission is re-run after its model gateway timed out.</summary>
        internal const string ModelGatewayRetryEvent = "mission.model_gateway_retry";

        /// <summary>
        /// Whether a failure is a model gateway giving up before the model answered: an HTTP 502 or 504
        /// from the request to the model whose text says the operation was canceled or timed out, or is
        /// a proxy or gateway timeout.
        /// </summary>
        /// <param name="failureReason">Mission failure reason.</param>
        /// <returns>True for a gateway timeout.</returns>
        internal static bool IsModelGatewayTimeoutFailureReason(string? failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) return false;
            bool gatewayStatus = failureReason.Contains("status 502", StringComparison.OrdinalIgnoreCase)
                || failureReason.Contains("status 504", StringComparison.OrdinalIgnoreCase)
                || failureReason.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase)
                || failureReason.Contains("HTTP 504", StringComparison.OrdinalIgnoreCase);
            if (!gatewayStatus) return false;
            return failureReason.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
                || failureReason.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || failureReason.Contains("gateway timeout", StringComparison.OrdinalIgnoreCase)
                || failureReason.Contains("Proxy error", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Names the configured route without credentials, URL paths, or query parameters.
        /// A runtime default is labelled as unresolved rather than inferred from process output.
        /// </summary>
        private static string GetModelGatewayEndpointLabel(Captain captain)
        {
            if (!String.IsNullOrWhiteSpace(captain.ModelEndpointId)) return captain.ModelEndpointId;
            string? baseUrl = captain.ApiBaseUrl;
            if (captain.Runtime == AgentRuntimeEnum.Mux)
            {
                try
                {
                    MuxCaptainOptions? options = CaptainRuntimeOptions.GetMuxOptions(captain);
                    if (!String.IsNullOrWhiteSpace(options?.Endpoint)) return options.Endpoint;
                    baseUrl = options?.BaseUrl ?? baseUrl;
                }
                catch (System.Text.Json.JsonException)
                {
                    // Invalid configuration cannot supply a trustworthy endpoint identity.
                    return "unresolved/" + captain.Runtime;
                }
            }
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? endpoint)
                && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps))
            {
                return endpoint.Scheme + "://" + endpoint.IdnHost
                    + (endpoint.IsDefaultPort ? String.Empty : ":" + endpoint.Port);
            }
            return "runtime-default/" + captain.Runtime;
        }

        private async Task<int> CountModelGatewayRetriesAsync(string missionId, CancellationToken token)
        {
            try
            {
                EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = missionId,
                    EventType = ModelGatewayRetryEvent,
                    PageNumber = 1,
                    PageSize = 10
                }, token).ConfigureAwait(false);
                return (int)page.TotalRecords;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // Without the count the budget cannot be proven, so no re-run is spent.
                _Logging.Warn(_Header + "could not count model gateway retries for mission " + missionId + ": " + ex.Message);
                return Int32.MaxValue;
            }
        }

        private static bool IsCaptainUnavailableFailureReason(string failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason))
                return false;

            string normalized = NormalizeProcessExitFailureReason(failureReason);

            // Completion-verification failures (health check found an active mission with no
            // recoverable process id). These are transient orchestration faults, not work
            // failures, so they take the same retry path as captain-unavailable runtime faults.
            if (normalized.Contains("agent completion cannot be verified", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("no process ID is recorded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ProviderQuotaLimitDetector.IsQuotaLimitSignal(normalized))
            {
                return true;
            }

            if (ProviderQuotaLimitDetector.IsAuthFailureSignal(normalized))
            {
                return true;
            }

            bool mentionsModel = normalized.Contains("model", StringComparison.OrdinalIgnoreCase);
            bool modelUnavailable =
                normalized.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("unavailable", StringComparison.OrdinalIgnoreCase);

            return mentionsModel && modelUnavailable;
        }

        private static string NormalizeProcessExitFailureReason(string failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason))
                return String.Empty;

            string normalized = failureReason.Trim();

            // An [ARMADA:ACTIVITY] marker is log framing, not failure prose. The captain's own-runtime
            // provider telemetry that reaches here keeps its text, but the marker itself must never be
            // stored in a mission failure reason.
            const string activityMarker = "[ARMADA:ACTIVITY]";
            if (normalized.StartsWith(activityMarker, StringComparison.Ordinal))
            {
                normalized = normalized.Substring(activityMarker.Length).Trim();
            }

            if (normalized.StartsWith("[stderr]", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("[stderr]".Length).Trim();
            }

            return normalized;
        }

        private async Task HaltVoyageAsync(string voyageId, string failedMissionId, string failureReason, CancellationToken token)
        {
            Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            if (voyage == null) return;
            if (voyage.Status == VoyageStatusEnum.Cancelled || voyage.Status == VoyageStatusEnum.Complete) return;

            // A halt is a voyage cancel, so it goes through the one voyage cancel: the captain of every mission still
            // running is recalled first, which stops its agent process and releases it, and then the voyage and its
            // live missions are written Cancelled and its armed Checks discarded. The failed mission is already
            // Failed, so the cancel leaves it as it is.
            VoyageCancellationResult halted;
            try
            {
                halted = await VoyageCancellation.CancelAsync(
                    _Database,
                    voyage,
                    "Voyage halted after mission " + failedMissionId + " failed: " + failureReason,
                    RecallCaptainAsync,
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "voyage " + voyageId + " was not halted after mission " + failedMissionId
                    + " failed, because a running captain could not be recalled; the voyage stays live for recovery: " + ex.Message);
                return;
            }

            if (halted.CancelledMissions.Count > 0)
                _Logging.Info(_Header + "voyage " + voyage.Id + " halted: cancelled " + halted.CancelledMissions.Count + " live mission(s)");

            await EmitEventAsync("voyage.cancelled", "Voyage halted after mission " + failedMissionId + " failed",
                entityType: "voyage", entityId: voyage.Id, missionId: failedMissionId, voyageId: voyage.Id, token: token).ConfigureAwait(false);
        }

        private async Task MaintainCaptainPoolAsync(CancellationToken token)
        {
            List<Captain> allCaptains = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            int idleCount = allCaptains.Count(c => c.State == CaptainStateEnum.Idle);
            int totalCount = allCaptains.Count;

            int needed = _Settings.MinIdleCaptains - idleCount;
            if (needed <= 0) return;

            // Respect max captains cap
            if (_Settings.MaxCaptains > 0)
            {
                int headroom = _Settings.MaxCaptains - totalCount;
                if (headroom <= 0) return;
                needed = Math.Min(needed, headroom);
            }

            _Logging.Info(_Header + "captain pool: " + idleCount + " idle, need " + needed + " more to reach minimum of " + _Settings.MinIdleCaptains);

            for (int i = 0; i < needed; i++)
            {
                // Name captains sequentially based on total count
                string name = "captain-" + (totalCount + i + 1);
                Captain captain = new Captain(name);
                captain.State = CaptainStateEnum.Idle;
                await _Database.Captains.CreateAsync(captain, token).ConfigureAwait(false);
                _Logging.Info(_Header + "auto-spawned captain " + captain.Id + " (" + name + ")");
            }
        }

        #endregion
    }
}
