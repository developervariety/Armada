namespace Armada.Core.Services
{
    using System.Text;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for mission lifecycle management.
    /// </summary>
    public class MissionService : IMissionService
    {
        #region Public-Members

        /// <inheritdoc />
        public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }

        /// <summary>
        /// Take the telemetry the lifecycle handler recorded for a mission's latest launch (runtime-reported usage and model), or null.
        /// </summary>
        public Func<string, ChatTurnTelemetryRecorder?>? OnTakeMissionTelemetry { get; set; }

        /// <inheritdoc />
        public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }

        /// <inheritdoc />
        public Func<string, string?>? OnGetMissionOutput { get; set; }

        /// <summary>
        /// Optional callback invoked when a mission enters an approval-required review state.
        /// </summary>
        public Action<Mission>? OnReviewRequested { get; set; }

        /// <summary>
        /// Optional gate consulted before a mission is assigned to the selected captain. Returns true to allow
        /// the assignment, or false to defer it (the mission stays Pending and is retried). Used to enforce the
        /// "require a connected Harbor" policy: when set and it returns false, the mission waits instead of
        /// running in-process.
        /// </summary>
        public Func<Mission, Captain, Task<bool>>? CanAssignMissionAsync { get; set; }

        /// <summary>
        /// Chooses where a mission's dock is created before it is provisioned: on a Harbor that can serve the vessel, on
        /// the Admiral's host, or nowhere yet (the mission stays Pending). When null every dock is created on the
        /// Admiral's host.
        /// </summary>
        public Func<Mission, Captain, Vessel, Task<DockPlacement>>? ResolveDockPlacementAsync { get; set; }

        /// <summary>
        /// Where docks live. Operations on a Harbor-hosted dock (instruction files, git, the Definition-of-Done gate) run
        /// on its Harbor through this; without it every dock is treated as local.
        /// </summary>
        public DockHostResolver? DockHosts { get; set; }

        #endregion

        #region Private-Members

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _CompletionsInFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _ReviewDecisionGate = new SemaphoreSlim(1, 1);

        private const int _MaxLaunchFailuresBeforeFail = 6;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MissionLaunchFailure> _LaunchFailures = new System.Collections.Concurrent.ConcurrentDictionary<string, MissionLaunchFailure>(StringComparer.Ordinal);

        private string _Header = "[MissionService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService? _Git;
        private IDockService _Docks;
        private ICaptainService _Captains;
        private IPromptTemplateService? _PromptTemplates;
        private IDefinitionOfDoneGate? _DefinitionOfDone;
        private readonly IDockFileSystem _LocalFiles = new LocalDockFileSystem();
        private readonly IHostCommandExecutor _LocalCommands = new LocalHostCommandExecutor();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _PlacementWaitReasons =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private const string ArchitectHandoffMarker = "<!-- ARMADA:ARCHITECT-HANDOFF -->";
        private const string ReviewFeedbackMarker = "<!-- ARMADA:REVIEW-FEEDBACK -->";
        private const string ReviewerGuidanceMarker = "<!-- ARMADA:REVIEWER-GUIDANCE -->";
        private static readonly System.Text.RegularExpressions.Regex _ScopedFilesDirectiveRegex =
            new System.Text.RegularExpressions.Regex(@"^\s*(?:Touch|Edit|Modify)\s+only\s+(?<files>.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex _ScopedFileTokenRegex =
            new System.Text.RegularExpressions.Regex(
                @"(?<path>(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+|[A-Za-z0-9_.-]+\.(?:cs|csproj|sln|md|json|yaml|yml|ts|tsx|js|jsx|css|html|sh|bat))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly HashSet<string> _IgnoredMissionArtifactFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CODEX.md",
            "CLAUDE.md",
            "MUX.md"
        };

        /// <summary>
        /// Tracks in-flight mission assignment operations by mission ID.
        /// Prevents duplicate provisioning/launch when multiple dispatch paths
        /// race on the same mission.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, byte> _InFlightAssignments =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>
        /// Parsed mission definition extracted from an architect's output.
        /// </summary>
        private class ParsedArchitectMission
        {
            /// <summary>
            /// Mission title.
            /// </summary>
            public string Title { get; set; } = "";

            /// <summary>
            /// Mission description.
            /// </summary>
            public string Description { get; set; } = "";

            /// <summary>
            /// Optional dependency reference emitted by the architect (legacy marker format: a "Depends on:" line).
            /// </summary>
            public string? DependsOnReference { get; set; } = null;

            /// <summary>
            /// 1-based index of an earlier parsed mission this one depends on (structured armada-plan format).
            /// </summary>
            public int? DependsOnIndex { get; set; } = null;

            /// <summary>
            /// Whether the mission waits until the other Worker missions in the voyage settle.
            /// </summary>
            public bool WaitForOtherMissions { get; set; } = false;
        }

        private sealed class WorktreePlaybookLocation
        {
            public string ResolvedPath { get; set; } = String.Empty;

            public string RelativePath { get; set; } = String.Empty;
        }

        private sealed class ArchitectDependencyExtractionResult
        {
            public string Description { get; set; } = String.Empty;

            public string? DependsOnReference { get; set; } = null;
        }

        private sealed class MissionOutcomeSignalDefinition
        {
            public SignalTypeEnum Type { get; set; }

            public string Payload { get; set; } = String.Empty;
        }

        private sealed class MissionOutcomeEventDefinition
        {
            public string EventType { get; set; } = String.Empty;

            public string EventMessage { get; set; } = String.Empty;
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="docks">Dock service.</param>
        /// <param name="captains">Captain service.</param>
        /// <param name="promptTemplates">Prompt template service (optional for backward compatibility).</param>
        /// <param name="git">Git service used for branch cleanup on non-landed intermediate stages.</param>
        /// <param name="definitionOfDone">Optional in-dock Definition-of-Done gate.</param>
        public MissionService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IDockService docks,
            ICaptainService captains,
            IPromptTemplateService? promptTemplates = null,
            IGitService? git = null,
            IDefinitionOfDoneGate? definitionOfDone = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git;
            _Docks = docks ?? throw new ArgumentNullException(nameof(docks));
            _Captains = captains ?? throw new ArgumentNullException(nameof(captains));
            _PromptTemplates = promptTemplates;
            _DefinitionOfDone = definitionOfDone;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<MissionAssignmentBlocker?> GetAssignmentBlockerAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (mission.Status != MissionStatusEnum.Pending) return null;

            MissionAssignmentBlocker blocker = new MissionAssignmentBlocker();

            Vessel? vessel = String.IsNullOrEmpty(mission.VesselId) ? null : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.VesselMissing;
                blocker.Summary = String.IsNullOrEmpty(mission.VesselId)
                    ? "This mission has no vessel, so it cannot be assigned."
                    : "This mission's vessel no longer exists, so it cannot be assigned.";
                return blocker;
            }

            if (UsesSharedLocalAndWorkingDirectory(vessel))
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.VesselMisconfigured;
                blocker.Summary = "Vessel " + vessel.Name + " uses the same directory for its local repository and working directory, so a dock cannot be provisioned. Change the vessel's working directory.";
                return blocker;
            }

            if (_LaunchFailures.TryGetValue(mission.Id, out MissionLaunchFailure? launchFailure))
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.LaunchFailing;
                blocker.UntilUtc = launchFailure.NextAttemptUtc;
                blocker.Summary = "Starting the captain failed " + launchFailure.Count + (launchFailure.Count == 1 ? " time" : " times")
                    + ": " + launchFailure.Reason + " Armada tries again at " + launchFailure.NextAttemptUtc.ToString("HH:mm:ss") + " UTC.";
                return blocker;
            }

            if (!String.IsNullOrEmpty(mission.DependsOnMissionId))
            {
                Mission? dependency = await _Database.Missions.ReadAsync(mission.DependsOnMissionId, token).ConfigureAwait(false);
                blocker.DependsOnMissionId = mission.DependsOnMissionId;
                if (dependency == null)
                {
                    blocker.Reason = MissionAssignmentBlockerReasonEnum.DependencyNotFinished;
                    blocker.Summary = "Waiting for mission " + mission.DependsOnMissionId + ", which no longer exists.";
                    return blocker;
                }

                if (dependency.Status != MissionStatusEnum.Complete && dependency.Status != MissionStatusEnum.WorkProduced)
                {
                    blocker.Reason = MissionAssignmentBlockerReasonEnum.DependencyNotFinished;
                    blocker.Summary = "Waiting for mission '" + dependency.Title + "' (" + dependency.Status + ") to finish.";
                    return blocker;
                }

                if (dependency.Status == MissionStatusEnum.WorkProduced && !IsPipelineHandoffPrepared(mission, dependency))
                {
                    blocker.Reason = MissionAssignmentBlockerReasonEnum.DependencyHandoffPending;
                    blocker.Summary = "Mission '" + dependency.Title + "' finished; Armada is preparing the handoff to this stage.";
                    return blocker;
                }

                blocker.DependsOnMissionId = null;
            }

            if (await ShouldDeferArchitectSequencedMissionAsync(mission, token).ConfigureAwait(false))
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.WaitingForVoyageWorkers;
                blocker.Summary = "Waiting for the voyage's other implementation pipelines, including reviews and checks, to settle.";
                return blocker;
            }

            List<Mission> vesselMissions = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            List<Mission> activeMissions = vesselMissions
                .Where(m => m.Id != mission.Id && (m.Status == MissionStatusEnum.Assigned || m.Status == MissionStatusEnum.InProgress))
                .ToList();
            List<Mission> broadMissions = activeMissions.Where(m => IsBroadScope(m)).ToList();
            if (broadMissions.Count > 0)
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.VesselBroadScopeMissionActive;
                blocker.BlockingMissionIds = broadMissions.Select(m => m.Id).ToList();
                blocker.Summary = "Broad-scope mission '" + broadMissions[0].Title + "' holds vessel " + vessel.Name + " until it finishes.";
                return blocker;
            }

            if (activeMissions.Count > 0 && IsBroadScope(mission))
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.BroadScopeWaitingForVessel;
                blocker.BlockingMissionIds = activeMissions.Select(m => m.Id).ToList();
                blocker.Summary = "This is a broad-scope mission; it waits until vessel " + vessel.Name + " has no active missions (" + activeMissions.Count + " running).";
                return blocker;
            }

            if (activeMissions.Count > 0 && !vessel.AllowConcurrentMissions)
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.VesselConcurrencyLimit;
                blocker.BlockingMissionIds = activeMissions.Select(m => m.Id).ToList();
                blocker.Summary = "Vessel " + vessel.Name + " runs one mission at a time and is busy with '" + activeMissions[0].Title + "'. Turn on Allow Concurrent Missions on the vessel to run more at once.";
                return blocker;
            }

            // Captain availability: the same pool FindAvailableCaptainAsync draws from (idle captains), checked
            // without resolving or persisting a preferred captain.
            List<Captain> allCaptains = (await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false))
                .Where(c => CaptainVisibleToMission(c, mission)).ToList();
            if (allCaptains.Count == 0)
            {
                blocker.Reason = MissionAssignmentBlockerReasonEnum.NoCaptains;
                blocker.Summary = "Waiting for a captain: there are no captains. Create one to run missions.";
                return blocker;
            }

            List<Captain> idleCaptains = allCaptains.Where(c => c.State == CaptainStateEnum.Idle).ToList();
            if (idleCaptains.Count > 0)
            {
                bool eligible = await IsAnyCaptainEligibleAsync(idleCaptains, mission, token).ConfigureAwait(false);
                if (eligible)
                {
                    blocker.Reason = MissionAssignmentBlockerReasonEnum.AwaitingDispatch;
                    blocker.Summary = "A captain is free; the mission is assigned on the next dispatch cycle.";
                    return blocker;
                }

                blocker.Reason = MissionAssignmentBlockerReasonEnum.NoEligibleCaptain;
                blocker.Summary = "Waiting for a captain: no idle captain may take this mission"
                    + (!String.IsNullOrEmpty(mission.Persona) ? " (persona " + mission.Persona + ")" : "")
                    + (mission.Tier.HasValue ? " (tier " + mission.Tier.Value + " or higher)" : "")
                    + ". Check the persona's captain priorities and the captains' allowed personas and tiers.";
                await DescribeCaptainsAsync(blocker, allCaptains, mission.TenantId, token).ConfigureAwait(false);
                return blocker;
            }

            blocker.Reason = MissionAssignmentBlockerReasonEnum.NoIdleCaptain;
            await DescribeCaptainsAsync(blocker, allCaptains, mission.TenantId, token).ConfigureAwait(false);
            List<DateTime> quarantineEnds = blocker.Captains
                .Where(c => c.QuarantineUntilUtc.HasValue)
                .Select(c => c.QuarantineUntilUtc!.Value)
                .ToList();
            if (allCaptains.All(c => c.State == CaptainStateEnum.Quarantined) && quarantineEnds.Count > 0)
            {
                blocker.UntilUtc = quarantineEnds.Min();
            }

            if (blocker.Captains.Count == 1)
            {
                blocker.Summary = "Waiting for a captain: " + (blocker.Captains[0].CaptainName ?? blocker.Captains[0].CaptainId) + " is " + blocker.Captains[0].Detail + ".";
            }
            else
            {
                blocker.Summary = "Waiting for a captain: every captain is busy.";
            }

            return blocker;
        }

        /// <inheritdoc />
        public async Task<bool> TryAssignAsync(Mission mission, Vessel vessel, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            if (!_InFlightAssignments.TryAdd(mission.Id, 0))
            {
                _Logging.Debug(_Header + "mission " + mission.Id + " assignment already in flight -- skipping duplicate");
                return false;
            }

            try
            {
                Mission? latestMission = null;
                if (!String.IsNullOrEmpty(mission.TenantId))
                {
                    latestMission = await _Database.Missions.ReadAsync(mission.TenantId, mission.Id, token).ConfigureAwait(false);
                }
                if (latestMission == null)
                {
                    latestMission = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                }
                if (latestMission != null)
                {
                    mission = latestMission;
                }

                if (mission.Status != MissionStatusEnum.Pending)
                {
                    _Logging.Debug(_Header + "mission " + mission.Id + " is " + mission.Status +
                        " in the database -- skipping assignment");
                    return false;
                }

                if (_LaunchFailures.TryGetValue(mission.Id, out MissionLaunchFailure? backoff) && DateTime.UtcNow < backoff.NextAttemptUtc
                    && !await HasUntriedIdleCaptainAsync(mission, backoff, token).ConfigureAwait(false))
                {
                    _Logging.Debug(_Header + "mission " + mission.Id + " backing off after " + backoff.Count + " failed launch(es) until " + backoff.NextAttemptUtc.ToString("o"));
                    return false;
                }

                if (!String.IsNullOrEmpty(mission.VoyageId))
                {
                    Voyage? voyage = await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
                    if (voyage != null &&
                        (voyage.Status == VoyageStatusEnum.Cancelled ||
                         voyage.Status == VoyageStatusEnum.Failed ||
                         voyage.Status == VoyageStatusEnum.Complete))
                    {
                        mission.Status = MissionStatusEnum.Cancelled;
                        mission.ProcessId = null;
                        mission.CompletedUtc = DateTime.UtcNow;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        if (String.IsNullOrWhiteSpace(mission.FailureReason))
                        {
                            mission.FailureReason = "Parent voyage " + voyage.Id + " is " + voyage.Status + ".";
                        }

                        if (!mission.FailureKind.HasValue)
                        {
                            mission.FailureKind = MissionFailureKindEnum.DependencyFailed;
                        }

                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "mission " + mission.Id + " belongs to terminal voyage " + voyage.Id +
                            " (" + voyage.Status + ") -- cancelling instead of assigning");
                        return false;
                    }
                }

                if (UsesSharedLocalAndWorkingDirectory(vessel))
                {
                    _Logging.Warn(_Header + "vessel " + vessel.Id +
                        " uses the same path for LocalPath and WorkingDirectory (" +
                        (vessel.WorkingDirectory ?? vessel.LocalPath ?? "unknown") +
                        ") -- skipping mission assignment because dock provisioning requires a separate bare repository path");
                    return false;
                }

            // Check pipeline dependency -- skip if the mission depends on another that hasn't completed
            // or if the downstream handoff has not yet populated the mission's branch/context.
            if (!String.IsNullOrEmpty(mission.DependsOnMissionId))
            {
                Mission? dependency = await _Database.Missions.ReadAsync(mission.DependsOnMissionId, token).ConfigureAwait(false);
                if (dependency == null)
                {
                    _Logging.Warn(_Header + "mission " + mission.Id + " depends on " + mission.DependsOnMissionId + " which was not found -- skipping assignment");
                    return false;
                }

                if (dependency.Status != MissionStatusEnum.Complete &&
                    dependency.Status != MissionStatusEnum.WorkProduced)
                {
                    // Dependency not yet satisfied -- don't assign
                    return false;
                }

                // A dependency in WorkProduced means the upstream agent finished, but the
                // synchronous completion flow may still be preparing the downstream mission.
                // Do not launch the next stage until it has been explicitly handed the same
                // branch as the dependency. This prevents workers from starting with the
                // original top-level dispatch prompt before architect/test/judge handoff runs.
                if (dependency.Status == MissionStatusEnum.WorkProduced &&
                    !IsPipelineHandoffPrepared(mission, dependency))
                {
                    _Logging.Debug(_Header + "mission " + mission.Id + " depends on " + dependency.Id +
                        " which is WorkProduced, but handoff is not prepared yet -- deferring assignment");
                    return false;
                }
            }

            if (await ShouldDeferArchitectSequencedMissionAsync(mission, token).ConfigureAwait(false))
            {
                _Logging.Debug(_Header + "mission " + mission.Id +
                    " is architect-marked as sequential after implementation work -- deferring assignment");
                return false;
            }

            // Check for vessel-level lock (broad-scope missions block new assignments)
            List<Mission> activeMissions = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            List<Mission> broadMissions = activeMissions.Where(m =>
                (m.Status == MissionStatusEnum.InProgress || m.Status == MissionStatusEnum.Assigned) &&
                IsBroadScope(m)).ToList();

            if (broadMissions.Count > 0)
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has a broad-scope mission in progress — deferring assignment of " + mission.Id);
                return false;
            }

            // Check if this mission is broad-scope and vessel already has active work
            // Only count truly active missions (agent running or assigned).
            // WorkProduced and PullRequestOpen are post-agent states where the agent
            // has finished -- they should NOT block new mission dispatch.
            int concurrentCount = activeMissions.Count(m =>
                m.Status == MissionStatusEnum.Assigned ||
                m.Status == MissionStatusEnum.InProgress);

            if (IsBroadScope(mission) && concurrentCount > 0)
            {
                _Logging.Warn(_Header + "broad-scope mission " + mission.Id + " deferred — vessel " + vessel.Id + " has " + concurrentCount + " active mission(s)");
                return false;
            }

            // Enforce per-vessel serialization unless explicitly allowed
            if (!vessel.AllowConcurrentMissions && concurrentCount > 0)
            {
                _Logging.Info(_Header + "vessel " + vessel.Id + " already has " + concurrentCount + " active mission(s); deferring " + mission.Id + " (AllowConcurrentMissions=false)");
                return false;
            }

            // Warn about concurrent missions on same vessel when allowed
            if (vessel.AllowConcurrentMissions && concurrentCount > 0)
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " already has " + concurrentCount + " active mission(s) — potential for conflicts (AllowConcurrentMissions=true)");
            }

            // Resolve the mission's preferred captain + fallback tier (per-persona default / voyage override)
            // before assignment, so per-step captain selection applies to every mission of a step, including
            // fan-out missions that were created without an explicit dictated captain.
            await ResolvePreferredCaptainAsync(mission, token).ConfigureAwait(false);

            // Find an idle captain: the preferred captain when idle, otherwise fall back by persona/tier.
            Captain? captain = await FindAvailableCaptainAsync(mission, token).ConfigureAwait(false);
            if (captain == null)
            {
                _Logging.Warn(_Header + "no idle captains available for mission " + mission.Id +
                    (mission.Persona != null ? " (persona: " + mission.Persona + ")" : ""));
                return false;
            }

            // Policy gate: when the deployment requires a connected Harbor, defer assignment until one is
            // available for this mission rather than running the captain in-process. The mission stays Pending
            // and is retried on the next dispatch cycle.
            if (CanAssignMissionAsync != null)
            {
                bool allowed = await CanAssignMissionAsync(mission, captain).ConfigureAwait(false);
                if (!allowed)
                {
                    _Logging.Info(_Header + "deferring mission " + mission.Id + ": launch policy requires the requesting user's Harbor to be connected and eligible, and none is yet");
                    return false;
                }
            }

            // Choose where the dock lives before creating it: a Harbor that can serve the vessel, the Admiral's host, or
            // nowhere yet (the mission stays Pending and is retried on the next dispatch cycle).
            DockPlacement placement = DockPlacement.OnAdmiral("no Harbor placement is configured");
            if (ResolveDockPlacementAsync != null)
            {
                placement = await ResolveDockPlacementAsync(mission, captain, vessel).ConfigureAwait(false);
                if (placement.Wait)
                {
                    // Log a reason once per mission until it changes, not on every dispatch cycle.
                    string previousReason = _PlacementWaitReasons.TryGetValue(mission.Id, out string? known) ? known : String.Empty;
                    if (!String.Equals(previousReason, placement.Reason, StringComparison.Ordinal))
                    {
                        _PlacementWaitReasons[mission.Id] = placement.Reason;
                        _Logging.Warn(_Header + "mission " + mission.Id + " waits: " + placement.Reason);
                    }

                    return false;
                }
            }

            _PlacementWaitReasons.TryRemove(mission.Id, out string? _);

            // Missions with an existing branch continue work on that branch. This covers
            // downstream pipeline stages, review rework loops, and resumed missions.
            bool preserveExistingBranch = !String.IsNullOrEmpty(mission.BranchName);
            string branchName = preserveExistingBranch
                ? mission.BranchName!
                : BuildMissionBranchName(captain, mission);

            // Claim the captain before any slow work. The claim is atomic (only an Idle captain is taken), so a
            // concurrent dispatcher can never take it in the gap, and from here on the captain names this mission,
            // which keeps orphan recovery from reverting the assignment while its dock is being provisioned.
            bool claimed = await _Database.Captains.TryClaimAsync(captain.Id, mission.Id, null, token).ConfigureAwait(false);
            if (!claimed)
            {
                _Logging.Info(_Header + "captain " + captain.Id + " was claimed by another mission before " + mission.Id + " could take it; retrying next cycle");
                return false;
            }

            mission.BranchName = branchName;
            mission.CaptainId = captain.Id;
            mission.Status = MissionStatusEnum.Assigned;
            mission.LastUpdateUtc = DateTime.UtcNow;
            bool assignmentRecorded = await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { MissionStatusEnum.Pending }, token).ConfigureAwait(false);
            if (!assignmentRecorded)
            {
                // Someone else moved the mission (cancelled, or assigned by another caller) after it was read.
                _Logging.Info(_Header + "mission " + mission.Id + " left Pending before its assignment was recorded; releasing captain " + captain.Id);
                await _Database.Captains.TryReleaseFromMissionAsync(captain.Id, mission.Id, CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = mission.Id;
            captain.LastHeartbeatUtc = DateTime.UtcNow;
            captain.LastUpdateUtc = DateTime.UtcNow;

            // Everything from here to launch runs inside one rollback boundary: any failure puts the mission back to
            // Pending, releases the captain only while it still holds this mission, and reclaims the dock.
            Dock? dock = null;
            try
            {
                try
                {
                    _Logging.Debug(_Header + "provisioning dock for mission " + mission.Id + " on vessel " + vessel.Id + " with captain " + captain.Id);
                    if (placement.HarborId != null)
                        dock = await _Docks.ProvisionOnHarborAsync(vessel, captain, branchName, mission.Id, placement.HarborId, token).ConfigureAwait(false);
                    else
                        dock = await _Docks.ProvisionAsync(vessel, captain, branchName, mission.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
                {
                    _Logging.Warn(_Header + "dock provisioning threw for mission " + mission.Id + " vessel " + vessel.Id + " captain " + captain.Id + ": "
                        + (ex is DockProvisioningException ? ex.Message : ex.ToString()));
                    await RollbackAssignmentAsync(mission, captain, null, preserveExistingBranch).ConfigureAwait(false);
                    return false;
                }

                if (dock == null)
                {
                    _Logging.Warn(_Header + "dock provisioning failed for captain " + captain.Id + " vessel " + vessel.Id + " mission " + mission.Id + " -- reverting to Pending");
                    await RollbackAssignmentAsync(mission, captain, null, preserveExistingBranch).ConfigureAwait(false);
                    return false;
                }

                // Track the dock on the mission and the captain, each only while they still describe this assignment.
                mission.DockId = dock.Id;
                mission.LastUpdateUtc = DateTime.UtcNow;
                bool dockRecorded = await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { MissionStatusEnum.Assigned }, token).ConfigureAwait(false);
                bool captainHasDock = dockRecorded && await _Database.Captains.TrySetDockAsync(captain.Id, mission.Id, dock.Id, token).ConfigureAwait(false);
                if (!captainHasDock)
                {
                    _Logging.Warn(_Header + "mission " + mission.Id + " or captain " + captain.Id + " changed while dock " + dock.Id + " was provisioned; abandoning this assignment");
                    await RollbackAssignmentAsync(mission, captain, dock, preserveExistingBranch).ConfigureAwait(false);
                    return false;
                }

                captain.CurrentDockId = dock.Id;

                // Commit continuity: a later stage must start from exactly the commit the stage before it produced. A dock
                // that lacks it (the branch lived on another host and this one created it from the default branch) would
                // silently discard upstream work, so the stage is refused with the reason instead.
                string? continuityFailure = preserveExistingBranch ? await CheckStageCommitContinuityAsync(mission, dock, token).ConfigureAwait(false) : null;
                if (continuityFailure != null)
                {
                    _Logging.Warn(_Header + "mission " + mission.Id + " refused: " + continuityFailure);
                    await FailAssignmentAsync(mission, captain, dock, continuityFailure).ConfigureAwait(false);
                    return false;
                }

                // Create assignment signal
                Signal signal = new Signal(SignalTypeEnum.Assignment, mission.Title);
                signal.TenantId = mission.TenantId;
                signal.UserId = mission.UserId;
                signal.ToCaptainId = captain.Id;
                await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

                // Generate mission CLAUDE.md into worktree (on the dock's Harbor for a Harbor-hosted dock)
                DockHost? harborHost = HarborHostFor(dock);
                await GenerateClaudeMdOnHostAsync(harborHost, dock.WorktreePath!, mission, vessel, captain, token).ConfigureAwait(false);
                await EnsureMissionInstructionsPresentAsync(harborHost, dock.WorktreePath!, mission, captain, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                _Logging.Warn(_Header + "could not prepare mission " + mission.Id + " for captain " + captain.Id + ": " + ex.Message);
                await RollbackAssignmentAsync(mission, captain, dock, preserveExistingBranch).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                await RollbackAssignmentAsync(mission, captain, dock, preserveExistingBranch).ConfigureAwait(false);
                throw;
            }

            // Launch agent process via captain service
            if (_Captains.OnLaunchAgent == null)
            {
                _Logging.Warn(_Header + "no OnLaunchAgent handler configured -- cannot launch agent for captain " + captain.Id);
                await RollbackAssignmentAsync(mission, captain, dock, preserveExistingBranch).ConfigureAwait(false);
                return false;
            }

            int processId;
            try
            {
                processId = await _Captains.OnLaunchAgent.Invoke(captain, mission, dock).ConfigureAwait(false);
            }
            catch (MissionLaunchConfigurationException configuration)
            {
                // A deterministic policy/configuration problem cannot be repaired by repeatedly launching.
                // Keep it visible as a terminal operational failure, without a provider call or rescue loop.
                _LaunchFailures.TryRemove(mission.Id, out MissionLaunchFailure? _);
                await FailAssignmentAsync(mission, captain, dock, configuration.Message).ConfigureAwait(false);

                // The same configuration fails every mission this captain is given; take it out of the pool for the
                // quarantine window instead of letting it be picked first again.
                await QuarantineMisconfiguredCaptainAsync(captain, configuration.Message).ConfigureAwait(false);
                return false;
            }
            catch (HarborDockNotFoundException dockMissing)
            {
                // The Harbor chosen for the mission does not have its dock, and the mission cannot run on the Admiral
                // host either. Returning it to Pending would provision a new dock and launch into the same missing path
                // on every dispatch cycle, so the mission fails with the actionable reason instead, and its dependents
                // and voyage settle like any other terminal failure.
                _Logging.Warn(_Header + "mission " + mission.Id + " failed at launch: " + dockMissing.Message);
                await FailAssignmentAsync(mission, captain, dock, dockMissing.Message).ConfigureAwait(false);
                return false;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                _Logging.Warn(_Header + "failed to launch agent for captain " + captain.Id + ": " + ex.ToString());

                // Back off instead of re-provisioning and re-launching on every dispatch cycle; after repeated
                // failures with the same cause (a CLI that is not installed where the mission lands) fail the
                // mission with the reason rather than loop forever.
                MissionLaunchFailure failure = RecordLaunchFailure(mission, captain, ex);
                if (failure.Count >= _MaxLaunchFailuresBeforeFail && IsRuntimeMissing(ex))
                {
                    _LaunchFailures.TryRemove(mission.Id, out MissionLaunchFailure? _);
                    await FailAssignmentAsync(mission, captain, dock,
                        "Captain " + captain.Name + " could not be started " + failure.Count + " times: " + failure.Reason
                        + " If this mission should run on your Harbor, connect it and turn on Require Harbor for launch (Server settings), then restart the mission.").ConfigureAwait(false);
                    return false;
                }

                await RollbackAssignmentAsync(mission, captain, dock, preserveExistingBranch).ConfigureAwait(false);

                try
                {
                    Signal errorSignal = new Signal(SignalTypeEnum.Error, "Failed to launch agent: " + ex.Message);
                    errorSignal.TenantId = mission.TenantId;
                    errorSignal.UserId = mission.UserId;
                    errorSignal.FromCaptainId = captain.Id;
                    await _Database.Signals.CreateAsync(errorSignal, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception signalEx)
                {
                    _Logging.Warn(_Header + "could not record launch failure signal for mission " + mission.Id + ": " + signalEx.Message);
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                await RollbackAssignmentAsync(mission, captain, dock, preserveExistingBranch).ConfigureAwait(false);
                throw;
            }

            // A fast in-process captain (an ApiEndpoint loop against a quick or failing endpoint) can finish before the
            // launch call returns, and its exit handler may already have moved the mission on (WorkProduced, Failed) and
            // released the captain. Both writes below are therefore compare-and-set: the captain's process handle is
            // recorded only while it still holds this mission, and the launch only while the mission is still Assigned.
            // A failure past this point must not roll back: the agent is running and its exit handler owns the outcome.
            try
            {
                if (await _Database.Captains.TrySetProcessAsync(captain.Id, mission.Id, processId, CancellationToken.None).ConfigureAwait(false))
                {
                    captain.ProcessId = processId;
                }

                mission.ProcessId = processId;
                mission.Status = MissionStatusEnum.InProgress;
                mission.StartedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                bool launchRecorded = await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { MissionStatusEnum.Assigned }, CancellationToken.None).ConfigureAwait(false);
                Mission? currentMission = launchRecorded ? null : await _Database.Missions.ReadAsync(mission.Id, CancellationToken.None).ConfigureAwait(false);
                if (currentMission != null)
                {
                    _Logging.Info(_Header + "mission " + mission.Id + " already moved to " + currentMission.Status + " before its launch was recorded (agent process " + processId + " finished first); keeping that state");
                    mission.ProcessId = currentMission.ProcessId;
                    mission.Status = currentMission.Status;
                    mission.StartedUtc = currentMission.StartedUtc;
                    mission.LastUpdateUtc = currentMission.LastUpdateUtc;
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "agent process " + processId + " for mission " + mission.Id + " launched but its launch could not be recorded: " + ex.Message
                    + " (the process exit handler and health checks own the outcome)");
            }

            _LaunchFailures.TryRemove(mission.Id, out MissionLaunchFailure? _);
            _Logging.Info(_Header + "launched agent process " + processId + " for captain " + captain.Id);
            _Logging.Info(_Header + "assigned mission " + mission.Id + " to captain " + captain.Id + " at " + dock.WorktreePath);
            return true;
            }
            finally
            {
                _InFlightAssignments.TryRemove(mission.Id, out _);
            }
        }

        /// <inheritdoc />
        public async Task HandleCompletionAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (String.IsNullOrEmpty(captain.CurrentMissionId)) return;

            await HandleCompletionAsync(captain, captain.CurrentMissionId, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task HandleCompletionAsync(Captain captain, string missionId, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (String.IsNullOrEmpty(missionId)) return;

            // Deduplication is not keyed by mission id here: a mission-id gate (held past the handler) also swallowed
            // the completion of a later attempt of the same mission. Each process exit is claimed once by its caller
            // (IMissionMethods.TryClaimProcessExitAsync), and the core's first write is a compare-and-set on the status
            // it read, so a duplicate for the same attempt loses that write and stops.
            _CompletionsInFlight.AddOrUpdate(missionId, 1, (_, count) => count + 1);
            try
            {
                await HandleCompletionCoreAsync(captain, missionId, token).ConfigureAwait(false);
            }
            finally
            {
                if (_CompletionsInFlight.AddOrUpdate(missionId, 0, (_, count) => count - 1) <= 0)
                    _CompletionsInFlight.TryRemove(new KeyValuePair<string, int>(missionId, 0));
            }
        }

        /// <inheritdoc />
        public bool IsCompletionInFlight(string missionId)
        {
            if (String.IsNullOrEmpty(missionId)) return false;
            return _CompletionsInFlight.TryGetValue(missionId, out int count) && count > 0;
        }

        /// <inheritdoc />
        public async Task<Mission> ApproveReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, bool conditional = false, CancellationToken token = default)
        {
            await _ReviewDecisionGate.WaitAsync(token).ConfigureAwait(false);
            try { return await ApproveReviewCoreAsync(missionId, reviewedByUserId, comment, conditional, token).ConfigureAwait(false); }
            finally { _ReviewDecisionGate.Release(); }
        }

        private async Task<Mission> ApproveReviewCoreAsync(string missionId, string? reviewedByUserId, string? comment = null, bool conditional = false, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            Mission mission = await RequireReviewMissionAsync(missionId, token).ConfigureAwait(false);
            if (reviewedByUserId != null && mission.ReviewDeadlineUtc <= DateTime.UtcNow && await ReadApprovalOptionsAsync(mission, token).ConfigureAwait(false) != null)
                throw new InvalidOperationException("The approval timer has expired; its configured default answer applies.");
            bool hasDependentPipelineStages = await HasDependentPipelineStages(mission.VoyageId, mission.Id, token).ConfigureAwait(false);

            mission.ReviewComment = NormalizeReviewComment(comment);
            mission.ReviewedByUserId = reviewedByUserId;
            mission.ReviewedUtc = DateTime.UtcNow;

            if (hasDependentPipelineStages)
            {
                mission.Status = MissionStatusEnum.Complete;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                await TryHandoffToNextStageAsync(mission, token).ConfigureAwait(false);

                // A handoff can fail the approved stage (for example an Architect whose plan cannot be parsed). Then
                // the following stages can never start: cancel them and settle the voyage instead of leaving them
                // Pending forever.
                Mission? afterHandoff = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                if (afterHandoff != null && afterHandoff.Status == MissionStatusEnum.Failed)
                {
                    await CancelDependentPipelineStagesAsync(afterHandoff, token).ConfigureAwait(false);
                    await UpdateVoyageTerminalStatusAsync(afterHandoff.VoyageId, token).ConfigureAwait(false);
                    return afterHandoff;
                }

                // Conditionally Approve: fold the reviewer's guidance into the freshly-prepared next stage(s)
                // before they dispatch, so the next captain is required to take that feedback into account.
                if (conditional && !String.IsNullOrWhiteSpace(mission.ReviewComment))
                {
                    await ApplyConditionalFeedbackToNextStagesAsync(mission, mission.ReviewComment!, token).ConfigureAwait(false);
                }

                await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
                await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);

                Mission? refreshed = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                return refreshed ?? mission;
            }

            Dock? dock = await ReadMissionDockAsync(mission, token).ConfigureAwait(false);
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            if (dock == null)
            {
                mission.Status = MissionStatusEnum.LandingFailed;
                mission.FailureReason = "Review approved but the mission dock was unavailable for landing.";
                mission.FailureKind = MissionFailureKindEnum.LandingConflict;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
                return mission;
            }

            if (OnMissionComplete != null)
            {
                await OnMissionComplete.Invoke(mission, dock).ConfigureAwait(false);
            }

            await ReclaimMissionDockAsync(dock.Id, token).ConfigureAwait(false);
            await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);

            Mission? landed = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            return landed ?? mission;
        }

        /// <inheritdoc />
        public async Task<Mission> RestartAsync(Mission mission, string? title = null, string? description = null, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (mission.Status != MissionStatusEnum.Failed && mission.Status != MissionStatusEnum.Cancelled && mission.Status != MissionStatusEnum.LandingFailed)
                throw new InvalidOperationException("Only Failed, LandingFailed, or Cancelled missions can be restarted (current: " + mission.Status + ").");

            if (!String.IsNullOrEmpty(title)) mission.Title = title!;
            if (!String.IsNullOrEmpty(description)) mission.Description = description!;

            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge))
            {
                JudgeVerdictEnum previousVerdict = JudgeVerdictParser.Parse(mission.AgentOutput);
                if ((previousVerdict == JudgeVerdictEnum.Fail || previousVerdict == JudgeVerdictEnum.NeedsRevision)
                    && !String.IsNullOrWhiteSpace(mission.AgentOutput))
                {
                    string priorFindings = mission.AgentOutput.Trim();
                    const int maxFindings = 12000;
                    if (priorFindings.Length > maxFindings)
                        priorFindings = priorFindings.Substring(0, maxFindings / 2)
                            + "\n(previous review excerpt truncated)\n"
                            + priorFindings.Substring(priorFindings.Length - maxFindings / 2);
                    if (!(mission.Description ?? String.Empty).Contains(priorFindings, StringComparison.Ordinal))
                        mission.Description = (mission.Description ?? String.Empty)
                            + "\n\n## Previous Judge findings (historical; recheck on restart)\n"
                            + "Re-evaluate every finding below against the current branch and current requirements. "
                            + "Report which are resolved, superseded, or still reproducible, and check the full mission scope. "
                            + "This is historical evidence, not the current verdict or a reason to skip independent validation.\n\n"
                            + priorFindings;
                }
            }

            // A later pipeline stage builds on the earlier stages' commits: keep their branch. A first stage starts
            // over on a fresh branch.
            string? keptBranch = null;
            if (!String.IsNullOrEmpty(mission.DependsOnMissionId))
            {
                Mission? dependency = await _Database.Missions.ReadAsync(mission.DependsOnMissionId!, token).ConfigureAwait(false);
                keptBranch = dependency?.BranchName ?? mission.BranchName;
            }

            ResetForRestart(mission, keptBranch);
            mission = await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(mission.VoyageId))
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(mission.VoyageId!, token).ConfigureAwait(false);
                if (voyage != null && (voyage.Status == VoyageStatusEnum.Failed || voyage.Status == VoyageStatusEnum.Cancelled || voyage.Status == VoyageStatusEnum.Complete))
                {
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage.CompletedUtc = null;
                    voyage.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "reopened voyage " + voyage.Id + " for restarted mission " + mission.Id);
                }

                // Restore the stages cancelled because this one failed (the chain hanging off it).
                List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId!, token).ConfigureAwait(false);
                HashSet<string> chain = new HashSet<string>(StringComparer.Ordinal) { mission.Id };
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (Mission candidate in voyageMissions)
                    {
                        if (chain.Contains(candidate.Id) || String.IsNullOrEmpty(candidate.DependsOnMissionId) || !chain.Contains(candidate.DependsOnMissionId!)) continue;
                        if (candidate.Status != MissionStatusEnum.Cancelled || candidate.FailureKind != MissionFailureKindEnum.DependencyFailed) continue;
                        chain.Add(candidate.Id);
                        ResetForRestart(candidate, null);
                        await _Database.Missions.UpdateAsync(candidate, token).ConfigureAwait(false);
                        grew = true;
                    }
                }
            }

            _Logging.Info(_Header + "restarted mission " + mission.Id + (keptBranch != null ? " on branch " + keptBranch : ""));
            return mission;
        }

        private static void ResetForRestart(Mission mission, string? branchName)
        {
            mission.Status = MissionStatusEnum.Pending;
            mission.CaptainId = null;
            mission.BranchName = branchName;
            mission.PrUrl = null;
            mission.CommitHash = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.DiffSnapshot = null;
            mission.FailureReason = null;
            mission.FailureKind = null;
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.ReviewComment = null;
            mission.ReviewedByUserId = null;
            mission.ReviewedUtc = null;
            mission.ReviewRequestedUtc = null;
            mission.ReviewDeadlineUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
        }

        /// <inheritdoc />
        public async Task<Mission> DenyReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, ReviewDenyActionEnum? actionOverride = null, CancellationToken token = default)
        {
            await _ReviewDecisionGate.WaitAsync(token).ConfigureAwait(false);
            try { return await DenyReviewCoreAsync(missionId, reviewedByUserId, comment, actionOverride, token).ConfigureAwait(false); }
            finally { _ReviewDecisionGate.Release(); }
        }

        private async Task<Mission> DenyReviewCoreAsync(string missionId, string? reviewedByUserId, string? comment = null, ReviewDenyActionEnum? actionOverride = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            Mission mission = await RequireReviewMissionAsync(missionId, token).ConfigureAwait(false);
            if (reviewedByUserId != null && mission.ReviewDeadlineUtc <= DateTime.UtcNow && await ReadApprovalOptionsAsync(mission, token).ConfigureAwait(false) != null)
                throw new InvalidOperationException("The approval timer has expired; its configured default answer applies.");
            Dock? dock = await ReadMissionDockAsync(mission, token).ConfigureAwait(false);
            string reviewComment = NormalizeReviewComment(comment) ?? "Review denied. Rework is required before this stage can continue.";

            // "More Work Required" and "Deny" surface as an explicit action from the reviewer; fall back to the
            // mission's configured deny action when the caller does not specify one.
            ReviewDenyActionEnum effectiveAction = actionOverride ?? mission.ReviewDenyAction;

            mission.ReviewComment = reviewComment;
            mission.ReviewedByUserId = reviewedByUserId;
            mission.ReviewedUtc = DateTime.UtcNow;

            if (dock != null)
            {
                await ReclaimMissionDockAsync(dock.Id, token).ConfigureAwait(false);
            }

            if (effectiveAction == ReviewDenyActionEnum.FailPipeline)
            {
                mission.Status = MissionStatusEnum.Failed;
                mission.FailureReason = BuildReviewDeniedFailureReason(reviewComment);
                mission.FailureKind = MissionFailureKindEnum.ReviewDenied;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.ProcessId = null;
                mission.DockId = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
                await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
                return mission;
            }

            mission.Status = MissionStatusEnum.Pending;
            mission.Description = ApplyReviewFeedback(mission.Description, reviewComment);
            mission.CaptainId = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.PrUrl = null;
            mission.CommitHash = null;
            mission.DiffSnapshot = null;
            mission.AgentOutput = null;
            mission.FailureReason = null;
            mission.FailureKind = null;
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.TotalRuntimeMs = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(mission.VesselId))
            {
                Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
                if (vessel != null)
                {
                    await TryAssignAsync(mission, vessel, token).ConfigureAwait(false);
                    Mission? refreshed = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                    if (refreshed != null) return refreshed;
                }
            }

            return mission;
        }

        /// <summary>
        /// Core completion logic, called under in-flight deduplication guard.
        /// </summary>
        /// <summary>
        /// If a completion looks like a no-op false-complete, re-dispatch the mission to a fresh captain
        /// (bounded by <see cref="ArmadaSettings.MaxNoOpRedispatchAttempts"/>) or, once exhausted, fail it
        /// so it surfaces in the operator inbox. Returns true when the completion was handled as a no-op
        /// (the caller must not proceed to landing).
        /// </summary>
        /// <summary>
        /// Run the pre-land dock-boundary scanner for the mission's vessel. On a finding, fail the mission
        /// with a redaction-safe reason (which surfaces it in the operator inbox) and return true so the
        /// caller does not land. Returns false when the vessel has no boundary rules or the dock is clean.
        /// </summary>
        private async Task<bool> TryFailMissionForBoundaryViolationAsync(Mission mission, Dock? dock, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.VesselId)) return false;
            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) return false;

            DockBoundaryPolicy policy = new DockBoundaryPolicy
            {
                SecretScanEnabled = vessel.SecretScanEnabled,
                ProtectedPathGlobs = vessel.ProtectedPathPatterns ?? new List<string>(),
                PrivateIdentifiers = vessel.PrivateIdentifierDenylist ?? new List<string>(),
            };
            if (!policy.HasAnyRule()) return false;

            DiffChangeSummary changes = await ResolveDiffChangeSummaryAsync(mission, vessel, dock, token).ConfigureAwait(false);
            List<BoundaryFinding> findings = DockBoundaryScanner.Scan(mission.DiffSnapshot, changes.ChangedPaths, policy);
            if (findings.Count == 0) return false;

            if (dock != null)
            {
                try { await ReclaimMissionDockAsync(dock.Id, token).ConfigureAwait(false); }
                catch (Exception ex) { _Logging.Warn(_Header + "boundary reclaim error for mission " + mission.Id + ": " + ex.ToString()); }
            }

            mission.Status = MissionStatusEnum.Failed;
            mission.FailureReason = DockBoundaryScanner.Summarize(findings);
            mission.FailureKind = MissionFailureKindEnum.Boundary;
            mission.CaptainId = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + mission.Id + " blocked by dock-boundary scanner (" + findings.Count + " finding(s))");
            await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Size and path facts for a mission's change. Parses the persisted diff snapshot by its hunk ranges and,
        /// when the dock's worktree is still on disk, combines it with git's --name-status / --numstat output for
        /// the branch (union of paths, larger counts), so a missing or partial snapshot cannot hide a change.
        /// </summary>
        private async Task<DiffChangeSummary> ResolveDiffChangeSummaryAsync(Mission mission, Vessel vessel, Dock? dock, CancellationToken token)
        {
            DiffChangeSummary summary = DiffChangeSummary.FromUnifiedDiff(mission.DiffSnapshot);
            DockHost? harborHost = HarborHostFor(dock);
            IGitService? git = harborHost != null ? harborHost.Git : _Git;
            if (git == null || dock == null || String.IsNullOrEmpty(dock.WorktreePath) || !await DockDirectoryExistsAsync(harborHost, dock.WorktreePath!, token).ConfigureAwait(false))
                return summary;

            string baseBranch = String.IsNullOrEmpty(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch;
            try
            {
                IReadOnlyList<GitChangedFile> gitChanges = await git.GetBranchChangesAsync(dock.WorktreePath!, baseBranch, token).ConfigureAwait(false);
                return DiffChangeSummary.Combine(summary, DiffChangeSummary.FromGitChanges(gitChanges));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException)
            {
                _Logging.Warn(_Header + "could not list branch changes for mission " + mission.Id + " in " + dock.WorktreePath + ": " + ex.Message);
                return summary;
            }
        }

        /// <summary>
        /// Evaluate the vessel's auto-land predicate against a mission that would otherwise land. When the
        /// change violates the vessel's file/line/path rules, route the mission to Review and return true so
        /// the caller holds instead of landing. Returns false when auto-land is disabled or the change is
        /// within the rules.
        /// </summary>
        /// <summary>
        /// Run the vessel's in-dock Definition-of-Done gate for a mission about to land. On a classified
        /// failure (Compile/TestFail/Timeout/Infra), fail the mission with the classified reason, cancel
        /// dependent pipeline stages, and return true so the caller does not land. Returns false when the
        /// gate is disabled/unconfigured or the change passed.
        /// </summary>
        private async Task<bool> TryFailForDefinitionOfDoneAsync(Mission mission, Dock? dock, CancellationToken token)
        {
            if (_DefinitionOfDone == null) return false;
            if (String.IsNullOrEmpty(mission.VesselId)) return false;
            if (dock == null || String.IsNullOrEmpty(dock.WorktreePath)) return false;

            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null || !vessel.DefinitionOfDoneEnabled) return false;

            DockHost? harborHost = HarborHostFor(dock);
            DefinitionOfDoneResult result = harborHost != null
                ? await _DefinitionOfDone.EvaluateOnHostAsync(vessel, dock.WorktreePath!, harborHost.Commands, token).ConfigureAwait(false)
                : await _DefinitionOfDone.EvaluateAsync(vessel, dock.WorktreePath!, token).ConfigureAwait(false);
            if (result.Passed) return false;

            mission.Status = MissionStatusEnum.Failed;
            mission.FailureReason = "Definition-of-Done gate failed (" + result.Outcome + "): " + result.Detail;
            mission.FailureKind = MissionFailureClassifier.FromDefinitionOfDone(result.Outcome);
            mission.CompletedUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + mission.Id + " failed the Definition-of-Done gate (" + result.Outcome + ")");

            await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
            return true;
        }

        /// <inheritdoc />
        public async Task<AutoLandDecision?> EvaluateAutoLandAsync(string missionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null || String.IsNullOrEmpty(mission.VesselId)) return null;

            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
            if (vessel == null) return null;

            Dock? dock = null;
            if (!String.IsNullOrEmpty(mission.DockId))
                dock = !String.IsNullOrEmpty(mission.TenantId)
                    ? await _Database.Docks.ReadAsync(mission.TenantId, mission.DockId!, token).ConfigureAwait(false)
                    : await _Database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);

            DiffChangeSummary changes = await ResolveDiffChangeSummaryAsync(mission, vessel, dock, token).ConfigureAwait(false);
            AutoLandPolicy policy = new AutoLandPolicy
            {
                Enabled = vessel.AutoLandEnabled,
                MaxFiles = vessel.AutoLandMaxFiles,
                MaxLines = vessel.AutoLandMaxLines,
                PathAllowGlobs = vessel.AutoLandPathAllowGlobs ?? new List<string>(),
                PathDenyGlobs = vessel.AutoLandPathDenyGlobs ?? new List<string>(),
            };

            return AutoLandPredicate.Evaluate(changes.FileCount, changes.ChangedLineCount, changes.ChangedPaths, policy);
        }

        private async Task<RunApprovalOptions?> ReadApprovalOptionsAsync(Mission mission, CancellationToken token)
        {
            Voyage? voyage = String.IsNullOrEmpty(mission.VoyageId) ? null : await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
            return voyage?.ApprovalOptions;
        }

        private async Task<DateTime?> ReviewDeadlineAsync(Mission mission, CancellationToken token)
        {
            RunApprovalOptions? options = await ReadApprovalOptionsAsync(mission, token).ConfigureAwait(false);
            return options != null ? DateTime.UtcNow.AddSeconds(options.TimeoutSeconds)
                : _Settings.ReviewTimeoutMinutes > 0 ? DateTime.UtcNow.AddMinutes(_Settings.ReviewTimeoutMinutes) : (DateTime?)null;
        }

        private async Task<bool> TryHoldForAutoLandAsync(Mission mission, Dock? dock, CancellationToken token)
        {
            RunApprovalOptions? holdApproval = await ReadApprovalOptionsAsync(mission, token).ConfigureAwait(false);
            if (holdApproval?.ApproveAll == true) return false;
            if (String.IsNullOrEmpty(mission.VesselId)) return false;
            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null || !vessel.AutoLandEnabled) return false;

            DiffChangeSummary changes = await ResolveDiffChangeSummaryAsync(mission, vessel, dock, token).ConfigureAwait(false);
            List<string> changedPaths = changes.ChangedPaths;
            int filesChanged = changes.FileCount;
            int linesChanged = changes.ChangedLineCount;

            AutoLandPolicy policy = new AutoLandPolicy
            {
                Enabled = true,
                MaxFiles = vessel.AutoLandMaxFiles,
                MaxLines = vessel.AutoLandMaxLines,
                PathAllowGlobs = vessel.AutoLandPathAllowGlobs ?? new List<string>(),
                PathDenyGlobs = vessel.AutoLandPathDenyGlobs ?? new List<string>(),
            };

            AutoLandDecision decision = AutoLandPredicate.Evaluate(filesChanged, linesChanged, changedPaths, policy);
            if (decision.Land) return false;

            mission.Status = MissionStatusEnum.Review;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.ReviewRequestedUtc = DateTime.UtcNow;
            mission.ReviewDeadlineUtc = await ReviewDeadlineAsync(mission, token).ConfigureAwait(false);
            if (holdApproval != null) mission.RequiresReview = true;
            mission.ReviewComment = "Auto-land held: " + (decision.HoldReason ?? "change exceeds auto-land rules");
            mission.ReviewedByUserId = null;
            mission.ReviewedUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            OnReviewRequested?.Invoke(mission);
            _Logging.Info(_Header + "auto-land held mission " + mission.Id + " for review: " + (decision.HoldReason ?? "rules"));
            return true;
        }

        private async Task<bool> TryRejectNoOpCompletionAsync(Mission mission, Dock? dock, CancellationToken token)
        {
            if (!NoOpCompletionDetector.IsNoOp(mission.DiffSnapshot, mission.AgentOutput, mission.TotalRuntimeMs))
                return false;

            // Release the dock/captain before re-dispatching or failing.
            if (dock != null)
            {
                try { await ReclaimMissionDockAsync(dock.Id, token).ConfigureAwait(false); }
                catch (Exception ex) { _Logging.Warn(_Header + "no-op reclaim error for mission " + mission.Id + ": " + ex.ToString()); }
            }

            int maxAttempts = _Settings.MaxNoOpRedispatchAttempts;
            if (mission.RedispatchAttempts < maxAttempts)
            {
                mission.RedispatchAttempts++;
                mission.Status = MissionStatusEnum.Pending;
                mission.CaptainId = null;
                mission.DockId = null;
                mission.ProcessId = null;
                mission.PrUrl = null;
                mission.CommitHash = null;
                mission.DiffSnapshot = null;
                mission.AgentOutput = null;
                mission.FailureReason = null;
                mission.FailureKind = null;
                mission.StartedUtc = null;
                mission.CompletedUtc = null;
                mission.TotalRuntimeMs = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "no-op completion detected for mission " + mission.Id +
                    "; re-dispatching to a fresh captain (attempt " + mission.RedispatchAttempts + "/" + maxAttempts + ")");

                if (!String.IsNullOrEmpty(mission.VesselId))
                {
                    Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
                    if (vessel != null) await TryAssignAsync(mission, vessel, token).ConfigureAwait(false);
                }
                return true;
            }

            // Retries exhausted: fail and surface to the operator inbox (LandingFailed/Failed feed it).
            mission.Status = MissionStatusEnum.Failed;
            mission.FailureReason = "No-op completion: the captain repeatedly completed with no changes after " +
                mission.RedispatchAttempts + " re-dispatch attempt(s). Likely an impossible or mis-specified mission.";
            mission.FailureKind = MissionFailureKindEnum.NoOp;
            mission.CaptainId = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "no-op completion for mission " + mission.Id +
                " exhausted re-dispatch attempts; failing to operator inbox");
            await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
            return true;
        }

        private async Task HandleCompletionCoreAsync(Captain captain, string missionId, CancellationToken token)
        {
            Mission? mission = null;
            if (!String.IsNullOrEmpty(captain.TenantId))
            {
                mission = await _Database.Missions.ReadAsync(captain.TenantId, missionId, token).ConfigureAwait(false);
            }
            if (mission == null)
            {
                mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            }
            if (mission == null) return;

            // Idempotency guard: if the mission has already been processed (e.g. by a concurrent
            // health check or process exit handler), skip to avoid double-processing.
            if (mission.Status == MissionStatusEnum.WorkProduced ||
                mission.Status == MissionStatusEnum.Complete ||
                mission.Status == MissionStatusEnum.Failed ||
                mission.Status == MissionStatusEnum.Cancelled ||
                mission.Status == MissionStatusEnum.LandingFailed ||
                mission.Status == MissionStatusEnum.PullRequestOpen)
            {
                _Logging.Debug(_Header + "mission " + missionId + " already in post-work state " + mission.Status + " -- skipping completion handler");
                return;
            }

            RunApprovalOptions? completionApproval = await ReadApprovalOptionsAsync(mission, token).ConfigureAwait(false);
            if (completionApproval?.ApproveAll == true) mission.RequiresReview = false;

            // Read-only modes (Audit/Research) produce a written report, not a commit. Their empty diff is a
            // successful outcome, so they skip no-op rejection and never attempt landing.
            bool isReadOnlyMission = MissionModeContract.IsReadOnly(mission.Mode);

            // Mark mission as work produced (agent finished, landing not yet attempted). Conditional on the status read
            // above, so exactly one completion wins and a concurrent handler that moved the mission (an interruption
            // re-dispatch, a cancel, a second completion) is never overwritten by this one.
            MissionStatusEnum observedStatus = mission.Status;
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.ProcessId = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            if (!await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { observedStatus }, token).ConfigureAwait(false))
            {
                _Logging.Debug(_Header + "mission " + missionId + " left " + observedStatus + " before its completion was recorded -- skipping completion handler");
                return;
            }

            _Logging.Info(_Header + "mission " + mission.Id + " work produced by captain " + captain.Id);

            // Get dock for diff capture (prefer mission-level DockId, fall back to captain-level)
            Dock? dock = null;
            string? dockId = mission.DockId ?? captain.CurrentDockId;
            if (!String.IsNullOrEmpty(dockId))
            {
                dock = !String.IsNullOrEmpty(mission.TenantId)
                    ? await _Database.Docks.ReadAsync(mission.TenantId, dockId, token).ConfigureAwait(false)
                    : await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
            }

            if (dock != null && String.IsNullOrEmpty(mission.BranchName) && !String.IsNullOrEmpty(dock.BranchName))
            {
                mission.BranchName = dock.BranchName;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Debug(_Header + "backfilled branch " + dock.BranchName + " onto mission " + mission.Id +
                    " from dock " + dock.Id + " before pipeline handoff");
            }

            // A captain that edited files but never ran git commit (common for smaller local models) would otherwise
            // produce an empty diff, and the edits would be deleted with the dock. Commit them first.
            if (dock != null) await CommitLeftoverWorkAsync(mission, captain, dock, token).ConfigureAwait(false);

            // Record the exact commit this stage produced. The next stage verifies its dock starts from it, and rework
            // stagnation detection compares it across rounds. Landing later replaces it with the landed commit.
            if (dock != null)
            {
                string? producedCommit = await ReadDockHeadAsync(dock, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(producedCommit) && !String.Equals(producedCommit, mission.CommitHash, StringComparison.Ordinal))
                {
                    mission.CommitHash = producedCommit;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                }
            }

            // Capture diff BEFORE pipeline handoff so the next stage gets the actual diff
            if (dock != null && OnCaptureDiff != null)
            {
                try
                {
                    await OnCaptureDiff.Invoke(mission, dock).ConfigureAwait(false);
                    // Re-read mission to get the persisted DiffSnapshot
                    Mission? refreshed = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                    if (refreshed != null && !String.IsNullOrEmpty(refreshed.DiffSnapshot))
                    {
                        mission.DiffSnapshot = refreshed.DiffSnapshot;
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error capturing diff for mission " + mission.Id + ": " + ex.ToString());
                }
            }

            bool failedForScopeViolation = false;
            if (dock != null)
            {
                failedForScopeViolation = await TryFailMissionForScopeViolationAsync(mission, dock, token).ConfigureAwait(false);
            }

            // Capture accumulated agent stdout output before pipeline handoff
            if (OnGetMissionOutput != null)
            {
                string? agentOutput = OnGetMissionOutput(mission.Id);
                ChatTurnTelemetryRecorder? reported = OnTakeMissionTelemetry?.Invoke(mission.Id);
                if (!String.IsNullOrEmpty(agentOutput))
                {
                    mission.AgentOutput = agentOutput;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                    // The runtime's own usage report (mux contract 2 run_completed, a Claude Code / Codex / OpenCode
                    // usage event), read from its raw output as it arrived; without one (an older lifecycle handler),
                    // from the collected output; otherwise both counts are estimated from text length and flagged.
                    if (reported == null)
                    {
                        reported = new ChatTurnTelemetryRecorder();
                        foreach (string line in agentOutput.Split('\n'))
                            reported.ObserveLine(captain.Runtime, line.TrimEnd('\r'));
                    }

                    await TokenUsageCapture.CaptureAsync(
                        _Database, _Logging, "mission",
                        model: reported.Model ?? captain.Model,
                        runtime: captain.Runtime.ToString(),
                        tenantId: mission.TenantId,
                        userId: null,
                        vesselId: String.IsNullOrEmpty(mission.VesselId) ? null : mission.VesselId,
                        captainId: captain.Id,
                        sourceId: mission.Id,
                        inputTokens: reported.InputTokens,
                        outputTokens: reported.OutputTokens,
                        cachedTokens: reported.CachedTokens,
                        inputText: mission.Description,
                        outputText: agentOutput,
                        token: token,
                        harborId: mission.AssignedHarborId).ConfigureAwait(false);
                }
            }

            // Reject a no-op false-complete (fast exit, empty diff, trivial output): re-dispatch to a
            // fresh captain up to a bounded number of times, then fail to the operator inbox. Skipped
            // when the mission already failed scope validation.
            if (!failedForScopeViolation && !isReadOnlyMission &&
                await TryRejectNoOpCompletionAsync(mission, dock, token).ConfigureAwait(false))
            {
                await ReleaseCaptainAfterCompletionAsync(captain, mission.Id).ConfigureAwait(false);
                await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
                return;
            }

            // Pre-land dock-boundary scan: secrets, protected paths, private-identifier leaks. A finding
            // blocks landing and surfaces the mission (with a redaction-safe reason) in the operator inbox.
            if (!failedForScopeViolation &&
                await TryFailMissionForBoundaryViolationAsync(mission, dock, token).ConfigureAwait(false))
            {
                await ReleaseCaptainAfterCompletionAsync(captain, mission.Id).ConfigureAwait(false);
                await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
                return;
            }

            string? judgeBlockReason = null;
            if (!failedForScopeViolation && String.Equals(mission.Persona, "Judge", StringComparison.OrdinalIgnoreCase))
            {
                // Only the structured [ARMADA:VERDICT] protocol line counts; a missing or conflicting verdict is no
                // verdict, which blocks landing.
                JudgeVerdictEnum verdict = JudgeVerdictParser.Parse(mission.AgentOutput);
                string? verdictFailureReason = null;
                bool rejectedPass = false;

                // A PASS must be substantiated (three lenses + real narrative). A rejected PASS is not
                // silently re-run: it is downgraded to a blocking verdict and the mission fails terminally
                // with an explicit reason so an operator sees it rather than a quiet re-dispatch.
                if (verdict == JudgeVerdictEnum.Pass && !TryValidateJudgePassOutput(mission.AgentOutput, out verdictFailureReason))
                {
                    verdict = JudgeVerdictEnum.NeedsRevision;
                    rejectedPass = true;
                }

                // A specification conflict is not a defect a Worker can fix, so it never consumes the rework budget. The
                // stage goes to a person, who approves the work as is, restarts the Judge, or stops the pipeline; nothing is
                // waived automatically. A SPEC_CONFLICT without the quoted criterion is a contract violation (repaired below).
                if (verdict == JudgeVerdictEnum.SpecConflict)
                {
                    string? conflict = JudgeContract.ExtractSpecConflict(mission.AgentOutput);
                    if (conflict != null)
                    {
                        mission.RequiresReview = true;
                        judgeBlockReason = "Judge reported a specification conflict (a person must decide; no criterion was waived): " + conflict;
                        _Logging.Warn(_Header + "judge mission " + mission.Id + " reported a specification conflict; sending it to review");
                    }
                    else
                    {
                        verdict = JudgeVerdictEnum.None;
                    }
                }

                if (verdict != JudgeVerdictEnum.Pass && verdict != JudgeVerdictEnum.SpecConflict)
                {
                    // To block, the Judge must exhibit a concrete affected case. A block without one is a
                    // contract violation: the mission still fails terminally, but the reason makes clear the
                    // Judge did not substantiate the block rather than the work being definitively wrong.
                    bool isBlockingVerdict = verdict == JudgeVerdictEnum.Fail || verdict == JudgeVerdictEnum.NeedsRevision;
                    bool exhibitsAffectedCase = JudgeContract.ExhibitsAffectedCase(mission.AgentOutput);

                    string blockingReason;
                    if (rejectedPass)
                    {
                        blockingReason = "Judge PASS rejected (" + (verdictFailureReason ?? "unsubstantiated approval") + ")";
                    }
                    else if (isBlockingVerdict && !exhibitsAffectedCase)
                    {
                        blockingReason = "Judge verdict: " + (verdict == JudgeVerdictEnum.Fail ? "FAIL" : "NEEDS_REVISION") +
                            " but did not exhibit a concrete affected case; a block must cite a real affected file, line, or scenario";
                    }
                    else
                    {
                        blockingReason = verdict switch
                        {
                            JudgeVerdictEnum.Fail => "Judge verdict: FAIL",
                            JudgeVerdictEnum.NeedsRevision => "Judge verdict: NEEDS_REVISION",
                            _ => "Judge mission did not emit an explicit PASS verdict"
                        };
                    }

                    string? reworkUnavailableReason = null;
                    if (!mission.RequiresReview && !rejectedPass && isBlockingVerdict && exhibitsAffectedCase
                        && await TryScheduleJudgeReworkAsync(mission, token, reason => reworkUnavailableReason = reason).ConfigureAwait(false))
                    {
                        // The Judge asked for concrete changes: the work goes back to a Worker on the same branch with
                        // the review, then to a new Judge. The rest of the pipeline now waits on that Judge.
                        _Logging.Info(_Header + "judge mission " + mission.Id + " asked for revisions (" + blockingReason + "); sent back for rework");
                    }
                    else if (mission.RequiresReview)
                    {
                        // The stage has a review gate: a person decides. Approving overrides the Judge and lands the
                        // work; denying with FailPipeline stops the pipeline, RetryStage runs the Judge again.
                        judgeBlockReason = blockingReason;
                        _Logging.Warn(_Header + "judge mission " + mission.Id + " blocked landing, sent to review: " + blockingReason);
                    }
                    else if ((rejectedPass || !isBlockingVerdict || !exhibitsAffectedCase)
                        && await TryScheduleOutputRepairAsync(mission, blockingReason, JudgeContract.OutputContract(), token).ConfigureAwait(false))
                    {
                        // The Judge broke its output contract (no verdict, conflicting verdicts, an unsubstantiated
                        // PASS, or a block without an affected case). That is a formatting failure, not a review
                        // outcome: the same Judge runs again with the exact problem quoted before anything fails.
                        await FinishOutputRepairScheduledAsync(mission, captain, dock, token).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        if (rejectedPass || !isBlockingVerdict || !exhibitsAffectedCase)
                            blockingReason = AppendRepairHistory(blockingReason, mission);
                        mission.Status = MissionStatusEnum.Failed;
                        mission.CompletedUtc = DateTime.UtcNow;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        mission.FailureReason = blockingReason + (reworkUnavailableReason == null ? String.Empty
                            : "\n" + reworkUnavailableReason + "\nRemediation: Read this Judge's affected-case report and correct the identified code or configuration defects. Dispatch a follow-up mission using the preserved work branch and an independent Judge; keep strict selection unless you explicitly choose a different captain. Rejected work has not been approved for landing.");
                        mission.FailureKind = MissionFailureKindEnum.JudgeRejected;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        _Logging.Warn(_Header + "judge mission " + mission.Id + " blocked landing: " + blockingReason);
                    }
                }
            }

            bool hasDependentPipelineStages = await HasDependentPipelineStages(mission.VoyageId, mission.Id, token).ConfigureAwait(false);
            bool awaitingManualReview = false;
            if (!failedForScopeViolation &&
                mission.Status == MissionStatusEnum.WorkProduced &&
                mission.RequiresReview)
            {
                mission.Status = MissionStatusEnum.Review;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.ReviewRequestedUtc = DateTime.UtcNow;
                mission.ReviewDeadlineUtc = await ReviewDeadlineAsync(mission, token).ConfigureAwait(false);
                mission.ReviewComment = judgeBlockReason != null
                    ? "Judge blocked landing: " + judgeBlockReason + ". Approve to land the work anyway, or deny: FailPipeline stops the pipeline, RetryStage runs the Judge again."
                    : null;
                mission.ReviewedByUserId = null;
                mission.ReviewedUtc = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                OnReviewRequested?.Invoke(mission);
                awaitingManualReview = true;
            }

            // Pipeline handoff: if missions in the same voyage depend on this one, prepare them
            bool preparedDownstreamStages = false;
            if (!failedForScopeViolation && !awaitingManualReview)
            {
                preparedDownstreamStages = await TryHandoffToNextStageAsync(mission, token).ConfigureAwait(false);
            }

            Mission? missionAfterHandoff = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            if (missionAfterHandoff != null)
            {
                mission = missionAfterHandoff;
            }

            if (mission.Status == MissionStatusEnum.Failed ||
                mission.Status == MissionStatusEnum.Cancelled ||
                mission.Status == MissionStatusEnum.LandingFailed)
            {
                await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
                await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
            }

            await EmitMissionOutcomeTelemetryAsync(mission, captain, token).ConfigureAwait(false);

            bool shouldAttemptLanding =
                !awaitingManualReview &&
                !preparedDownstreamStages &&
                !hasDependentPipelineStages &&
                !isReadOnlyMission &&
                (mission.Status == MissionStatusEnum.WorkProduced ||
                mission.Status == MissionStatusEnum.PullRequestOpen);

            // Read-only missions (Audit/Research) that are not held for review or feeding a pipeline stage
            // complete directly: there is nothing to land, and an empty diff is the expected outcome.
            if (isReadOnlyMission &&
                !awaitingManualReview &&
                !preparedDownstreamStages &&
                !hasDependentPipelineStages &&
                mission.Status == MissionStatusEnum.WorkProduced)
            {
                mission.Status = MissionStatusEnum.Complete;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
                _Logging.Info(_Header + "read-only " + mission.Mode + " mission " + mission.Id +
                    " completed without landing (no commit expected)");
            }

            // In-dock Definition-of-Done gate: run the vessel's build + unit tests inside the mission's own
            // checkout before acceptance. A classified failure (Compile/TestFail/Timeout/Infra) blocks
            // landing and fails the mission so it surfaces (and can be recovered).
            if (shouldAttemptLanding && await TryFailForDefinitionOfDoneAsync(mission, dock, token).ConfigureAwait(false))
            {
                shouldAttemptLanding = false;
            }

            // Per-vessel auto-land predicate: a change that is too large or touches denied/out-of-scope
            // paths holds for review instead of landing unattended.
            if (shouldAttemptLanding && await TryHoldForAutoLandAsync(mission, dock, token).ConfigureAwait(false))
            {
                shouldAttemptLanding = false;
                awaitingManualReview = true;
            }

            if (!shouldAttemptLanding)
            {
                _Logging.Debug(_Header + "skipping landing for mission " + mission.Id +
                    " because it is not a terminal landed stage yet (status: " + mission.Status + ")");
            }

            // Invoke OnMissionComplete synchronously (Phase A: push branch, create PR, or enqueue).
            // Captain stays in Working state until the handoff completes, preventing the captain
            // from being reassigned while git operations are still in progress.
            if (shouldAttemptLanding && dock != null && OnMissionComplete != null)
            {
                _Logging.Debug(_Header + "executing synchronous landing handoff for mission " + mission.Id);
                try
                {
                    await OnMissionComplete.Invoke(mission, dock).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error in mission complete handler for " + mission.Id + ": " + ex.ToString());
                }
            }

            // Reclaim the dock after the handoff completes (or if no handler was set)
            string? completionDockId = dock?.Id;
            bool cleanupArchitectBranch =
                preparedDownstreamStages &&
                String.Equals(mission.Persona, "Architect", StringComparison.OrdinalIgnoreCase);
            bool retainDockForReview =
                awaitingManualReview &&
                !hasDependentPipelineStages &&
                !String.IsNullOrEmpty(completionDockId);

            if (!String.IsNullOrEmpty(completionDockId) && !retainDockForReview)
            {
                try
                {
                    await _Docks.ReclaimAsync(completionDockId, token: token).ConfigureAwait(false);
                }
                catch (Exception reclaimEx)
                {
                    _Logging.Warn(_Header + "error reclaiming dock " + completionDockId + " after mission " + mission.Id + ": " + reclaimEx.Message);
                }
            }

            if (cleanupArchitectBranch)
            {
                await CleanupArchitectBranchAsync(mission, dock, token).ConfigureAwait(false);
            }

            MissionOutcomeSignalDefinition missionOutcomeSignal = BuildMissionOutcomeSignal(mission);
            Signal signal = new Signal(missionOutcomeSignal.Type, missionOutcomeSignal.Payload);
            signal.FromCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            // Release the captain to idle only AFTER the handoff and dock reclaim are done,
            // and only if the captain is still assigned to this mission. Orphan recovery can
            // finalize an older mission using a captain record that has already moved on.
            await ReleaseCaptainAfterCompletionAsync(captain, mission.Id).ConfigureAwait(false);

            await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public bool IsBroadScope(Mission mission)
        {
            if (mission == null) return false;

            string text = ((mission.Title ?? "") + " " + (mission.Description ?? "")).ToLowerInvariant();

            string[] broadIndicators = new[]
            {
                "refactor entire",
                "refactor all",
                "rename across",
                "migrate project",
                "upgrade framework",
                "restructure",
                "rewrite",
                "overhaul",
                "global search and replace",
                "update all",
                "format all",
                "lint entire"
            };

            foreach (string indicator in broadIndicators)
            {
                if (text.Contains(indicator)) return true;
            }

            return false;
        }

        /// <inheritdoc />
        public async Task RegenerateInstructionsAsync(Mission mission, Captain captain, Dock dock, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (dock == null) throw new ArgumentNullException(nameof(dock));
            if (String.IsNullOrEmpty(dock.WorktreePath) || String.IsNullOrEmpty(mission.VesselId)) return;
            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
            if (vessel == null) return;
            DockHost? harborHost = HarborHostFor(dock);
            await GenerateClaudeMdOnHostAsync(harborHost, dock.WorktreePath!, mission, vessel, captain, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task GenerateClaudeMdAsync(string worktreePath, Mission mission, Vessel vessel, Captain? captain = null, CancellationToken token = default)
        {
            return GenerateClaudeMdOnHostAsync(null, worktreePath, mission, vessel, captain, token);
        }

        /// <summary>
        /// Generate the mission's instruction file into a dock: on this machine when <paramref name="harborHost"/> is null,
        /// else on that Harbor (file writes, git anchors, and exclude entries all go through the Harbor).
        /// </summary>
        private async Task GenerateClaudeMdOnHostAsync(DockHost? harborHost, string worktreePath, Mission mission, Vessel vessel, Captain? captain, CancellationToken token)
        {
            if (String.IsNullOrEmpty(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            string instructionsFileName = MissionPromptBuilder.GetInstructionsFileName(captain != null ? captain.Runtime.ToString() : null);
            string instructionsPath = Path.Combine(worktreePath, instructionsFileName);

            Mission? previousStage = await ReadPreviousStageAsync(mission, token).ConfigureAwait(false);
            Dictionary<string, string> templateParams = MissionPromptBuilder.BuildTemplateParams(mission, vessel, captain, null, previousStage);
            List<MissionPlaybookSnapshot> playbookSnapshots = await LoadMissionPlaybookSnapshotsAsync(mission, token).ConfigureAwait(false);

            string content = "";

            // BuildTemplateParams includes the mandatory persona contract even when the captain
            // has no custom system instructions. Deliver that contract to every generated brief.
            if (!String.IsNullOrWhiteSpace(templateParams["CaptainInstructions"]))
            {
                content += await ResolveSectionAsync("mission.captain_instructions_wrapper", templateParams, token).ConfigureAwait(false);
                content += "\n";
            }

            // Vessel context sections
            if (!String.IsNullOrEmpty(vessel.ProjectContext))
            {
                content += await ResolveSectionAsync("mission.project_context_wrapper", templateParams, token).ConfigureAwait(false);
                content += "\n";
            }

            if (!String.IsNullOrEmpty(vessel.StyleGuide))
            {
                content += await ResolveSectionAsync("mission.code_style_wrapper", templateParams, token).ConfigureAwait(false);
                content += "\n";
            }

            if (vessel.EnableModelContext && !String.IsNullOrEmpty(vessel.ModelContext))
            {
                content += await ResolveSectionAsync("mission.model_context_wrapper", templateParams, token).ConfigureAwait(false);
                content += "\n";
            }

            if (playbookSnapshots.Count > 0)
            {
                templateParams["SelectedPlaybooksMarkdown"] = await RenderSelectedPlaybooksMarkdownAsync(
                    harborHost,
                    worktreePath,
                    mission,
                    playbookSnapshots,
                    token).ConfigureAwait(false);
                content += await ResolveSectionAsync("mission.playbooks_wrapper", templateParams, token).ConfigureAwait(false);
                content += "\n";
            }

            // Inject the vessel's project-profile skills (best-effort) as a Skills section.
            string skillsMarkdown = await ResolveSkillsMarkdownAsync(vessel, token).ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(skillsMarkdown))
            {
                content += "## Skills\n\n" + skillsMarkdown + "\n";
            }

            // Mission preamble and metadata -- resolve persona prompt first, then inject into metadata template.
            // Apply the vessel's project-profile persona override (if any) so per-project customization takes effect.
            PersonaOverride? personaOverride = await ResolvePersonaOverrideAsync(vessel, mission.Persona, token).ConfigureAwait(false);
            string? personaTemplateName = await ResolvePersonaTemplateNameAsync(mission, token).ConfigureAwait(false);
            string personaPrompt = await MissionPromptBuilder.ResolvePersonaPromptAsync(mission.Persona, personaTemplateName, templateParams, _PromptTemplates, personaOverride, token).ConfigureAwait(false);
            templateParams["PersonaPrompt"] = personaPrompt;
            content += await ResolveSectionAsync("mission.metadata", templateParams, token).ConfigureAwait(false);
            content += "\n";

            // Mission-mode contract: read-only modes (Audit/Research) get a report-shaped brief that forbids
            // repository changes and states that an empty diff is a successful outcome. Write missions add nothing.
            string missionModeSection = MissionModeContract.BuildBriefSection(mission.Mode);
            if (!String.IsNullOrEmpty(missionModeSection))
            {
                content += missionModeSection;
                content += "\n";
            }

            // Resolved git anchors (start commit, target branch, working branch, recent commits on the paths
            // the mission names, and which subject terms already exist in the tree) so the captain does not
            // burn opening turns deriving them. Best-effort: a git failure degrades to a smaller section.
            string? headCommit = null;
            IReadOnlyList<string>? recentPathCommits = null;
            IReadOnlyList<string>? subjectTermsPresent = null;
            IGitService? anchorGit = harborHost != null ? harborHost.Git : _Git;
            if (anchorGit != null)
            {
                try { headCommit = await anchorGit.GetHeadCommitHashAsync(worktreePath, token).ConfigureAwait(false); }
                catch { headCommit = null; }

                string missionText = (mission.Title ?? "") + "\n" + (mission.Description ?? "");
                try
                {
                    IReadOnlyList<string> namedPaths = GitAnchorInputs.ExtractPaths(missionText);
                    if (namedPaths.Count > 0)
                        recentPathCommits = await anchorGit.GetRecentCommitsForPathsAsync(worktreePath, namedPaths, 3, token).ConfigureAwait(false);
                }
                catch { recentPathCommits = null; }

                try
                {
                    IReadOnlyList<string> terms = GitAnchorInputs.ExtractSubjectTerms(mission.Title, mission.Description);
                    if (terms.Count > 0)
                        subjectTermsPresent = await anchorGit.FindExistingSubjectTermsAsync(worktreePath, terms, token).ConfigureAwait(false);
                }
                catch { subjectTermsPresent = null; }
            }
            string gitAnchors = GitAnchorsFormatter.Render(headCommit, vessel.DefaultBranch, mission.BranchName, recentPathCommits, subjectTermsPresent);
            if (!String.IsNullOrEmpty(gitAnchors))
            {
                content += gitAnchors;
                content += "\n";

                // Persist the resolved anchors on the dock (documented raw-JSON snapshot) so the dashboard
                // and a resuming captain can read them without re-deriving. Best-effort.
                if (!String.IsNullOrEmpty(mission.DockId))
                {
                    try
                    {
                        GitAnchorsSnapshot snapshot = new GitAnchorsSnapshot
                        {
                            StartCommit = headCommit,
                            TargetBranch = vessel.DefaultBranch,
                            WorkingBranch = mission.BranchName,
                            RecentPathCommits = recentPathCommits != null ? new List<string>(recentPathCommits) : new List<string>(),
                            SubjectTermsPresent = subjectTermsPresent != null ? new List<string>(subjectTermsPresent) : new List<string>(),
                        };
                        Dock? anchorDock = await _Database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);
                        if (anchorDock != null)
                        {
                            anchorDock.GitAnchorsJson = System.Text.Json.JsonSerializer.Serialize(snapshot);
                            anchorDock.LastUpdateUtc = DateTime.UtcNow;
                            await _Database.Docks.UpdateAsync(anchorDock, token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _Logging.Debug(_Header + "could not persist git anchors on dock " + mission.DockId + ": " + ex.Message);
                    }
                }
            }

            // Rules, context conservation, merge conflicts, progress signals -- from templates or hardcoded fallback
            content += await ResolveSectionAsync("mission.rules", templateParams, token).ConfigureAwait(false);
            content += "\n";
            content += await ResolveSectionAsync("mission.context_conservation", templateParams, token).ConfigureAwait(false);
            content += "\n";
            content += await ResolveSectionAsync("mission.merge_conflict_avoidance", templateParams, token).ConfigureAwait(false);
            content += "\n";
            content += await ResolveSectionAsync("mission.progress_signals", templateParams, token).ConfigureAwait(false);

            // Papercuts apply to every mission mode: an audit meets stale docs and dead links exactly as
            // an implementation does. Judge and Architect are excluded -- a judge reports what it finds
            // through its verdict, and an architect emits mission blocks only.
            if (!PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge) &&
                !PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Architect))
            {
                content += "\n";
                content += BuildPapercutsSection();
            }

            // Model context updates
            if (vessel.EnableModelContext)
            {
                content += "\n";
                content += await ResolveSectionAsync("mission.model_context_updates", templateParams, token).ConfigureAwait(false);
            }

            // If there's an existing runtime instruction file, preserve it and prepend our instructions
            IDockFileSystem files = harborHost != null ? harborHost.Files : _LocalFiles;
            string? existing = await files.ReadTextAsync(instructionsPath, token).ConfigureAwait(false);

            // A runtime whose instructions file is not the repository's own (MUX.md, CODEX.md, GEMINI.md, CURSOR.md)
            // would otherwise never see the project's rules: mux loads MUX.md first and skips AGENTS.md and CLAUDE.md.
            // Carry the repository's AGENTS.md, else CLAUDE.md, into the generated file. Codex reads AGENTS.md itself.
            if (existing == null && !String.Equals(instructionsFileName, "CLAUDE.md", StringComparison.Ordinal))
            {
                bool runtimeReadsAgentsMd = captain != null && captain.Runtime == AgentRuntimeEnum.Codex;
                string[] fallbacks = runtimeReadsAgentsMd ? new[] { "CLAUDE.md" } : new[] { "AGENTS.md", "CLAUDE.md" };
                if (runtimeReadsAgentsMd && await files.ReadTextAsync(Path.Combine(worktreePath, "AGENTS.md"), token).ConfigureAwait(false) != null)
                    fallbacks = Array.Empty<string>();
                foreach (string fallback in fallbacks)
                {
                    string? projectInstructions = await files.ReadTextAsync(Path.Combine(worktreePath, fallback), token).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(projectInstructions))
                    {
                        existing = projectInstructions;
                        _Logging.Debug(_Header + "carrying the repository's " + fallback + " into " + instructionsFileName + " for mission " + mission.Id);
                        break;
                    }
                }
            }

            if (existing != null)
            {
                string sanitizedExisting = SanitizeExistingInstructions(existing);

                if (!String.IsNullOrWhiteSpace(sanitizedExisting))
                {
                    if (!String.Equals(existing, sanitizedExisting, StringComparison.Ordinal))
                    {
                        _Logging.Debug(_Header + "sanitized generated mission sections from existing instructions at " + instructionsPath);
                    }

                    templateParams["ExistingClaudeMd"] = sanitizedExisting;
                    content += await ResolveSectionAsync("mission.existing_instructions_wrapper", templateParams, token).ConfigureAwait(false);
                }
            }

            await files.WriteTextAsync(instructionsPath, content, token).ConfigureAwait(false);

            // Persist a stable copy outside the dock so the dashboard and APIs can still
            // show the generated mission instructions after the worktree is reclaimed.
            try
            {
                string instructionsSnapshotDir = Path.Combine(_Settings.LogDirectory, "instructions");
                Directory.CreateDirectory(instructionsSnapshotDir);
                string snapshotPath = Path.Combine(instructionsSnapshotDir, mission.Id + "." + instructionsFileName);
                await File.WriteAllTextAsync(snapshotPath, content).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not persist mission instructions snapshot for " + mission.Id + ": " + ex.ToString());
            }

            // Ensure the generated instruction file is ignored locally so agents don't commit it.
            // Mission instructions are ephemeral and should not alter tracked repository files.
            bool isGitCheckout = false;
            try
            {
                isGitCheckout = await files.AddGitExcludeEntryAsync(worktreePath, instructionsFileName, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _Logging.Warn(_Header + "could not update git exclude for " + instructionsFileName + ": " + ex.ToString());
            }

            try
            {
                if (isGitCheckout)
                {
                    await HideTrackedInstructionsFileAsync(harborHost != null ? harborHost.Commands : _LocalCommands, worktreePath, instructionsFileName, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _Logging.Warn(_Header + "could not hide tracked " + instructionsFileName + " from git: " + ex.Message);
            }

            _Logging.Debug(_Header + "generated mission instructions at " + instructionsPath);
        }

        private async Task<List<MissionPlaybookSnapshot>> LoadMissionPlaybookSnapshotsAsync(Mission mission, CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (mission.PlaybookSnapshots != null && mission.PlaybookSnapshots.Count > 0)
                return mission.PlaybookSnapshots;

            if (String.IsNullOrEmpty(mission.Id))
                return new List<MissionPlaybookSnapshot>();

            List<MissionPlaybookSnapshot> snapshots = await _Database.Playbooks
                .GetMissionSnapshotsAsync(mission.Id, token)
                .ConfigureAwait(false);
            mission.PlaybookSnapshots = snapshots;
            return snapshots;
        }

        private async Task<string> RenderSelectedPlaybooksMarkdownAsync(
            DockHost? harborHost,
            string worktreePath,
            Mission mission,
            List<MissionPlaybookSnapshot> snapshots,
            CancellationToken token)
        {
            if (String.IsNullOrEmpty(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (snapshots == null || snapshots.Count == 0) return String.Empty;

            List<string> sections = new List<string>();
            bool snapshotStateChanged = false;

            for (int i = 0; i < snapshots.Count; i++)
            {
                MissionPlaybookSnapshot snapshot = snapshots[i];
                string header = "### " + snapshot.FileName;
                string? description = String.IsNullOrWhiteSpace(snapshot.Description) ? null : snapshot.Description.Trim();

                // A captain on a Harbor cannot read the Admiral's files, so a by-reference playbook is attached into its
                // dock instead.
                PlaybookDeliveryModeEnum deliveryMode = snapshot.DeliveryMode;
                if (harborHost != null && deliveryMode == PlaybookDeliveryModeEnum.InstructionWithReference)
                    deliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree;

                switch (deliveryMode)
                {
                    case PlaybookDeliveryModeEnum.InstructionWithReference:
                        string resolvedPath = await MaterializeReferencePlaybookAsync(mission, snapshot, i, token).ConfigureAwait(false);
                        if (!String.Equals(snapshot.ResolvedPath, resolvedPath, StringComparison.Ordinal))
                        {
                            snapshot.ResolvedPath = resolvedPath;
                            snapshot.WorktreeRelativePath = null;
                            snapshotStateChanged = true;
                        }

                        sections.Add(
                            header + "\n" +
                            (description != null ? description + "\n" : "") +
                            "Read and follow this playbook at `" + resolvedPath + "`.");
                        break;

                    case PlaybookDeliveryModeEnum.AttachIntoWorktree:
                        WorktreePlaybookLocation attachedPlaybook = await MaterializeWorktreePlaybookAsync(
                            harborHost != null ? harborHost.Files : _LocalFiles,
                            worktreePath,
                            snapshot,
                            i,
                            token).ConfigureAwait(false);
                        if (!String.Equals(snapshot.ResolvedPath, attachedPlaybook.ResolvedPath, StringComparison.Ordinal) ||
                            !String.Equals(snapshot.WorktreeRelativePath, attachedPlaybook.RelativePath, StringComparison.Ordinal))
                        {
                            snapshot.ResolvedPath = attachedPlaybook.ResolvedPath;
                            snapshot.WorktreeRelativePath = attachedPlaybook.RelativePath;
                            snapshotStateChanged = true;
                        }

                        sections.Add(
                            header + "\n" +
                            (description != null ? description + "\n" : "") +
                            "Read and follow this attached playbook at `" + attachedPlaybook.RelativePath.Replace("\\", "/") + "`.");
                        break;

                    default:
                        sections.Add(
                            header + "\n" +
                            (description != null ? description + "\n\n" : "") +
                            snapshot.Content.TrimEnd());
                        break;
                }
            }

            if (snapshotStateChanged)
            {
                await _Database.Playbooks.SetMissionSnapshotsAsync(mission.Id, snapshots, token).ConfigureAwait(false);
            }

            return String.Join("\n\n", sections);
        }

        private async Task<string> MaterializeReferencePlaybookAsync(
            Mission mission,
            MissionPlaybookSnapshot snapshot,
            int selectionOrder,
            CancellationToken token)
        {
            string playbookDir = Path.Combine(_Settings.LogDirectory, "playbooks", mission.Id);
            Directory.CreateDirectory(playbookDir);

            string fileName = BuildMaterializedPlaybookFileName(selectionOrder, snapshot.FileName);
            string resolvedPath = Path.Combine(playbookDir, fileName);
            await File.WriteAllTextAsync(resolvedPath, snapshot.Content, token).ConfigureAwait(false);
            return resolvedPath;
        }

        private async Task<WorktreePlaybookLocation> MaterializeWorktreePlaybookAsync(
            IDockFileSystem files,
            string worktreePath,
            MissionPlaybookSnapshot snapshot,
            int selectionOrder,
            CancellationToken token)
        {
            string relativeDir = Path.Combine(".armada", "playbooks");
            string absoluteDir = Path.Combine(worktreePath, relativeDir);

            string fileName = BuildMaterializedPlaybookFileName(selectionOrder, snapshot.FileName);
            string absolutePath = Path.Combine(absoluteDir, fileName);
            await files.WriteTextAsync(absolutePath, snapshot.Content, token).ConfigureAwait(false);

            await files.AddGitExcludeEntryAsync(worktreePath, ".armada/playbooks/", token).ConfigureAwait(false);

            return new WorktreePlaybookLocation
            {
                ResolvedPath = absolutePath,
                RelativePath = Path.Combine(relativeDir, fileName)
            };
        }

        private static string BuildMaterializedPlaybookFileName(int selectionOrder, string? originalFileName)
        {
            string safeName = String.IsNullOrWhiteSpace(originalFileName) ? "PLAYBOOK.md" : originalFileName.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(invalid, '_');
            }

            return (selectionOrder + 1).ToString("D2") + "_" + safeName;
        }

        /// <summary>
        /// Resolve the prompt template configured on the mission's persona (<see cref="Persona.PromptTemplateName"/>).
        /// Returns null (use the conventional persona.&lt;name&gt; template) when the persona is not found or names no
        /// template, and logs and returns null when the configured template does not exist.
        /// </summary>
        private async Task<string?> ResolvePersonaTemplateNameAsync(Mission mission, CancellationToken token)
        {
            string personaName = PersonaCatalog.NormalizeName(mission.Persona ?? PersonaCatalog.Worker);
            if (String.IsNullOrWhiteSpace(personaName)) return null;

            Persona? persona;
            try
            {
                persona = await ReadPersonaByNameAsync(mission.TenantId, personaName, token, mission.UserId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read persona " + personaName + " for mission " + mission.Id + "; using the default persona template: " + ex.Message);
                return null;
            }

            if (persona == null || String.IsNullOrWhiteSpace(persona.PromptTemplateName)) return null;
            string templateName = persona.PromptTemplateName.Trim();
            if (_PromptTemplates == null) return templateName;

            PromptTemplate? template = await _PromptTemplates.ResolveAsync(templateName, token).ConfigureAwait(false);
            if (template == null)
            {
                _Logging.Warn(_Header + "persona " + persona.Name + " references prompt template " + templateName +
                    ", which does not exist; mission " + mission.Id + " uses the default template " + MissionPromptBuilder.GetPersonaTemplateName(personaName));
                return null;
            }

            return templateName;
        }

        /// <summary>
        /// Read the prior pipeline stage (the mission this one depends on), or null. Best-effort.
        /// </summary>
        private async Task<Mission?> ReadPreviousStageAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.DependsOnMissionId)) return null;
            try
            {
                return !String.IsNullOrEmpty(mission.TenantId)
                    ? await _Database.Missions.ReadAsync(mission.TenantId!, mission.DependsOnMissionId!, token).ConfigureAwait(false)
                    : await _Database.Missions.ReadAsync(mission.DependsOnMissionId!, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read prior stage " + mission.DependsOnMissionId + " for mission " + mission.Id + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolve the effective per-project persona override for a mission's persona by selecting the
        /// vessel's project profile (vessel -> fleet -> global). Best-effort: returns null on any error so
        /// a profile lookup never blocks dispatch.
        /// </summary>
        private async Task<PersonaOverride?> ResolvePersonaOverrideAsync(Vessel vessel, string? persona, CancellationToken token)
        {
            if (vessel == null || String.IsNullOrWhiteSpace(persona)) return null;

            try
            {
                List<ProjectProfile> profiles = await _Database.ProjectProfiles.EnumerateAllAsync(
                    new ProjectProfileQuery
                    {
                        TenantId = vessel.TenantId,
                        Active = true,
                        PageNumber = 1,
                        PageSize = 1000
                    },
                    token).ConfigureAwait(false);

                if (profiles.Count == 0) return null;

                ProjectProfile? profile = ProjectProfileService.SelectForVessel(profiles, vessel);
                return ProjectProfileService.ResolvePersonaOverride(profile, persona);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error resolving persona override for vessel " + vessel.Id + ": " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Resolve the vessel's project-profile skills into a combined markdown block for prompt
        /// injection. Best-effort: returns an empty string on any error or when no profile/skills apply.
        /// Skill references in the profile may be skill ids (skl_...) or skill names.
        /// </summary>
        private async Task<string> ResolveSkillsMarkdownAsync(Vessel vessel, CancellationToken token)
        {
            if (vessel == null) return String.Empty;

            try
            {
                List<ProjectProfile> profiles = await _Database.ProjectProfiles.EnumerateAllAsync(
                    new ProjectProfileQuery { TenantId = vessel.TenantId, Active = true, PageNumber = 1, PageSize = 1000 },
                    token).ConfigureAwait(false);
                if (profiles.Count == 0) return String.Empty;

                ProjectProfile? profile = ProjectProfileService.SelectForVessel(profiles, vessel);
                if (profile == null || profile.Skills == null || profile.Skills.Count == 0) return String.Empty;

                List<Skill> skills = await _Database.Skills.EnumerateAllAsync(
                    new SkillQuery { TenantId = vessel.TenantId, Active = true, PageNumber = 1, PageSize = 1000 },
                    token).ConfigureAwait(false);
                if (skills.Count == 0) return String.Empty;

                Dictionary<string, Skill> byId = new Dictionary<string, Skill>(StringComparer.Ordinal);
                Dictionary<string, Skill> byName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
                foreach (Skill skill in skills)
                {
                    byId[skill.Id] = skill;
                    if (!String.IsNullOrWhiteSpace(skill.Name)) byName[skill.Name.Trim()] = skill;
                }

                List<string> blocks = new List<string>();
                foreach (string reference in profile.Skills)
                {
                    if (String.IsNullOrWhiteSpace(reference)) continue;
                    Skill? resolved = null;
                    if (byId.TryGetValue(reference.Trim(), out Skill? byIdMatch)) resolved = byIdMatch;
                    else if (byName.TryGetValue(reference.Trim(), out Skill? byNameMatch)) resolved = byNameMatch;
                    if (resolved == null || String.IsNullOrWhiteSpace(resolved.Content)) continue;
                    blocks.Add("### " + resolved.Name + "\n\n" + resolved.Content.Trim());
                }

                return String.Join("\n\n", blocks);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error resolving skills for vessel " + vessel.Id + ": " + ex.ToString());
                return String.Empty;
            }
        }

        /// <summary>
        /// Preserve only stable project instructions from an existing runtime instruction file.
        /// Generated Armada mission blocks are stripped to avoid recursively injecting stale
        /// mission objectives into future captain prompts.
        /// </summary>
        private static string SanitizeExistingInstructions(string existing)
        {
            if (String.IsNullOrWhiteSpace(existing)) return String.Empty;

            int generatedSectionIndex = existing.IndexOf("# Mission Instructions", StringComparison.Ordinal);
            if (generatedSectionIndex < 0)
            {
                generatedSectionIndex = existing.IndexOf("## Mission Instructions", StringComparison.Ordinal);
            }

            if (generatedSectionIndex >= 0)
            {
                return existing.Substring(0, generatedSectionIndex).TrimEnd();
            }

            return existing.TrimEnd();
        }

        /// <summary>
        /// Builds the papercut directive. A captain that meets broken friction -- a stale doc, a dead
        /// link, a brief that contradicts itself, a missing sibling repository -- works around it and
        /// says nothing, so the next captain on the same vessel pays the same cost again. This asks for
        /// one line per problem and forbids fixing it out of scope, which keeps the report cheap and the
        /// diff clean.
        ///
        /// Built in code rather than added to a template, so an existing stored template row that
        /// predates this module cannot silently drop it.
        /// </summary>
        /// <returns>The papercut section.</returns>
        internal static string BuildPapercutsSection()
        {
            return
                "## Papercuts\n" +
                "\n" +
                "Report friction you meet. Do not work around it silently. One line each, on its own line:\n" +
                "\n" +
                "`[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"one line\",\"detail\":\"optional\",\"path\":\"optional/file.cs\"}`\n" +
                "\n" +
                "- Categories: BriefContradiction, ToolFailure, MissingDoc, BrokenLink, RepoFriction, " +
                "TestFlake, EnvSetup, PlatformBug, Other. Severity: Low, Medium, High.\n" +
                "- Report it, do not fix it. A fix outside your mission scope is a merge conflict for another captain.\n" +
                "- Never block on a papercut. Report it and continue the mission.\n" +
                "- Your own assigned work is not a papercut. Report what made the work harder than it needed to be.\n" +
                "- Include no credentials, tokens, or absolute host paths.\n" +
                "- Ten per mission is the limit. Report the ones that cost you time.\n";
        }

        /// <summary>
        /// Resolve a named template section. Falls back to empty string if no template service or template not found.
        /// </summary>
        private async Task<string> ResolveSectionAsync(string templateName, Dictionary<string, string> templateParams, CancellationToken token)
        {
            if (_PromptTemplates != null)
            {
                string rendered = await _PromptTemplates.RenderAsync(templateName, templateParams, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(rendered))
                    return rendered;
            }

            // Hardcoded fallbacks for backward compatibility when template service is unavailable
            string fallback = GetHardcodedFallback(templateName);
            if (!String.IsNullOrEmpty(fallback))
            {
                foreach (KeyValuePair<string, string> kvp in templateParams)
                {
                    fallback = fallback.Replace("{" + kvp.Key + "}", kvp.Value ?? "");
                }
            }
            return fallback;
        }

        /// <summary>
        /// Returns hardcoded prompt section content as a fallback when the template service is unavailable.
        /// </summary>
        private string GetHardcodedFallback(string templateName)
        {
            switch (templateName)
            {
                case "mission.rules":
                    return
                        "## Rules\n" +
                        "- Work only within this worktree directory\n" +
                        "- Commit all changes to the current branch\n" +
                        "- Commit and push your changes -- the Admiral will also push if needed\n" +
                        "- If you encounter a blocking issue, commit what you have and exit\n" +
                        "- Exit with code 0 on success\n" +
                        "- Do not use extended/Unicode characters (em dashes, smart quotes, etc.) -- use only ASCII characters in all output and commit messages\n" +
                        "- Do not use ANSI color codes or terminal formatting in output -- keep all output plain text\n";

                case "mission.context_conservation":
                    return
                        "## Context Conservation (CRITICAL)\n" +
                        "\n" +
                        "You have a limited context window. Exceeding it will crash your process and fail the mission. " +
                        "Follow these rules to stay within limits:\n" +
                        "\n" +
                        "1. **NEVER read entire large files.** If a file is over 200 lines, read only the specific " +
                        "section you need using line offsets. Use grep/search to find the right section first.\n" +
                        "\n" +
                        "2. **Read before you write, but read surgically.** Read only the 10-30 lines around the code " +
                        "you need to change, not the whole file.\n" +
                        "\n" +
                        "3. **Do not explore the codebase broadly.** Only read files explicitly mentioned in your " +
                        "mission description. If the mission says to edit README.md, read only the section you need " +
                        "to edit, not the entire README.\n" +
                        "\n" +
                        "4. **Make your changes and finish.** Do not re-read files to verify your changes, do not " +
                        "read files for 'context' that isn't directly needed for your edit, and do not explore related " +
                        "files out of curiosity.\n" +
                        "\n" +
                        "5. **Complete the assigned scope.** Work incrementally and validate relevant changes. If a real " +
                        "blocker prevents completion, preserve your work and report the blocker explicitly. Never " +
                        "emit a success marker or claim completion for partial work.\n";

                case "mission.merge_conflict_avoidance":
                    return
                        "## Avoiding Merge Conflicts (CRITICAL)\n" +
                        "\n" +
                        "You are one of several captains working on this repository. Other captains may be working on " +
                        "other missions in parallel on separate branches. To prevent merge conflicts and landing failures, " +
                        "you MUST follow these rules:\n" +
                        "\n" +
                        "1. **Only modify files explicitly mentioned in your mission description.** If the description says " +
                        "to edit `src/routes/users.ts`, do NOT also refactor `src/routes/orders.ts` even if you notice " +
                        "improvements. Another captain may be working on that file.\n" +
                        "\n" +
                        "2. **Do not make \"helpful\" changes outside your scope.** Do not rename shared variables, " +
                        "reorganize imports in files you were not asked to touch, reformat code in unrelated files, " +
                        "update documentation files unless instructed, or modify configuration/project files " +
                        "(e.g., .csproj, package.json, tsconfig.json) unless your mission specifically requires it.\n" +
                        "\n" +
                        "3. **Do not modify barrel/index export files** (e.g., index.ts, mod.rs) unless your mission " +
                        "explicitly requires it. These are high-conflict files that many missions may need to touch.\n" +
                        "\n" +
                        "4. **Keep changes minimal and focused.** The fewer files you touch, the lower the risk of " +
                        "conflicts. If your mission can be completed by editing 2 files, do not edit 5.\n" +
                        "\n" +
                        "5. **If you must create new files**, prefer names that are specific to your mission's feature " +
                        "rather than generic names that another captain might also choose.\n" +
                        "\n" +
                        "6. **Do not modify or delete files created by another mission's branch.** You are working in " +
                        "an isolated worktree -- if you see files that seem unrelated to your mission, leave them alone.\n" +
                        "\n" +
                        "Violating these rules will cause your branch to conflict with other captains' branches during " +
                        "landing, resulting in a LandingFailed status and wasted work.\n";

                case "mission.progress_signals":
                    return
                        "## Progress Signals (Optional)\n" +
                        "You can report progress to the Admiral by printing these lines to stdout:\n" +
                        "- `[ARMADA:PROGRESS] 50` -- report completion percentage (0-100)\n" +
                        "- `[ARMADA:STATUS] Testing` -- report that you are now running tests (informational; `[ARMADA:STATUS] InProgress` switches back)\n" +
                        "- `[ARMADA:MESSAGE] your message here` -- send a progress message\n" +
                        "- `[ARMADA:TOKENS] input=1234 output=567 cached=0` -- report the tokens you consumed this session (input/prompt, output/completion, and cache-read). Emit this once near the end if your runtime can determine the counts; it lets the Admiral record real token usage instead of an estimate.\n" +
                        "- `[ARMADA:RESULT] COMPLETE` -- worker/test engineer mission finished successfully\n" +
                        "- `[ARMADA:VERDICT] PASS` -- judge approves the mission\n" +
                        "- `[ARMADA:VERDICT] FAIL` -- judge rejects the mission\n" +
                        "- `[ARMADA:VERDICT] NEEDS_REVISION` -- judge requests follow-up changes\n" +
                        "Architect missions must not emit `[ARMADA:RESULT]` or `[ARMADA:VERDICT]`; they must output only real mission definitions: one fenced `armada-plan` JSON block (preferred) or real `[ARMADA:MISSION]` blocks.\n";

                case "mission.model_context_updates":
                    return
                        "## Model Context Updates\n" +
                        "\n" +
                        "Model context accumulation is enabled for this vessel. Before you finish your mission, " +
                        "review the existing model context above (if any) and consider whether you have discovered " +
                        "key information that would help future agents work on this repository more effectively. " +
                        "Examples include: architectural insights, code style conventions, naming conventions, " +
                        "logging patterns, error handling patterns, testing patterns, build quirks, common pitfalls, " +
                        "important dependencies, interdependencies between modules, concurrency patterns, " +
                        "and performance considerations.\n" +
                        "\n" +
                        "If you have useful additions, call `update_vessel_context` with the `modelContext` " +
                        "parameter set to the COMPLETE updated model context (not just your additions -- include " +
                        "the existing content with your additions merged in). Be thorough -- this context is a " +
                        "goldmine for future agents. Focus on information that is not obvious from reading the code, " +
                        "and organize it clearly with sections or headings.\n" +
                        "\n" +
                        "If you have nothing to add, skip this step.\n";

                case "mission.playbooks_wrapper":
                    return
                        "## Playbooks\n" +
                        "These playbooks are part of the required instructions for this mission. Read and follow them.\n" +
                        "\n" +
                        "{SelectedPlaybooksMarkdown}\n";

                case "mission.captain_instructions_wrapper":
                    return
                        "## Captain Instructions\n" +
                        "{CaptainInstructions}\n";

                case "mission.project_context_wrapper":
                    return
                        "## Project Context\n" +
                        "{ProjectContext}\n";

                case "mission.code_style_wrapper":
                    return
                        "## Code Style\n" +
                        "{StyleGuide}\n";

                case "mission.model_context_wrapper":
                    return
                        "## Model Context\n" +
                        "The following context was accumulated by AI agents during previous missions on this repository. " +
                        "Use this information to work more effectively.\n" +
                        "\n" +
                        "{ModelContext}\n";

                case "mission.metadata":
                    return
                        "# Mission Instructions\n" +
                        "\n" +
                        "{PersonaPrompt}\n" +
                        "\n" +
                        "## Mission\n" +
                        "- **Title:** {MissionTitle}\n" +
                        "- **ID:** {MissionId}\n" +
                        "- **Voyage:** {VoyageId}\n" +
                        "\n" +
                        "## Description\n" +
                        "{MissionDescription}\n" +
                        "\n" +
                        "## Repository\n" +
                        "- **Name:** {VesselName}\n" +
                        "- **Branch:** {BranchName}\n" +
                        "- **Default Branch:** {DefaultBranch}\n";

                case "mission.existing_instructions_wrapper":
                    return
                        "\n## Existing Project Instructions\n" +
                        "\n" +
                        "{ExistingClaudeMd}";

                default:
                    return "";
            }
        }

        #endregion

        #region Private-Methods

        private async Task<bool> HasUntriedIdleCaptainAsync(Mission mission, MissionLaunchFailure failure, CancellationToken token)
        {
            // Backoff protects against relaunching the same broken captain; it should not hold a mission back when a
            // captain that has not failed it is idle and could start it now.
            List<Captain> idle = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle, token).ConfigureAwait(false);
            lock (failure.FailedCaptainIds)
                return idle.Any(c => CaptainVisibleToMission(c, mission) && !failure.FailedCaptainIds.Contains(c.Id));
        }

        private async Task QuarantineMisconfiguredCaptainAsync(Captain captain, string reason)
        {
            try
            {
                // Take the captain out of Idle atomically where the tenant-scoped reserve applies; a tenantless captain
                // (single-user setups and tests) is checked and written directly.
                if (!String.IsNullOrEmpty(captain.TenantId)
                    && !await _Database.Captains.TryReserveAsync(captain.TenantId!, captain.Id, CaptainStateEnum.Quarantined, CancellationToken.None).ConfigureAwait(false))
                {
                    _Logging.Info(_Header + "captain " + captain.Id + " was taken before it could be quarantined for its configuration; leaving it");
                    return;
                }

                Captain? current = await _Database.Captains.ReadAsync(captain.Id, CancellationToken.None).ConfigureAwait(false);
                if (current == null) return;
                if (String.IsNullOrEmpty(captain.TenantId))
                {
                    if (current.State != CaptainStateEnum.Idle || !String.IsNullOrEmpty(current.CurrentMissionId)) return;
                    current.State = CaptainStateEnum.Quarantined;
                }
                else if (current.State != CaptainStateEnum.Quarantined)
                {
                    return;
                }
                current.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(_Settings.CaptainQuarantineMinutes);
                current.QuarantineReason = "configuration: " + reason;
                current.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Captains.UpdateAsync(current, CancellationToken.None).ConfigureAwait(false);
                _Logging.Warn(_Header + "quarantined captain " + captain.Id + " for " + _Settings.CaptainQuarantineMinutes + " minute(s): " + reason);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not quarantine misconfigured captain " + captain.Id + ": " + ex.Message);
            }
        }

        private async Task<List<Captain>> ApplyCapacityLimitsAsync(List<Captain> idleCaptains, Mission mission, CancellationToken token)
        {
            if (idleCaptains.Count == 0) return idleCaptains;

            // Per-runtime caps (AgentSettings.MaxConcurrent, 0 = unlimited) and per-endpoint caps
            // (InferenceOptions.MaxConcurrentMissions) count every Working captain, so several captains pointed at one
            // local model server share its budget instead of each starting a run against it.
            Dictionary<AgentRuntimeEnum, int> runtimeLimits = new Dictionary<AgentRuntimeEnum, int>();
            foreach (AgentSettings agent in _Settings.Agents ?? new List<AgentSettings>())
            {
                if (agent.MaxConcurrent > 0 && (!runtimeLimits.TryGetValue(agent.Runtime, out int existing) || agent.MaxConcurrent < existing))
                    runtimeLimits[agent.Runtime] = agent.MaxConcurrent;
            }

            bool anyEndpoint = idleCaptains.Any(c => !String.IsNullOrEmpty(c.ModelEndpointId));
            if (runtimeLimits.Count == 0 && !anyEndpoint) return idleCaptains;

            List<Captain> working = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);
            Dictionary<string, ModelEndpoint?> endpoints = new Dictionary<string, ModelEndpoint?>(StringComparer.Ordinal);
            async Task<ModelEndpoint?> EndpointOf(Captain captain)
            {
                if (String.IsNullOrEmpty(captain.ModelEndpointId)) return null;
                if (!endpoints.TryGetValue(captain.ModelEndpointId!, out ModelEndpoint? endpoint))
                {
                    endpoint = await _Database.ModelEndpoints.ReadAsync(captain.ModelEndpointId!, token).ConfigureAwait(false);
                    endpoints[captain.ModelEndpointId!] = endpoint;
                }
                return endpoint;
            }

            Dictionary<string, int> workingByEndpoint = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (anyEndpoint)
            {
                foreach (Captain busy in working)
                {
                    ModelEndpoint? endpoint = await EndpointOf(busy).ConfigureAwait(false);
                    if (endpoint == null) continue;
                    string key = EndpointCapacityKey(endpoint);
                    workingByEndpoint[key] = (workingByEndpoint.TryGetValue(key, out int count) ? count : 0) + 1;
                }
            }

            List<Captain> admitted = new List<Captain>();
            foreach (Captain candidate in idleCaptains)
            {
                if (runtimeLimits.TryGetValue(candidate.Runtime, out int runtimeLimit) && working.Count(w => w.Runtime == candidate.Runtime) >= runtimeLimit)
                {
                    _Logging.Debug(_Header + "captain " + candidate.Id + " skipped for mission " + mission.Id + ": " + candidate.Runtime + " is at its MaxConcurrent of " + runtimeLimit);
                    continue;
                }

                ModelEndpoint? endpoint = await EndpointOf(candidate).ConfigureAwait(false);
                if (endpoint != null)
                {
                    if (!endpoint.Enabled)
                    {
                        _Logging.Debug(_Header + "captain " + candidate.Id + " skipped for mission " + mission.Id + ": its model endpoint " + endpoint.Id + " is disabled");
                        continue;
                    }

                    int? endpointLimit = endpoint.InferenceOptions?.MaxConcurrentMissions;
                    if (endpointLimit.HasValue && (workingByEndpoint.TryGetValue(EndpointCapacityKey(endpoint), out int busyCount) ? busyCount : 0) >= endpointLimit.Value)
                    {
                        _Logging.Debug(_Header + "captain " + candidate.Id + " skipped for mission " + mission.Id + ": endpoint " + endpoint.BaseUrl + " is at its limit of " + endpointLimit.Value + " concurrent mission(s)");
                        continue;
                    }
                }

                admitted.Add(candidate);
            }

            return admitted;
        }

        private static string EndpointCapacityKey(ModelEndpoint endpoint)
        {
            string url = (endpoint.BaseUrl ?? String.Empty).Trim().TrimEnd('/');
            return String.IsNullOrEmpty(url) ? "endpoint:" + endpoint.Id : url.ToLowerInvariant();
        }

        private async Task<string?> ReadDockHeadAsync(Dock dock, CancellationToken token)
        {
            if (String.IsNullOrEmpty(dock.WorktreePath)) return null;
            DockHost? harborHost = HarborHostFor(dock);
            IGitService? git = harborHost != null ? harborHost.Git : _Git;
            if (git == null) return null;
            try
            {
                return await git.GetHeadCommitHashAsync(dock.WorktreePath!, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read HEAD of dock " + dock.Id + ": " + ex.Message);
                return null;
            }
        }

        private async Task<string?> CheckStageCommitContinuityAsync(Mission mission, Dock dock, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.DependsOnMissionId) || String.IsNullOrEmpty(dock.WorktreePath)) return null;
            Mission? upstream = await _Database.Missions.ReadAsync(mission.DependsOnMissionId!, token).ConfigureAwait(false);
            if (upstream == null || String.IsNullOrEmpty(upstream.CommitHash)) return null;
            if (!String.Equals(upstream.BranchName, mission.BranchName, StringComparison.Ordinal)) return null;

            DockHost? harborHost = HarborHostFor(dock);
            IGitService? git = harborHost != null ? harborHost.Git : _Git;
            if (git == null) return null;

            string? head = await ReadDockHeadAsync(dock, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(head)) return null;
            if (String.Equals(head, upstream.CommitHash, StringComparison.OrdinalIgnoreCase)) return null;

            bool? contained;
            try
            {
                contained = await git.IsAncestorAsync(dock.WorktreePath!, upstream.CommitHash!, head!, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not verify commit continuity for mission " + mission.Id + ": " + ex.Message);
                return null;
            }

            if (contained == true) return null;

            string where = harborHost != null ? "Harbor " + harborHost.HarborId : "the Admiral host";
            return "Commit continuity check failed: branch " + mission.BranchName + " on " + where + " starts at " + head
                + ", which does not contain commit " + upstream.CommitHash + " produced by the previous stage " + upstream.Id + ". "
                + "The previous stage ran on a different host and its commits are not in this repository, so this stage would have started without them. "
                + "Remediation: run the stage where the previous stage ran (connect that Harbor, or turn off Require Harbor for launch if it ran on the Admiral), "
                + "or push branch " + mission.BranchName + " to the vessel's remote, then restart this mission.";
        }

        private const string _OutputRepairStart = "<!-- armada:output-repair -->";
        private const string _OutputRepairEnd = "<!-- /armada:output-repair -->";

        private async Task<bool> TryScheduleOutputRepairAsync(Mission mission, string problem, string contract, CancellationToken token)
        {
            // A person decides review-gated stages; never re-run behind their back.
            if (mission.RequiresReview) return false;

            int max = _Settings.MaxOutputRepairAttempts;
            if (mission.OutputRepairAttempts >= max) return false;

            MissionStatusEnum observedStatus = mission.Status;
            int attempt = mission.OutputRepairAttempts + 1;
            string note = _OutputRepairStart + "\n## Output contract repair (attempt " + attempt + " of " + max + ")\n"
                + "Your previous response for this mission could not be used: " + problem.Trim() + "\n"
                + "Do the same job again from the current branch and respond in exactly this format. Nothing else about the task has changed.\n"
                + "Required format: " + contract.Trim() + "\n"
                + _OutputRepairEnd;

            mission.OutputRepairAttempts = attempt;
            mission.Description = ReplaceOutputRepairNote(mission.Description, note);
            mission.Status = MissionStatusEnum.Pending;
            mission.CaptainId = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.AgentOutput = null;
            mission.DiffSnapshot = null;
            mission.FailureReason = null;
            mission.FailureKind = null;
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.TotalRuntimeMs = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            if (!await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { observedStatus }, token).ConfigureAwait(false))
            {
                _Logging.Info(_Header + "mission " + mission.Id + " left " + observedStatus + " before its output repair was recorded; not repairing");
                mission.OutputRepairAttempts = attempt - 1;
                return false;
            }

            _Logging.Warn(_Header + "mission " + mission.Id + " (" + (mission.Persona ?? "stage") + ") broke its output contract (" + problem.Trim()
                + "); running it again with the contract errors quoted (repair " + attempt + "/" + max + ")");
            return true;
        }

        private async Task FinishOutputRepairScheduledAsync(Mission mission, Captain captain, Dock? dock, CancellationToken token)
        {
            if (dock != null)
            {
                try { await _Docks.ReclaimAsync(dock.Id, token: CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _Logging.Warn(_Header + "error reclaiming dock " + dock.Id + " before output repair of mission " + mission.Id + ": " + ex.Message); }
            }

            await ReleaseCaptainAfterCompletionAsync(captain, mission.Id).ConfigureAwait(false);
            await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
        }

        private string AppendRepairHistory(string reason, Mission mission)
        {
            if (mission.OutputRepairAttempts <= 0)
                return _Settings.MaxOutputRepairAttempts <= 0 ? reason : reason + " (output repair is unavailable for review-gated stages or was not possible)";
            return reason + " (after " + mission.OutputRepairAttempts + " output-contract repair attempt(s); the stage kept breaking its output format, so check the captain's model and persona template)";
        }

        private static string ReplaceOutputRepairNote(string? description, string note)
        {
            string current = description ?? String.Empty;
            int start = current.IndexOf(_OutputRepairStart, StringComparison.Ordinal);
            if (start >= 0)
            {
                int end = current.IndexOf(_OutputRepairEnd, start, StringComparison.Ordinal);
                current = end >= 0
                    ? current.Substring(0, start) + current.Substring(end + _OutputRepairEnd.Length)
                    : current.Substring(0, start);
            }

            return current.TrimEnd() + "\n\n" + note;
        }

        private async Task ReleaseCaptainAfterCompletionAsync(Captain captain, string missionId)
        {
            // Compare-and-set: release only while the captain still holds this mission. Orphan recovery can finalize an
            // older mission using a captain record that has already moved on, and that newer assignment must survive.
            try
            {
                bool released = await _Database.Captains.TryReleaseFromMissionAsync(captain.Id, missionId, CancellationToken.None).ConfigureAwait(false);
                if (released)
                    _Logging.Info(_Header + "released captain " + captain.Id + " after mission " + missionId);
                else
                    _Logging.Debug(_Header + "skipping captain release for mission " + missionId + " because captain " + captain.Id + " no longer holds it");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not release captain " + captain.Id + " after mission " + missionId + ": " + ex.Message);
            }
        }

        private async Task RollbackAssignmentAsync(Mission mission, Captain captain, Dock? dock, bool preserveExistingBranch)
        {
            // Cleanup runs with no cancellation: a shutdown mid-assignment must not strand a claimed captain.
            try
            {
                mission.Status = MissionStatusEnum.Pending;
                mission.CaptainId = null;
                if (!preserveExistingBranch)
                    mission.BranchName = null;
                mission.DockId = null;
                mission.ProcessId = null;
                mission.StartedUtc = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                bool reverted = await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { MissionStatusEnum.Assigned }, CancellationToken.None).ConfigureAwait(false);
                if (!reverted)
                    _Logging.Info(_Header + "mission " + mission.Id + " already left Assigned; leaving its current state during rollback");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not revert mission " + mission.Id + " to Pending during rollback: " + ex.Message);
            }

            await ReleaseClaimedCaptainAsync(mission, captain).ConfigureAwait(false);
            await ReclaimAssignmentDockAsync(mission, dock).ConfigureAwait(false);
        }

        private async Task FailAssignmentAsync(Mission mission, Captain captain, Dock dock, string reason)
        {
            try
            {
                mission.Status = MissionStatusEnum.Failed;
                mission.FailureKind = MissionFailureKindEnum.Infra;
                mission.FailureReason = reason;
                mission.CaptainId = null;
                mission.DockId = null;
                mission.ProcessId = null;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                bool failed = await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { MissionStatusEnum.Assigned }, CancellationToken.None).ConfigureAwait(false);
                if (failed)
                    ArmadaMetrics.MissionsFailed.Add(1);
                else
                    _Logging.Info(_Header + "mission " + mission.Id + " already left Assigned; not marking it Failed");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not mark mission " + mission.Id + " failed: " + ex.Message);
            }

            await ReleaseClaimedCaptainAsync(mission, captain).ConfigureAwait(false);
            await ReclaimAssignmentDockAsync(mission, dock).ConfigureAwait(false);

            try
            {
                Signal failedSignal = new Signal(SignalTypeEnum.Error, "Mission " + mission.Id + " failed: " + reason);
                failedSignal.TenantId = mission.TenantId;
                failedSignal.UserId = mission.UserId;
                failedSignal.FromCaptainId = captain.Id;
                await _Database.Signals.CreateAsync(failedSignal, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record failure signal for mission " + mission.Id + ": " + ex.Message);
            }

            try
            {
                await CancelDependentPipelineStagesAsync(mission, CancellationToken.None).ConfigureAwait(false);
                await UpdateVoyageTerminalStatusAsync(mission.VoyageId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not settle dependents or voyage after mission " + mission.Id + " failed: " + ex.Message);
            }
        }

        private async Task ReleaseClaimedCaptainAsync(Mission mission, Captain captain)
        {
            try
            {
                bool released = await _Database.Captains.TryReleaseFromMissionAsync(captain.Id, mission.Id, CancellationToken.None).ConfigureAwait(false);
                if (released)
                {
                    captain.State = CaptainStateEnum.Idle;
                    captain.CurrentMissionId = null;
                    captain.CurrentDockId = null;
                    captain.ProcessId = null;
                    captain.RecoveryAttempts = 0;
                    _Logging.Info(_Header + "released captain " + captain.Id + " from mission " + mission.Id);
                }
                else
                {
                    _Logging.Info(_Header + "captain " + captain.Id + " no longer holds mission " + mission.Id + "; leaving it as is");
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not release captain " + captain.Id + " from mission " + mission.Id + ": " + ex.Message);
            }
        }

        private async Task ReclaimAssignmentDockAsync(Mission mission, Dock? dock)
        {
            if (dock == null) return;
            try
            {
                await _Docks.ReclaimAsync(dock.Id, token: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to reclaim dock " + dock.Id + " for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private static string BuildMissionBranchName(Captain captain, Mission mission)
        {
            return Constants.BranchPrefix + SanitizeBranchPathSegment(captain.Name) + "/" + mission.Id;
        }

        private static string SanitizeBranchPathSegment(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "captain";

            System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);
            bool previousDash = false;

            foreach (char current in value.Trim().ToLowerInvariant())
            {
                bool isAsciiLetter = current >= 'a' && current <= 'z';
                bool isAsciiDigit = current >= '0' && current <= '9';
                bool isSafeSeparator = current == '-' || current == '_';

                if (isAsciiLetter || isAsciiDigit || isSafeSeparator)
                {
                    builder.Append(current);
                    previousDash = false;
                }
                else if (!previousDash)
                {
                    builder.Append('-');
                    previousDash = true;
                }
            }

            string sanitized = builder.ToString().Trim('-', '_');
            if (String.IsNullOrEmpty(sanitized)) return "captain";

            const int maxSegmentLength = 64;
            if (sanitized.Length > maxSegmentLength)
            {
                sanitized = sanitized.Substring(0, maxSegmentLength).Trim('-', '_');
                if (String.IsNullOrEmpty(sanitized)) return "captain";
            }

            return sanitized;
        }

        private async Task CleanupArchitectBranchAsync(Mission mission, Dock? dock, CancellationToken token)
        {
            if (_Git == null)
            {
                _Logging.Debug(_Header + "git service unavailable -- skipping architect branch cleanup for mission " + mission.Id);
                return;
            }

            string? branchName = mission.BranchName ?? dock?.BranchName;
            if (String.IsNullOrEmpty(branchName) || String.IsNullOrEmpty(mission.VesselId))
            {
                return;
            }

            Vessel? vessel = !String.IsNullOrEmpty(mission.TenantId)
                ? await _Database.Vessels.ReadAsync(mission.TenantId, mission.VesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);

            // A Harbor-hosted dock's branch lives in the repository on that Harbor's host.
            DockHost? harborHost = HarborHostFor(dock);
            IGitService git = harborHost != null ? harborHost.Git : _Git;
            string? repositoryPath = harborHost != null ? dock!.RepositoryPath : vessel?.LocalPath;
            string? workingDirectory = harborHost != null ? (dock!.CheckoutPath ?? dock.RepositoryPath) : vessel?.WorkingDirectory;

            if (vessel == null || String.IsNullOrEmpty(repositoryPath))
            {
                _Logging.Warn(_Header + "unable to clean architect branch " + branchName +
                    " for mission " + mission.Id + " because vessel metadata is incomplete");
                return;
            }

            BranchCleanupPolicyEnum cleanupPolicy = vessel.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;
            if (cleanupPolicy == BranchCleanupPolicyEnum.None)
            {
                _Logging.Debug(_Header + "branch cleanup policy is None - retaining architect branch " + branchName + " after handoff");
                return;
            }

            try
            {
                await git.DeleteLocalBranchAsync(repositoryPath, branchName, token).ConfigureAwait(false);
                _Logging.Debug(_Header + "deleted architect branch " + branchName + " from bare repo after successful handoff");
            }
            catch (Exception branchEx)
            {
                _Logging.Warn(_Header + "failed to delete architect branch " + branchName + " from bare repo: " + branchEx.Message);
            }

            if (cleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote)
            {
                if (String.IsNullOrEmpty(workingDirectory))
                {
                    _Logging.Warn(_Header + "cannot delete remote architect branch " + branchName +
                        " because vessel working directory is not configured");
                    return;
                }

                try
                {
                    await git.DeleteRemoteBranchAsync(workingDirectory, branchName, token).ConfigureAwait(false);
                    _Logging.Debug(_Header + "deleted remote architect branch " + branchName + " after successful handoff");
                }
                catch (Exception remoteBranchEx)
                {
                    _Logging.Warn(_Header + "failed to delete remote architect branch " + branchName + ": " + remoteBranchEx.Message);
                }
            }
        }

        /// <summary>
        /// After a mission produces work, check if any missions in the same voyage depend on it
        /// and prepare them for assignment (inject prior stage context into description).
        /// </summary>
        /// <summary>
        /// Recover pipeline handoffs that dangled: a mission reached WorkProduced but its Pending
        /// downstream stage was never prepared (branch/context not copied), so it can never be
        /// dispatched. Re-drives the handoff for any such mission so the next stage becomes
        /// dispatchable. Architect stages are skipped here because re-driving their fan-out is not
        /// idempotent; their handoff is handled on completion. Returns the number re-driven.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Count of missions whose handoff was re-driven.</returns>
        public async Task<int> RecoverDanglingHandoffsAsync(CancellationToken token = default)
        {
            int redriven = 0;
            List<Mission> workProduced = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.WorkProduced, token).ConfigureAwait(false);

            foreach (Mission produced in workProduced)
            {
                if (String.IsNullOrEmpty(produced.VoyageId)) continue;
                if (String.Equals(produced.Persona, "Architect", StringComparison.OrdinalIgnoreCase)) continue;

                // A completion still being processed prepares its own handoff; re-driving it here would run the handoff twice.
                if (IsCompletionInFlight(produced.Id)) continue;

                // Recovery never bypasses the gates completion applies: a Judge's work moves on only with a substantiated PASS.
                if (PersonaCatalog.Matches(produced.Persona, PersonaCatalog.Judge)
                    && (JudgeVerdictParser.Parse(produced.AgentOutput) != JudgeVerdictEnum.Pass || !TryValidateJudgePassOutput(produced.AgentOutput, out _)))
                {
                    _Logging.Warn(_Header + "not re-driving handoff for Judge mission " + produced.Id + ": its recorded output is not a substantiated PASS");
                    continue;
                }

                List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(produced.VoyageId, token).ConfigureAwait(false);
                List<Mission> pendingDependents = voyageMissions
                    .Where(m => m.DependsOnMissionId == produced.Id && m.Status == MissionStatusEnum.Pending)
                    .ToList();
                if (pendingDependents.Count == 0) continue;

                bool anyUnprepared = pendingDependents.Any(dep => !IsPipelineHandoffPrepared(dep, produced));
                if (!anyUnprepared) continue;

                _Logging.Warn(_Header + "re-driving dangling pipeline handoff for WorkProduced mission " + produced.Id +
                    " (" + pendingDependents.Count + " pending dependent(s) not prepared)");
                try
                {
                    await TryHandoffToNextStageAsync(produced, token).ConfigureAwait(false);
                    Mission? afterHandoff = await _Database.Missions.ReadAsync(produced.Id, token).ConfigureAwait(false);
                    if (afterHandoff?.Status == MissionStatusEnum.Failed)
                    {
                        await CancelDependentPipelineStagesAsync(afterHandoff, token).ConfigureAwait(false);
                        await UpdateVoyageTerminalStatusAsync(afterHandoff.VoyageId, token).ConfigureAwait(false);
                    }
                    redriven++;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error re-driving handoff for mission " + produced.Id + ": " + ex.ToString());
                }
            }

            return redriven;
        }

        private async Task<bool> TryHandoffToNextStageAsync(Mission completedMission, CancellationToken token)
        {
            if (String.IsNullOrEmpty(completedMission.VoyageId)) return false;

            // Find missions that depend on this completed mission
            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(completedMission.VoyageId, token).ConfigureAwait(false);
            List<Mission> dependentMissions = voyageMissions.Where(m =>
                m.DependsOnMissionId == completedMission.Id &&
                m.Status == MissionStatusEnum.Pending).ToList();

            if (dependentMissions.Count == 0) return false;

            // Special handling for Architect stage: parse output into new missions
            if (String.Equals(completedMission.Persona, "Architect", StringComparison.OrdinalIgnoreCase))
            {
                ArchitectPlanParser.TryParse(completedMission.AgentOutput, out _, out string? planError);
                if (planError != null)
                {
                    if (await TryScheduleOutputRepairAsync(completedMission, planError, ArchitectPlanParser.FormatInstructions(), token).ConfigureAwait(false))
                        return false;

                    completedMission.FailureReason = AppendRepairHistory(planError, completedMission);
                    completedMission.Status = MissionStatusEnum.Failed;
                    completedMission.FailureKind = MissionFailureKindEnum.InvalidOutput;
                    completedMission.CompletedUtc = DateTime.UtcNow;
                    completedMission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(completedMission, token).ConfigureAwait(false);
                    return false;
                }
                List<ParsedArchitectMission> parsed = ParseArchitectOutput(completedMission);
                if (parsed.Count > 0)
                {
                    await ProjectArchitectMissionsToLogAsync(completedMission, parsed, token).ConfigureAwait(false);
                    _Logging.Debug(_Header + "architect produced " + parsed.Count + " mission definitions");

                    foreach (Mission nextMission in dependentMissions)
                    {
                if (String.Equals(nextMission.Persona, "Worker", StringComparison.OrdinalIgnoreCase))
                {
                    // Update the first parsed mission into this existing Worker mission slot
                    ParsedArchitectMission first = parsed[0];
                    nextMission.Title = first.Title + " [Worker]";
                    nextMission.Description = ArchitectHandoffMarker + "\n" + first.Description + await BuildArchitectSliceContextAsync(completedMission, parsed, 0, token).ConfigureAwait(false);
                    nextMission.WaitForVoyageWorkers = first.WaitForOtherMissions;
                    nextMission.BranchName = null;
                    nextMission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(nextMission, token).ConfigureAwait(false);
                    await RetitleDependentChainAsync(voyageMissions, nextMission, first.Title, first.Description, token).ConfigureAwait(false);

                    // Find what depends on this worker mission (Judge, Test Engineer stages)
                    // Create additional worker missions for remaining parsed items
                    for (int i = 1; i < parsed.Count; i++)
                    {
                                Mission additionalWorker = new Mission(parsed[i].Title + " [Worker]", ArchitectHandoffMarker + "\n" + parsed[i].Description
                                    + await BuildArchitectSliceContextAsync(completedMission, parsed, i, token).ConfigureAwait(false));
                                additionalWorker.TenantId = completedMission.TenantId;
                                additionalWorker.UserId = completedMission.UserId;
                                additionalWorker.VoyageId = completedMission.VoyageId;
                                additionalWorker.VesselId = completedMission.VesselId;
                        additionalWorker.Persona = "Worker";
                        additionalWorker.WaitForVoyageWorkers = parsed[i].WaitForOtherMissions;
                        additionalWorker.DependsOnMissionId = completedMission.Id;
                        additionalWorker.RequiresReview = nextMission.RequiresReview;
                        additionalWorker.ReviewDenyAction = nextMission.ReviewDenyAction;
                        additionalWorker.BranchName = null;
                        additionalWorker = await _Database.Missions.CreateAsync(additionalWorker, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "architect created additional worker mission " + additionalWorker.Id + ": " + parsed[i].Title);

                        await CloneDependentChainAsync(voyageMissions, nextMission, additionalWorker, parsed[i].Title, parsed[i].Description, token).ConfigureAwait(false);
                    }
                }
            }

                    await ApplyArchitectMissionDependenciesAsync(completedMission, parsed, token).ConfigureAwait(false);
                    return true; // Architect special handling complete, skip normal handoff
                }

                bool hadArchitectMarkers = !String.IsNullOrEmpty(completedMission.AgentOutput) &&
                    completedMission.AgentOutput.Contains("[ARMADA:MISSION]", StringComparison.Ordinal);

                string failureReason = hadArchitectMarkers
                    ? "Architect produced no valid [ARMADA:MISSION] definitions in output"
                    : "Architect produced no ```" + ArchitectPlanParser.FenceInfo + " plan block and no [ARMADA:MISSION] markers in output";

                // An absent plan is a format failure, not a judgment about the work: run the Architect again with the
                // exact contract before failing the voyage. Narration is never inferred into missions.
                if (await TryScheduleOutputRepairAsync(completedMission, failureReason, ArchitectPlanParser.FormatInstructions(), token).ConfigureAwait(false))
                    return false;

                failureReason = AppendRepairHistory(failureReason, completedMission);

                _Logging.Warn(_Header + "architect mission " + completedMission.Id +
                    " produced no valid mission definitions -- marking as failed");
                completedMission.Status = MissionStatusEnum.Failed;
                completedMission.FailureReason = failureReason;
                completedMission.FailureKind = MissionFailureKindEnum.InvalidOutput;
                completedMission.CompletedUtc = DateTime.UtcNow;
                completedMission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(completedMission, token).ConfigureAwait(false);
                return false;
            }

            // Stage-lag hardening: the just-completed stage's dock may have committed on a detached HEAD,
            // leaving its produced commit off the shared branch ref. The next stage reuses that same branch,
            // so safely advance the branch before handing off. An explicit refusal fails the handoff;
            // transport exceptions retain the existing best-effort logging behavior.
            if (!await HardenStageBranchAsync(completedMission, token).ConfigureAwait(false)) return false;

            foreach (Mission nextMission in dependentMissions)
            {
                // The persona template supplies role instructions; keep the persisted handoff concise.
                nextMission.Description = StripPipelineHandoff(nextMission.Description)
                    + PipelineHandoffContext.Build(completedMission);
                nextMission.BranchName = completedMission.BranchName;
                nextMission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(nextMission, token).ConfigureAwait(false);

                _Logging.Debug(_Header + "pipeline handoff: prepared mission " + nextMission.Id +
                    " (" + nextMission.Persona + ") with context from " + completedMission.Id +
                    " (" + completedMission.Persona + ")");

            }

            return true;
        }

        /// <summary>
        /// Force-advance the shared pipeline branch to the just-completed stage's produced commit. A prior
        /// stage's dock can end on a detached HEAD (its commit is not on the branch ref), so the next stage --
        /// which reuses the same branch -- would otherwise check out stale code. Resolve the dock's live HEAD
        /// and lift it onto the branch ref without rewinding newer work. A refused update blocks downstream
        /// dispatch with a recoverable commit reference and remediation. Transport exceptions retain the existing
        /// best-effort behavior.
        /// </summary>
        private async Task<bool> HardenStageBranchAsync(Mission completedMission, CancellationToken token)
        {
            if (_Git == null) return true;
            if (String.IsNullOrEmpty(completedMission.BranchName) || String.IsNullOrEmpty(completedMission.DockId)) return true;

            Dock? dock = !String.IsNullOrEmpty(completedMission.TenantId)
                ? await _Database.Docks.ReadAsync(completedMission.TenantId, completedMission.DockId!, token).ConfigureAwait(false)
                : await _Database.Docks.ReadAsync(completedMission.DockId!, token).ConfigureAwait(false);
            DockHost? harborHost = HarborHostFor(dock);
            IGitService git = harborHost != null ? harborHost.Git : _Git;
            if (dock == null || String.IsNullOrEmpty(dock.WorktreePath) || !await DockDirectoryExistsAsync(harborHost, dock.WorktreePath!, token).ConfigureAwait(false)) return true;

            try
            {
                string? headCommit = await git.GetHeadCommitHashAsync(dock.WorktreePath!, token).ConfigureAwait(false);
                if (String.IsNullOrEmpty(headCommit)) return true;

                // This also prevents rewinds when talking to an older Harbor implementation.
                if (await git.IsAncestorAsync(dock.WorktreePath!, headCommit!, completedMission.BranchName!, token).ConfigureAwait(false) == true) return true;
                bool advanced = await git.ForceAdvanceBranchAsync(dock.WorktreePath!, completedMission.BranchName!, headCommit!, token).ConfigureAwait(false);
                if (!advanced)
                {
                    completedMission.Status = MissionStatusEnum.Failed;
                    completedMission.CommitHash = headCommit;
                    completedMission.FailureKind = MissionFailureKindEnum.LandingConflict;
                    completedMission.FailureReason = "Pipeline handoff could not safely advance branch " + completedMission.BranchName
                        + " to stage commit " + headCommit + ". No downstream stage was dispatched. "
                        + "Remediation: inspect the branch and saved stage commit; integrate any divergent work without resetting newer commits, then restart the failed stage. Check Harbor/git availability if the update itself failed.";
                    completedMission.CompletedUtc = DateTime.UtcNow;
                    completedMission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(completedMission, token).ConfigureAwait(false);
                    return false;
                }
                if (advanced)
                {
                    _Logging.Debug(_Header + "stage-lag hardening: advanced branch " + completedMission.BranchName +
                        " to " + headCommit + " from dock " + dock.Id + " before handoff from mission " + completedMission.Id);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "stage-lag hardening failed for mission " + completedMission.Id + ": " + ex.ToString());
            }
            return true;
        }

        private async Task CancelDependentPipelineStagesAsync(Mission failedMission, CancellationToken token)
        {
            if (failedMission == null) throw new ArgumentNullException(nameof(failedMission));
            if (String.IsNullOrEmpty(failedMission.VoyageId)) return;

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(failedMission.VoyageId, token).ConfigureAwait(false);
            List<Mission> directDependents = voyageMissions.Where(m =>
                m.DependsOnMissionId == failedMission.Id &&
                (m.Status == MissionStatusEnum.Pending ||
                 m.Status == MissionStatusEnum.Assigned ||
                 m.Status == MissionStatusEnum.InProgress ||
                 m.Status == MissionStatusEnum.Testing ||
                 m.Status == MissionStatusEnum.Review)).ToList();

            foreach (Mission dependent in directDependents)
            {
                dependent.Status = MissionStatusEnum.Cancelled;
                dependent.FailureReason = "Blocked by failed dependency " + failedMission.Id;
                dependent.FailureKind = MissionFailureKindEnum.DependencyFailed;
                dependent.CompletedUtc = DateTime.UtcNow;
                dependent.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(dependent, token).ConfigureAwait(false);
                _Logging.Info(_Header + "cancelled dependent mission " + dependent.Id +
                    " because upstream mission " + failedMission.Id + " ended in " + failedMission.Status);
                await CancelDependentPipelineStagesAsync(dependent, token).ConfigureAwait(false);
            }
        }

        private async Task UpdateVoyageTerminalStatusAsync(string? voyageId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(voyageId)) return;

            Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            if (voyage == null) return;

            List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            if (missions.Count == 0) return;

            bool anyActive = missions.Any(m =>
                m.Status == MissionStatusEnum.Pending ||
                m.Status == MissionStatusEnum.Assigned ||
                m.Status == MissionStatusEnum.InProgress ||
                m.Status == MissionStatusEnum.Testing ||
                m.Status == MissionStatusEnum.Review ||
                m.Status == MissionStatusEnum.PullRequestOpen ||
                (m.Status == MissionStatusEnum.WorkProduced && IsCompletionInFlight(m.Id)));

            if (anyActive) return;

            bool allDone = missions.All(m =>
                m.Status == MissionStatusEnum.Complete ||
                m.Status == MissionStatusEnum.Failed ||
                m.Status == MissionStatusEnum.Cancelled ||
                m.Status == MissionStatusEnum.LandingFailed ||
                m.Status == MissionStatusEnum.WorkProduced);

            if (!allDone) return;

            // Not Complete while a WorkProduced mission is still waiting in the merge queue (F14).
            if (await MergeQueueMissionActivity.AnyAwaitingMergeQueueAsync(_Database, missions, token).ConfigureAwait(false)) return;

            bool anyFailed = missions.Any(m =>
                m.Status == MissionStatusEnum.Failed ||
                m.Status == MissionStatusEnum.LandingFailed);

            voyage.Status = anyFailed ? VoyageStatusEnum.Failed : VoyageStatusEnum.Complete;
            voyage.CompletedUtc = DateTime.UtcNow;
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);
            if (voyage.Status == VoyageStatusEnum.Complete) await PipelineStageSettler.SettleAsync(_Database, missions, token).ConfigureAwait(false);
            _Logging.Info(_Header + "voyage " + voyage.Id + " reached terminal status " + voyage.Status + " during mission completion");
        }

        private async Task CloneDependentChainAsync(
            List<Mission> voyageMissions,
            Mission templateMission,
            Mission newDependency,
            string parsedTitle,
            string parsedDescription,
            CancellationToken token)
        {
            List<Mission> directDependents = voyageMissions
                .Where(m => m.DependsOnMissionId == templateMission.Id)
                .OrderBy(m => m.CreatedUtc)
                .ToList();

            foreach (Mission templateChild in directDependents)
            {
                string childPersona = PersonaCatalog.NormalizeName(templateChild.Persona);
                if (String.IsNullOrEmpty(childPersona)) childPersona = templateChild.Persona ?? PersonaCatalog.Worker;

                Mission clonedStage = new Mission(
                    parsedTitle + " [" + childPersona + "]",
                    parsedDescription);
                clonedStage.TenantId = templateChild.TenantId;
                clonedStage.UserId = templateChild.UserId;
                clonedStage.VoyageId = templateChild.VoyageId;
                clonedStage.VesselId = templateChild.VesselId;
                clonedStage.Persona = childPersona;
                clonedStage.DependsOnMissionId = newDependency.Id;
                clonedStage.RequiresReview = templateChild.RequiresReview;
                clonedStage.ReviewDenyAction = templateChild.ReviewDenyAction;
                clonedStage.BranchName = null;
                clonedStage = await _Database.Missions.CreateAsync(clonedStage, token).ConfigureAwait(false);
                _Logging.Info(_Header + "architect created chained stage " + clonedStage.Id +
                    " (" + clonedStage.Persona + ") depending on " + newDependency.Id);
                await CloneDependentChainAsync(voyageMissions, templateChild, clonedStage, parsedTitle, parsedDescription, token).ConfigureAwait(false);
            }
        }

        private async Task RetitleDependentChainAsync(
            List<Mission> voyageMissions,
            Mission dependency,
            string parsedTitle,
            string parsedDescription,
            CancellationToken token)
        {
            List<Mission> directDependents = voyageMissions
                .Where(m => m.DependsOnMissionId == dependency.Id)
                .OrderBy(m => m.CreatedUtc)
                .ToList();

            foreach (Mission dependent in directDependents)
            {
                string dependentPersona = PersonaCatalog.NormalizeName(dependent.Persona);
                if (String.IsNullOrEmpty(dependentPersona)) dependentPersona = dependent.Persona ?? PersonaCatalog.Worker;

                dependent.Persona = dependentPersona;
                dependent.Title = parsedTitle + " [" + dependentPersona + "]";
                dependent.Description = parsedDescription;
                dependent.BranchName = null;
                dependent.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(dependent, token).ConfigureAwait(false);
                await RetitleDependentChainAsync(voyageMissions, dependent, parsedTitle, parsedDescription, token).ConfigureAwait(false);
            }
        }

        private async Task ApplyArchitectMissionDependenciesAsync(
            Mission architectMission,
            List<ParsedArchitectMission> parsed,
            CancellationToken token)
        {
            if (architectMission == null) throw new ArgumentNullException(nameof(architectMission));
            if (String.IsNullOrEmpty(architectMission.VoyageId)) return;
            if (parsed == null || parsed.Count == 0) return;

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(architectMission.VoyageId, token).ConfigureAwait(false);
            Dictionary<int, Mission> workerRootsByIndex = new Dictionary<int, Mission>();
            Dictionary<int, Mission> terminalStagesByIndex = new Dictionary<int, Mission>();
            Dictionary<string, Mission> terminalStagesByTitle = new Dictionary<string, Mission>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < parsed.Count; i++)
            {
                string workerTitle = parsed[i].Title + " [Worker]";
                Mission? workerRoot = voyageMissions.FirstOrDefault(m =>
                    String.Equals(m.Persona, "Worker", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(m.Title, workerTitle, StringComparison.OrdinalIgnoreCase));
                if (workerRoot == null) continue;

                Mission terminalStage = FindTerminalPipelineStage(voyageMissions, workerRoot);
                workerRootsByIndex[i + 1] = workerRoot;
                terminalStagesByIndex[i + 1] = terminalStage;
                terminalStagesByTitle[parsed[i].Title] = terminalStage;
            }

            for (int i = 0; i < parsed.Count; i++)
            {
                string? dependencyReference = parsed[i].DependsOnReference;
                int? dependencyIndex = parsed[i].DependsOnIndex;
                if (!dependencyIndex.HasValue && String.IsNullOrWhiteSpace(dependencyReference)) continue;
                if (!workerRootsByIndex.TryGetValue(i + 1, out Mission? workerRoot)) continue;

                // Structured plan: the dependency is an index. Legacy marker format: resolve the "Depends on:" text.
                Mission? resolvedDependency;
                if (dependencyIndex.HasValue)
                {
                    terminalStagesByIndex.TryGetValue(dependencyIndex.Value, out resolvedDependency);
                    dependencyReference = "mission " + dependencyIndex.Value;
                }
                else
                {
                    resolvedDependency = ResolveArchitectDependencyTerminalStage(
                        terminalStagesByIndex,
                        terminalStagesByTitle,
                        i + 1,
                        dependencyReference!);
                }
                if (resolvedDependency == null)
                {
                    _Logging.Warn(_Header + "could not resolve architect dependency '" + dependencyReference +
                        "' for mission '" + parsed[i].Title + "' -- leaving dependency on architect");
                    continue;
                }

                if (resolvedDependency.Id == workerRoot.Id)
                {
                    _Logging.Warn(_Header + "ignoring self-referential architect dependency '" + dependencyReference +
                        "' for mission '" + parsed[i].Title + "'");
                    continue;
                }

                workerRoot.DependsOnMissionId = resolvedDependency.Id;
                workerRoot.BranchName = null;
                workerRoot.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(workerRoot, token).ConfigureAwait(false);

                _Logging.Debug(_Header + "architect sequenced worker mission " + workerRoot.Id +
                    " to depend on terminal stage " + resolvedDependency.Id +
                    " from reference '" + dependencyReference + "'");
            }
        }

        private static Mission FindTerminalPipelineStage(IEnumerable<Mission> voyageMissions, Mission root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            Mission current = root;
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);

            while (!String.IsNullOrEmpty(current.Id) && visited.Add(current.Id))
            {
                Mission? next = voyageMissions
                    .Where(m => m.DependsOnMissionId == current.Id)
                    .OrderBy(m => m.CreatedUtc)
                    .FirstOrDefault();
                if (next == null) break;
                current = next;
            }

            return current;
        }

        private async Task<bool> HasDependentPipelineStages(string? voyageId, string missionId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(voyageId)) return false;

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            return voyageMissions.Any(m => m.DependsOnMissionId == missionId);
        }

        /// <summary>
        /// Parse mission definitions from an architect's output. The structured armada-plan JSON block
        /// (<see cref="ArchitectPlanParser"/>) is preferred: its dependsOn and waitForOtherMissions fields drive
        /// sequencing. Only when the output has no valid plan block does this fall back to the legacy formats:
        /// [ARMADA:MISSION] markers (with a "Depends on:" line) and then numbered summary lines, read from the agent
        /// output, the diff snapshot, or the description.
        /// </summary>
        private async Task<string> BuildArchitectSliceContextAsync(Mission architectMission, List<ParsedArchitectMission> parsed, int index, CancellationToken token)
        {
            // Each Worker gets one slice of the plan; give it the overall objective and the shape of the whole plan so
            // it neither drifts from the goal nor redoes a sibling's part.
            StringBuilder sb = new StringBuilder();
            sb.Append("\n\n## Full plan reference\nArchitect mission: ").Append(architectMission.Id)
                .Append(". Retrieve its complete output with mission_status or GET /api/v1/missions/")
                .Append(architectMission.Id).Append(" when relevant context is missing; do only your assigned slice.\n");
            Voyage? voyage = String.IsNullOrEmpty(architectMission.VoyageId) ? null : await _Database.Voyages.ReadAsync(architectMission.VoyageId!, token).ConfigureAwait(false);
            string objective = voyage != null && !String.IsNullOrWhiteSpace(voyage.Description) ? voyage.Description! : (voyage?.Title ?? String.Empty);
            if (!String.IsNullOrWhiteSpace(objective))
            {
                const int maxObjective = 2000;
                string trimmed = objective.Trim();
                if (trimmed.Length > maxObjective) trimmed = trimmed.Substring(0, maxObjective) + "\n(truncated)";
                sb.Append("\n\n## Overall objective (context only; do only your part)\n").Append(trimmed);
            }

            if (parsed.Count > 1)
            {
                sb.Append("\n\n## The Architect's plan (your part is marked; other parts are done by other missions)\n");
                for (int i = 0; i < parsed.Count; i++)
                    sb.Append(i + 1).Append(". ").Append(parsed[i].Title).Append(i == index ? "  <- this mission" : String.Empty).Append('\n');
            }

            return sb.ToString();
        }

        private List<ParsedArchitectMission> ParseArchitectOutput(Mission architectMission)
        {
            List<ParsedArchitectMission> results = new List<ParsedArchitectMission>();
            HashSet<string> seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (ArchitectPlanParser.TryParse(architectMission.AgentOutput, out ArchitectPlan? plan) && plan != null)
            {
                foreach (ArchitectPlanMission planned in plan.Missions)
                {
                    ParsedArchitectMission parsed = new ParsedArchitectMission();
                    parsed.Title = planned.Title ?? String.Empty;
                    parsed.Description = ArchitectPlanParser.DescribeMission(plan, planned);
                    parsed.DependsOnIndex = planned.DependsOn;
                    parsed.WaitForOtherMissions = planned.WaitForOtherMissions;
                    results.Add(parsed);
                }

                return results;
            }

            // Only what the Architect produced counts: its output, then a plan it committed (the diff). Explicit
            // [ARMADA:MISSION] markers remain supported for customized templates. Numbered narration ("1. Read the
            // README") is never inferred into missions: a plan without a structured block gets a repair turn instead.
            string?[] candidateSources =
            {
                architectMission.AgentOutput,
                architectMission.DiffSnapshot
            };

            for (int i = 0; i < candidateSources.Length; i++)
            {
                string? candidateSource = candidateSources[i];
                if (String.IsNullOrWhiteSpace(candidateSource)) continue;

                string source = candidateSource.Replace("\r\n", "\n");
                ParseArchitectMissionMarkers(source, results, seenTitles);
                if (results.Count > 0) break;
            }

            return results;
        }

        private void ParseArchitectMissionMarkers(
            string source,
            List<ParsedArchitectMission> results,
            HashSet<string> seenTitles)
        {
            if (String.IsNullOrWhiteSpace(source)) return;

            string[] segments = System.Text.RegularExpressions.Regex.Split(source, @"(?m)^\[ARMADA:MISSION\][ \t]*");

            for (int i = 1; i < segments.Length; i++)
            {
                string segment = segments[i].Trim();
                if (String.IsNullOrEmpty(segment)) continue;

                int closingTagIndex = segment.IndexOf("[/ARMADA:MISSION]", StringComparison.Ordinal);
                if (closingTagIndex >= 0)
                    segment = segment.Substring(0, closingTagIndex).Trim();

                if (String.IsNullOrEmpty(segment)) continue;

                int newlineIndex = segment.IndexOf('\n');
                string title;
                string description;

                if (newlineIndex >= 0)
                {
                    title = segment.Substring(0, newlineIndex).Trim();
                    description = segment.Substring(newlineIndex + 1).Trim();
                }
                else
                {
                    title = segment.Trim();
                    description = "";
                }

                TryAddParsedArchitectMission(results, seenTitles, title, description);
            }
        }

        private void TryAddParsedArchitectMission(
            List<ParsedArchitectMission> results,
            HashSet<string> seenTitles,
            string? title,
            string? description)
        {
            if (String.IsNullOrWhiteSpace(title)) return;

            string normalizedTitle = title.Trim();
            string normalizedDescription = NormalizeArchitectDescription(description);
            ArchitectDependencyExtractionResult dependencyExtraction = ExtractArchitectDependencyReference(normalizedDescription);
            normalizedDescription = dependencyExtraction.Description;
            string? dependencyReference = dependencyExtraction.DependsOnReference;
            if (String.IsNullOrWhiteSpace(normalizedDescription))
            {
                // Title-only architect blocks are still actionable; preserve the title as
                // the downstream mission description so worker/test/judge prompts are not empty.
                normalizedDescription = normalizedTitle;
            }

            if (IsArchitectPlaceholderTitle(normalizedTitle))
                return;

            if (IsArchitectPlaceholderDescription(normalizedDescription))
                return;

            if (seenTitles.Add(normalizedTitle))
            {
                ParsedArchitectMission parsed = new ParsedArchitectMission();
                parsed.Title = normalizedTitle;
                parsed.Description = normalizedDescription;
                parsed.DependsOnReference = dependencyReference;
                parsed.WaitForOtherMissions = LegacyDescriptionRequestsDeferral(normalizedDescription);
                results.Add(parsed);
            }
        }

        private static string NormalizeArchitectDescription(string? description)
        {
            if (String.IsNullOrWhiteSpace(description)) return "";

            List<string> descriptionLines = description
                .Split('\n')
                .Select(l => l.Trim('\r'))
                .ToList();

            while (descriptionLines.Count > 0)
            {
                string firstLine = descriptionLines[0].Trim();
                if (IsAgentTelemetryLine(firstLine))
                {
                    descriptionLines.RemoveAt(0);
                    continue;
                }

                break;
            }

            while (descriptionLines.Count > 0)
            {
                string lastLine = descriptionLines[descriptionLines.Count - 1].Trim();
                if (IsAgentTelemetryLine(lastLine))
                {
                    descriptionLines.RemoveAt(descriptionLines.Count - 1);
                    continue;
                }

                break;
            }

            return String.Join("\n", descriptionLines).Trim();
        }

        private static ArchitectDependencyExtractionResult ExtractArchitectDependencyReference(string description)
        {
            if (String.IsNullOrWhiteSpace(description))
            {
                return new ArchitectDependencyExtractionResult();
            }

            List<string> keptLines = new List<string>();
            string? dependencyReference = null;

            foreach (string rawLine in description.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = rawLine.Trim();
                if (TryExtractArchitectDependency(trimmed, out string? dependencyCandidate, out string? remainingDescription))
                {
                    if (String.IsNullOrWhiteSpace(dependencyReference))
                    {
                        dependencyReference = NormalizeArchitectDependencyReference(dependencyCandidate ?? String.Empty);
                    }

                    if (!String.IsNullOrWhiteSpace(remainingDescription))
                    {
                        keptLines.Add(remainingDescription);
                    }

                    continue;
                }

                keptLines.Add(rawLine.TrimEnd('\r'));
            }

            return new ArchitectDependencyExtractionResult
            {
                Description = String.Join("\n", keptLines).Trim(),
                DependsOnReference = String.IsNullOrWhiteSpace(dependencyReference) ? null : dependencyReference
            };
        }

        private static bool TryExtractArchitectDependency(string line, out string? dependencyReference, out string? remainingDescription)
        {
            dependencyReference = null;
            remainingDescription = null;

            if (String.IsNullOrWhiteSpace(line)) return false;

            string trimmed = line.Trim();
            if (!trimmed.StartsWith("depends on", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string remainder = trimmed.Substring("depends on".Length).TrimStart();
            if (remainder.StartsWith(":", StringComparison.Ordinal))
            {
                remainder = remainder.Substring(1).TrimStart();
            }

            if (String.IsNullOrWhiteSpace(remainder)) return false;

            int sentenceBoundary = remainder.IndexOf(". ", StringComparison.Ordinal);
            if (sentenceBoundary >= 0)
            {
                dependencyReference = remainder.Substring(0, sentenceBoundary).Trim().TrimEnd('.');
                remainingDescription = remainder.Substring(sentenceBoundary + 2).Trim();
            }
            else
            {
                dependencyReference = remainder.Trim().TrimEnd('.');
                remainingDescription = "";
            }

            return !String.IsNullOrWhiteSpace(dependencyReference);
        }

        private static string NormalizeArchitectDependencyReference(string dependencyReference)
        {
            if (String.IsNullOrWhiteSpace(dependencyReference)) return "";

            string normalized = dependencyReference.Trim().Trim('"', '\'', '`');
            int commentIndex = normalized.IndexOf(" (", StringComparison.Ordinal);
            if (commentIndex > 0)
            {
                normalized = normalized.Substring(0, commentIndex).Trim();
            }

            return normalized.Trim().TrimEnd('.', ';', ',');
        }

        private static Mission? ResolveArchitectDependencyTerminalStage(
            IReadOnlyDictionary<int, Mission> terminalStagesByIndex,
            IReadOnlyDictionary<string, Mission> terminalStagesByTitle,
            int currentMissionIndex,
            string dependencyReference)
        {
            string normalizedReference = NormalizeArchitectDependencyReference(dependencyReference);
            if (String.IsNullOrWhiteSpace(normalizedReference)) return null;

            System.Text.RegularExpressions.Match numericMatch =
                System.Text.RegularExpressions.Regex.Match(
                    normalizedReference,
                    @"^(?:mission\s+)?(?<index>\d+)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (numericMatch.Success &&
                Int32.TryParse(numericMatch.Groups["index"].Value, out int dependencyIndex) &&
                dependencyIndex > 0 &&
                dependencyIndex < currentMissionIndex &&
                terminalStagesByIndex.TryGetValue(dependencyIndex, out Mission? terminalStage))
            {
                return terminalStage;
            }

            if (terminalStagesByTitle.TryGetValue(normalizedReference, out Mission? byTitle))
            {
                return byTitle;
            }

            return null;
        }

        private static bool IsAgentTelemetryLine(string? line)
        {
            if (String.IsNullOrWhiteSpace(line)) return true;

            string trimmed = line.Trim();
            if (ProgressParser.TryParse(trimmed) != null) return true;

            return trimmed.Equals("tokens used", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[\d,]+$");
        }

        private static bool IsArchitectPlaceholderTitle(string? title)
        {
            if (String.IsNullOrWhiteSpace(title)) return true;

            string trimmed = title.Trim();
            if (trimmed.Equals("...", StringComparison.Ordinal)) return true;
            if (trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("goal:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("inputs:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("deliverables:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("dependencies:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("risks:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("done_when:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("status:", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.StartsWith("reason:", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static bool IsArchitectPlaceholderDescription(string? description)
        {
            if (String.IsNullOrWhiteSpace(description)) return false;

            string[] lines = description
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => !String.IsNullOrEmpty(l))
                .ToArray();

            if (lines.Length == 0) return false;

            string[] placeholderPrefixes =
            {
                "goal:",
                "inputs:",
                "deliverables:",
                "dependencies:",
                "risks:",
                "done_when:"
            };

            return lines.All(line =>
                placeholderPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
                line.Contains("...", StringComparison.Ordinal));
        }

        private async Task<bool> TryFailMissionForScopeViolationAsync(Mission mission, Dock dock, CancellationToken token)
        {
            if (_Git == null ||
                String.IsNullOrEmpty(mission.Description) ||
                String.IsNullOrEmpty(dock.Id) ||
                String.IsNullOrEmpty(dock.WorktreePath))
            {
                return false;
            }

            HashSet<string> allowedFiles = ParseMissionScopedFiles(mission.Description);
            if (allowedFiles.Count < 1)
            {
                return false;
            }

            string? startCommit = TryReadDockStartCommit(dock.Id);
            if (String.IsNullOrEmpty(startCommit))
            {
                _Logging.Warn(_Header + "scope validation skipped for mission " + mission.Id +
                    " because dock start commit metadata is missing for " + dock.Id);
                return false;
            }

            IReadOnlyList<string> changedFiles;
            try
            {
                DockHost? harborHost = HarborHostFor(dock);
                IGitService git = harborHost != null ? harborHost.Git : _Git;
                changedFiles = await git.GetChangedFilesSinceAsync(dock.WorktreePath, startCommit, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "scope validation failed for mission " + mission.Id +
                    " while reading changed files: " + ex.Message);
                return false;
            }

            List<string> outOfScopeFiles = new List<string>();
            foreach (string changedFile in changedFiles)
            {
                string normalizedPath = NormalizeMissionPath(changedFile);
                if (_IgnoredMissionArtifactFiles.Contains(normalizedPath))
                {
                    continue;
                }

                if (!allowedFiles.Contains(normalizedPath))
                {
                    outOfScopeFiles.Add(normalizedPath);
                }
            }

            if (outOfScopeFiles.Count < 1)
            {
                return false;
            }

            mission.Status = MissionStatusEnum.Failed;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            mission.FailureReason = "Mission modified files outside its scoped file list: " + String.Join(", ", outOfScopeFiles);
            mission.FailureKind = MissionFailureKindEnum.ScopeViolation;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + mission.Id + " failed scope validation: " + mission.FailureReason);
            return true;
        }

        private HashSet<string> ParseMissionScopedFiles(string description)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(description))
            {
                return files;
            }

            foreach (System.Text.RegularExpressions.Match directiveMatch in _ScopedFilesDirectiveRegex.Matches(description))
            {
                string fileSegment = directiveMatch.Groups["files"].Value;
                foreach (System.Text.RegularExpressions.Match pathMatch in _ScopedFileTokenRegex.Matches(fileSegment))
                {
                    string normalizedPath = NormalizeMissionPath(pathMatch.Groups["path"].Value);
                    if (!String.IsNullOrEmpty(normalizedPath))
                    {
                        files.Add(normalizedPath);
                    }
                }
            }

            return files;
        }

        private string? TryReadDockStartCommit(string dockId)
        {
            try
            {
                string metadataPath = Path.Combine(_Settings.LogDirectory, "docks", dockId + ".start");
                if (!File.Exists(metadataPath))
                {
                    return null;
                }

                return File.ReadAllText(metadataPath).Trim();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read dock start commit metadata for " + dockId + ": " + ex.ToString());
                return null;
            }
        }

        private static string NormalizeMissionPath(string path)
        {
            return (path ?? String.Empty).Trim().Replace('\\', '/');
        }

        private async Task EnsureMissionInstructionsPresentAsync(
            DockHost? harborHost,
            string worktreePath,
            Mission mission,
            Captain captain,
            CancellationToken token)
        {
            if (String.IsNullOrEmpty(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            string instructionsFileName = MissionPromptBuilder.GetInstructionsFileName(captain.Runtime.ToString());
            string instructionsPath = Path.Combine(worktreePath, instructionsFileName);
            IDockFileSystem files = harborHost != null ? harborHost.Files : _LocalFiles;
            if (await files.ReadTextAsync(instructionsPath, token).ConfigureAwait(false) != null) return;

            string snapshotPath = Path.Combine(_Settings.LogDirectory, "instructions", mission.Id + "." + instructionsFileName);
            if (!File.Exists(snapshotPath))
            {
                _Logging.Warn(_Header + "mission instructions missing at " + instructionsPath +
                    " and no snapshot exists at " + snapshotPath);
                return;
            }

            await files.WriteTextAsync(instructionsPath, await File.ReadAllTextAsync(snapshotPath, token).ConfigureAwait(false), token).ConfigureAwait(false);
            _Logging.Warn(_Header + "restored missing mission instructions from snapshot to " + instructionsPath);
        }

        private bool TryValidateJudgePassOutput(string? agentOutput, out string? failureReason)
        {
            // Delegate the bounded three-lens PASS contract to the pure JudgeContract so the prompt builders,
            // this gate, and the tests share one definition.
            string narrative = ExtractJudgeNarrative(agentOutput ?? String.Empty);
            if (JudgeContract.ValidatePass(agentOutput, narrative, out failureReason))
            {
                return true;
            }

            return false;
        }

        private static string ExtractJudgeNarrative(string agentOutput)
        {
            if (String.IsNullOrWhiteSpace(agentOutput)) return String.Empty;

            List<string> lines = new List<string>();
            string[] split = agentOutput.Replace("\r\n", "\n").Split('\n');

            foreach (string rawLine in split)
            {
                string line = rawLine.Trim();
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (IsAgentTelemetryLine(line)) continue;
                if (JudgeVerdictParser.ParseLine(line).HasValue) continue;

                lines.Add(line);
            }

            return String.Join(" ", lines);
        }

        private async Task EmitMissionOutcomeTelemetryAsync(Mission mission, Captain captain, CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            MissionOutcomeEventDefinition missionOutcomeEvent = BuildMissionOutcomeEvent(mission);

            try
            {
                ArmadaEvent outcomeEvent = new ArmadaEvent(missionOutcomeEvent.EventType, missionOutcomeEvent.EventMessage);
                outcomeEvent.TenantId = mission.TenantId;
                outcomeEvent.UserId = mission.UserId;
                outcomeEvent.EntityType = "mission";
                outcomeEvent.EntityId = mission.Id;
                outcomeEvent.CaptainId = captain.Id;
                outcomeEvent.MissionId = mission.Id;
                outcomeEvent.VesselId = mission.VesselId;
                outcomeEvent.VoyageId = mission.VoyageId;
                await _Database.Events.CreateAsync(outcomeEvent, token).ConfigureAwait(false);
            }
            catch (Exception evtEx)
            {
                _Logging.Warn(_Header + "error emitting mission outcome event for " + mission.Id + ": " + evtEx.Message);
            }
        }

        private static MissionOutcomeSignalDefinition BuildMissionOutcomeSignal(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            return mission.Status switch
            {
                MissionStatusEnum.Complete => new MissionOutcomeSignalDefinition { Type = SignalTypeEnum.Completion, Payload = "Mission completed: " + mission.Title },
                MissionStatusEnum.PullRequestOpen => new MissionOutcomeSignalDefinition { Type = SignalTypeEnum.Completion, Payload = "Pull request open: " + mission.Title },
                MissionStatusEnum.Failed => new MissionOutcomeSignalDefinition { Type = SignalTypeEnum.Error, Payload = BuildFailurePayload("Mission failed: ", mission) },
                MissionStatusEnum.LandingFailed => new MissionOutcomeSignalDefinition { Type = SignalTypeEnum.Error, Payload = BuildFailurePayload("Landing failed: ", mission) },
                MissionStatusEnum.Cancelled => new MissionOutcomeSignalDefinition { Type = SignalTypeEnum.Error, Payload = BuildFailurePayload("Mission cancelled: ", mission) },
                _ => new MissionOutcomeSignalDefinition { Type = SignalTypeEnum.Completion, Payload = "Work produced: " + mission.Title }
            };
        }

        private static MissionOutcomeEventDefinition BuildMissionOutcomeEvent(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            return mission.Status switch
            {
                MissionStatusEnum.Failed => new MissionOutcomeEventDefinition { EventType = "mission.failed", EventMessage = BuildFailurePayload("Mission failed: ", mission) },
                MissionStatusEnum.LandingFailed => new MissionOutcomeEventDefinition { EventType = "mission.landing_failed", EventMessage = BuildFailurePayload("Landing failed: ", mission) },
                MissionStatusEnum.Cancelled => new MissionOutcomeEventDefinition { EventType = "mission.cancelled", EventMessage = BuildFailurePayload("Mission cancelled: ", mission) },
                _ => new MissionOutcomeEventDefinition { EventType = "mission.work_produced", EventMessage = "Work produced: " + mission.Title }
            };
        }

        private static string BuildFailurePayload(string prefix, Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            string payload = prefix + mission.Title;
            if (!String.IsNullOrWhiteSpace(mission.FailureReason))
            {
                payload += " (" + mission.FailureReason + ")";
            }

            return payload;
        }

        /// <summary>
        /// Ensure parsed architect mission definitions are visible in the mission log even when
        /// the source was a diff snapshot or other non-stdout artifact.
        /// </summary>
        private async Task ProjectArchitectMissionsToLogAsync(Mission architectMission, List<ParsedArchitectMission> parsed, CancellationToken token)
        {
            if (architectMission == null) throw new ArgumentNullException(nameof(architectMission));
            if (parsed == null || parsed.Count == 0) return;

            try
            {
                string missionLogDir = Path.Combine(_Settings.LogDirectory, "missions");
                Directory.CreateDirectory(missionLogDir);
                string logFilePath = Path.Combine(missionLogDir, architectMission.Id + ".log");

                string existing = File.Exists(logFilePath)
                    ? await File.ReadAllTextAsync(logFilePath, token).ConfigureAwait(false)
                    : String.Empty;

                if (existing.Contains("[ARMADA:MISSION]"))
                {
                    return;
                }

                using (StreamWriter writer = new StreamWriter(logFilePath, append: true))
                {
                    await writer.WriteLineAsync(String.Empty).ConfigureAwait(false);
                    await writer.WriteLineAsync("[Armada] Parsed architect mission definitions:").ConfigureAwait(false);
                    foreach (ParsedArchitectMission mission in parsed)
                    {
                        await writer.WriteLineAsync("[ARMADA:MISSION] " + mission.Title).ConfigureAwait(false);
                        if (!String.IsNullOrEmpty(mission.DependsOnReference))
                        {
                            await writer.WriteLineAsync("Depends on: " + mission.DependsOnReference).ConfigureAwait(false);
                        }
                        if (!String.IsNullOrEmpty(mission.Description))
                        {
                            await writer.WriteLineAsync(mission.Description).ConfigureAwait(false);
                        }
                        await writer.WriteLineAsync(String.Empty).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not project architect mission definitions into log for " + architectMission.Id + ": " + ex.ToString());
            }
        }

        private async Task<Mission> RequireReviewMissionAsync(string missionId, CancellationToken token)
        {
            Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null) throw new KeyNotFoundException("Mission not found: " + missionId);
            if (mission.Status != MissionStatusEnum.Review || !mission.RequiresReview)
            {
                throw new InvalidOperationException("Mission " + missionId + " is not waiting for an explicit review decision.");
            }

            return mission;
        }

        private async Task<Dock?> ReadMissionDockAsync(Mission mission, CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (String.IsNullOrEmpty(mission.DockId)) return null;

            return !String.IsNullOrEmpty(mission.TenantId)
                ? await _Database.Docks.ReadAsync(mission.TenantId, mission.DockId, token).ConfigureAwait(false)
                : await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
        }

        private async Task ReclaimMissionDockAsync(string dockId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(dockId)) return;

            try
            {
                await _Docks.ReclaimAsync(dockId, token: token).ConfigureAwait(false);
            }
            catch (Exception reclaimEx)
            {
                _Logging.Warn(_Header + "error reclaiming dock " + dockId + ": " + reclaimEx.Message);
            }
        }

        private async Task DispatchPendingMissionsAsync(CancellationToken token)
        {
            List<Mission> pendingMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Pending, token).ConfigureAwait(false);
            if (!pendingMissions.Any()) return;

            Mission nextMission = pendingMissions.OrderBy(m => m.Priority).ThenBy(m => m.CreatedUtc).First();
            if (String.IsNullOrEmpty(nextMission.VesselId)) return;

            Vessel? vessel = await _Database.Vessels.ReadAsync(nextMission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) return;

            await TryAssignAsync(nextMission, vessel, token).ConfigureAwait(false);
        }

        private static string? NormalizeReviewComment(string? comment)
        {
            if (String.IsNullOrWhiteSpace(comment)) return null;
            return comment.Trim();
        }

        private static string BuildReviewDeniedFailureReason(string comment)
        {
            return "Review denied: " + comment;
        }

        private static string ApplyReviewFeedback(string? description, string reviewComment)
        {
            string existing = description ?? String.Empty;
            int markerIndex = existing.IndexOf(ReviewFeedbackMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                existing = existing.Substring(0, markerIndex).TrimEnd();
            }

            string feedbackSection =
                ReviewFeedbackMarker + "\n" +
                "## Review Feedback\n" +
                reviewComment.Trim() + "\n\n" +
                "Address this feedback and continue the mission on the existing branch.\n";

            if (String.IsNullOrWhiteSpace(existing))
            {
                return feedbackSection;
            }

            return existing.TrimEnd() + "\n\n---\n\n" + feedbackSection;
        }

        /// <summary>
        /// Append reviewer guidance from a conditional approval to a downstream stage description, so the next
        /// captain must take the prior-stage feedback into account. Idempotent: an existing guidance block is
        /// replaced rather than stacked.
        /// </summary>
        private static string ApplyReviewerGuidance(string? description, string reviewComment)
        {
            string existing = description ?? String.Empty;
            int markerIndex = existing.IndexOf(ReviewerGuidanceMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                existing = existing.Substring(0, markerIndex).TrimEnd();
            }

            string guidanceSection =
                ReviewerGuidanceMarker + "\n" +
                "## Reviewer Guidance (from prior stage approval)\n" +
                reviewComment.Trim() + "\n\n" +
                "The previous stage was conditionally approved with the guidance above. Take it into account as you carry out this stage.\n";

            if (String.IsNullOrWhiteSpace(existing))
            {
                return guidanceSection;
            }

            return existing.TrimEnd() + "\n\n---\n\n" + guidanceSection;
        }

        /// <summary>
        /// After a conditional approval, fold the reviewer's guidance into the pending next-stage mission(s)
        /// that depend on the just-approved mission, before they dispatch.
        /// </summary>
        private async Task ApplyConditionalFeedbackToNextStagesAsync(Mission mission, string reviewComment, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.VoyageId)) return;

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId, token).ConfigureAwait(false);
            foreach (Mission next in voyageMissions)
            {
                if (next.DependsOnMissionId != mission.Id) continue;
                if (next.Status != MissionStatusEnum.Pending) continue;

                next.Description = ApplyReviewerGuidance(next.Description, reviewComment);
                next.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(next, token).ConfigureAwait(false);
            }
        }

        private async Task<bool> HasStrictCaptainOverrideAsync(Mission mission, CancellationToken token)
        {
            List<CaptainAssignmentOverride> overrides = await ReadVoyageCaptainOverridesAsync(mission.VoyageId, token).ConfigureAwait(false);
            CaptainAssignmentOverride? chosen = overrides.FirstOrDefault(o => PersonaCatalog.Matches(o.Persona, RoutingPersona(mission)))
                ?? overrides.FirstOrDefault(o => o.Persona == "*" || String.IsNullOrEmpty(o.Persona));
            return chosen?.StrictCaptain == true && !String.IsNullOrEmpty(chosen.CaptainId);
        }

        private async Task<HashSet<string>> ReviewAuthorCaptainIdsAsync(Mission mission, CancellationToken token)
        {
            HashSet<string> authors = new HashSet<string>(StringComparer.Ordinal);
            if (!PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge)) return authors;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { mission.Id };
            string? parentId = mission.DependsOnMissionId;
            while (!String.IsNullOrEmpty(parentId) && seen.Add(parentId))
            {
                Mission? parent = await _Database.Missions.ReadAsync(parentId, token).ConfigureAwait(false);
                if (parent == null || parent.VoyageId != mission.VoyageId || PersonaCatalog.Matches(parent.Persona, PersonaCatalog.Judge)) break;
                if (!String.IsNullOrEmpty(parent.CaptainId)) authors.Add(parent.CaptainId);
                if (PersonaCatalog.Matches(parent.Persona, PersonaCatalog.Worker)) break;
                parentId = parent.DependsOnMissionId;
            }
            return authors;
        }

        private async Task<bool> IsAnyCaptainEligibleAsync(List<Captain> idleCaptains, Mission mission, CancellationToken token)
        {
            HashSet<string> reviewAuthors = await ReviewAuthorCaptainIdsAsync(mission, token).ConfigureAwait(false);
            idleCaptains = idleCaptains.Where(c => !reviewAuthors.Contains(c.Id)).ToList();
            // Mirrors FindAvailableCaptainAsync: a requested (dictated) captain that is idle is always eligible;
            // otherwise the persona fence and the tier floor apply.
            if (!String.IsNullOrEmpty(mission.RequestedCaptainId) && idleCaptains.Any(c => c.Id == mission.RequestedCaptainId && CaptainVisibleToMission(c, mission) &&
                (!c.QuarantineUntilUtc.HasValue || c.QuarantineUntilUtc <= DateTime.UtcNow)))
                return true;
            if (!String.IsNullOrEmpty(mission.RequestedCaptainId) && (mission.Title.Contains(_EscalatedMarker, StringComparison.Ordinal)
                || (mission.Description ?? "").Contains("## Escalated to ", StringComparison.Ordinal))) return false;

            if (await HasStrictCaptainOverrideAsync(mission, token).ConfigureAwait(false)) return false;

            IEnumerable<Captain> eligible = idleCaptains.Where(c => CaptainVisibleToMission(c, mission) &&
                (!c.QuarantineUntilUtc.HasValue || c.QuarantineUntilUtc <= DateTime.UtcNow));
            string routingPersona = RoutingPersona(mission);
            eligible = eligible.Where(c => String.IsNullOrEmpty(c.AllowedPersonas) || CaptainAllowsPersona(c, routingPersona));
            if (mission.Tier.HasValue)
                eligible = eligible.Where(c => CaptainTierSelector.EffectiveTier(c) >= mission.Tier.Value);
            if (String.IsNullOrEmpty(mission.RequestedCaptainId))
            {
                Persona? definition = await ReadPersonaByNameAsync(mission.TenantId, routingPersona, token, mission.UserId).ConfigureAwait(false);
                if (definition != null && definition.CaptainPriorities.Count > 0)
                    return PersonaCaptainPrioritySelector.Select(definition, eligible.ToList()) != null ||
                        (PersonaCaptainPrioritySelector.AllowsGeneralFallback(definition) && eligible.Any());
            }
            return eligible.Any();
        }

        private static bool CaptainVisibleToMission(Captain captain, Mission mission)
        {
            return captain.TenantId == mission.TenantId && (captain.UserId == null || captain.UserId == mission.UserId);
        }

        private async Task DescribeCaptainsAsync(MissionAssignmentBlocker blocker, List<Captain> captains, string? tenantId, CancellationToken token)
        {
            // Only captains in the mission's tenant are described, so the explanation never names another tenant's work.
            IEnumerable<Captain> visible = String.IsNullOrEmpty(tenantId)
                ? captains
                : captains.Where(c => String.Equals(c.TenantId, tenantId, StringComparison.Ordinal));
            foreach (Captain captain in visible.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                MissionAssignmentCaptainStatus status = new MissionAssignmentCaptainStatus
                {
                    CaptainId = captain.Id,
                    CaptainName = captain.Name,
                    State = captain.State
                };

                switch (captain.State)
                {
                    case CaptainStateEnum.Idle:
                        status.Detail = "idle";
                        break;
                    case CaptainStateEnum.Working:
                        status.MissionId = captain.CurrentMissionId;
                        Mission? current = String.IsNullOrEmpty(captain.CurrentMissionId) ? null : await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                        status.Detail = current != null ? "working on mission '" + current.Title + "'" : "working on another mission";
                        break;
                    case CaptainStateEnum.Planning:
                        PlanningSession? planning = (await _Database.PlanningSessions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false))
                            .Where(p => p.Status == PlanningSessionStatusEnum.Created || p.Status == PlanningSessionStatusEnum.Active || p.Status == PlanningSessionStatusEnum.Responding || p.Status == PlanningSessionStatusEnum.Stopping)
                            .OrderByDescending(p => p.LastUpdateUtc)
                            .FirstOrDefault();
                        status.PlanningSessionId = planning?.Id;
                        status.Detail = planning != null ? "in planning session '" + planning.Title + "'" : "reserved by a planning session";
                        break;
                    case CaptainStateEnum.Refining:
                        ObjectiveRefinementSession? refinement = (await _Database.ObjectiveRefinementSessions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false))
                            .Where(r => r.Status == ObjectiveRefinementSessionStatusEnum.Created || r.Status == ObjectiveRefinementSessionStatusEnum.Active || r.Status == ObjectiveRefinementSessionStatusEnum.Responding || r.Status == ObjectiveRefinementSessionStatusEnum.Stopping)
                            .OrderByDescending(r => r.LastUpdateUtc)
                            .FirstOrDefault();
                        status.RefinementSessionId = refinement?.Id;
                        status.ObjectiveId = refinement?.ObjectiveId;
                        Objective? objective = refinement == null || String.IsNullOrEmpty(refinement.ObjectiveId) ? null : await _Database.Objectives.ReadAsync(refinement.ObjectiveId, token).ConfigureAwait(false);
                        status.Detail = objective != null
                            ? "refining backlog item '" + objective.Title + "' (stop the refinement session to free it)"
                            : "reserved by a backlog refinement session (stop the session to free it)";
                        break;
                    case CaptainStateEnum.Quarantined:
                        status.QuarantineUntilUtc = captain.QuarantineUntilUtc;
                        status.QuarantineReason = captain.QuarantineReason;
                        status.Detail = "quarantined"
                            + (!String.IsNullOrEmpty(captain.QuarantineReason) ? " (" + captain.QuarantineReason + ")" : "")
                            + (captain.QuarantineUntilUtc.HasValue ? " until " + captain.QuarantineUntilUtc.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC" : "")
                            + "; release it from the captain page to use it sooner";
                        break;
                    case CaptainStateEnum.Stalled:
                        status.Detail = "stalled and awaiting recovery";
                        break;
                    case CaptainStateEnum.Stopping:
                        status.Detail = "stopping";
                        break;
                    case CaptainStateEnum.Analyzing:
                        status.Detail = "analyzing a vessel";
                        break;
                    default:
                        status.Detail = captain.State.ToString();
                        break;
                }

                blocker.Captains.Add(status);
            }
        }

        private async Task<Captain?> FindAvailableCaptainAsync(Mission mission, CancellationToken token)
        {
            string? persona = RoutingPersona(mission);
            CaptainTierEnum? requiredTier = mission.Tier;
            string? requestedCaptainId = mission.RequestedCaptainId;
            // Only idle captains are eligible for assignment
            List<Captain> idleCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle, token).ConfigureAwait(false);
            idleCaptains = idleCaptains.Where(c => CaptainVisibleToMission(c, mission) &&
                (!c.QuarantineUntilUtc.HasValue || c.QuarantineUntilUtc <= DateTime.UtcNow)).ToList();
            HashSet<string> reviewAuthors = await ReviewAuthorCaptainIdsAsync(mission, token).ConfigureAwait(false);
            idleCaptains.RemoveAll(c => reviewAuthors.Contains(c.Id));
            idleCaptains = await ApplyCapacityLimitsAsync(idleCaptains, mission, token).ConfigureAwait(false);
            if (idleCaptains.Count == 0)
                return null;

            // A captain that already failed to start this mission is used again only when no other idle captain could
            // take it (a single-captain setup keeps retrying with backoff). A strict assignment names its only captain.
            if (_LaunchFailures.TryGetValue(mission.Id, out MissionLaunchFailure? priorFailures)
                && !(!String.IsNullOrEmpty(requestedCaptainId) && await HasStrictCaptainOverrideAsync(mission, token).ConfigureAwait(false)))
            {
                List<Captain> untried;
                lock (priorFailures.FailedCaptainIds) untried = idleCaptains.Where(c => !priorFailures.FailedCaptainIds.Contains(c.Id)).ToList();
                if (untried.Count > 0 && untried.Count < idleCaptains.Count)
                {
                    _Logging.Info(_Header + "mission " + mission.Id + " skips " + (idleCaptains.Count - untried.Count) + " captain(s) that already failed to start it");
                    idleCaptains = untried;
                }
            }

            // Preferred (dictated) captain: honor it when it is idle, bypassing the AllowedPersonas fence and
            // tier routing since the operator explicitly chose it. When it is busy (or has been deleted), fall
            // through to persona/tier routing using requiredTier as the fallback tier.
            if (!String.IsNullOrEmpty(requestedCaptainId))
            {
                Captain? preferred = idleCaptains.FirstOrDefault(c => c.Id == requestedCaptainId);
                if (preferred != null)
                    return preferred;

                // Recovery must wait for its chosen alternative, never silently rerun the failed captain.
                if (mission.Title.Contains(_EscalatedMarker, StringComparison.Ordinal) || (mission.Description ?? "").Contains("## Escalated to ", StringComparison.Ordinal)) return null;
                if (await HasStrictCaptainOverrideAsync(mission, token).ConfigureAwait(false)) return null;
                Captain? requested = await _Database.Captains.ReadAsync(requestedCaptainId, token).ConfigureAwait(false);
                if (requested == null)
                    _Logging.Warn(_Header + "requested captain " + requestedCaptainId + " not found; falling back to normal persona/tier routing");
                else
                    _Logging.Info(_Header + "requested captain " + requestedCaptainId + " is busy; falling back by tier for this dispatch tick");
            }

            // Filter by AllowedPersonas (null persona = any; null AllowedPersonas = fills any persona).
            List<Captain> eligible;
            if (String.IsNullOrEmpty(persona))
            {
                eligible = new List<Captain>(idleCaptains);
            }
            else
            {
                eligible = new List<Captain>();
                foreach (Captain captain in idleCaptains)
                {
                    if (String.IsNullOrEmpty(captain.AllowedPersonas) || CaptainAllowsPersona(captain, persona))
                        eligible.Add(captain);
                }
            }

            if (eligible.Count == 0)
            {
                return null;
            }

            // Hierarchies apply only when dispatch did not explicitly request a captain.
            if (String.IsNullOrEmpty(requestedCaptainId) && !String.IsNullOrEmpty(persona))
            {
                Persona? definition = await ReadPersonaByNameAsync(mission.TenantId, persona, token, mission.UserId).ConfigureAwait(false);
                if (definition != null && definition.CaptainPriorities.Count > 0)
                {
                    List<Captain> priorityEligible = eligible.Where(c => !requiredTier.HasValue ||
                        CaptainTierSelector.EffectiveTier(c) >= requiredTier.Value).ToList();
                    Captain? selected = PersonaCaptainPrioritySelector.Select(definition, priorityEligible);
                    if (selected != null)
                    {
                        int matchedIndex = definition.CaptainPriorities.FindIndex(p => PersonaCaptainPrioritySelector.Matches(p, selected));
                        _Logging.Info(_Header + "persona " + persona + " selected captain " + selected.Id +
                            " at priority " + (matchedIndex + 1));
                        return selected;
                    }
                    if (!PersonaCaptainPrioritySelector.AllowsGeneralFallback(definition)) return null;
                }
            }

            // Tier routing -- only when the mission explicitly requires a tier (so default dispatch is
            // unchanged). Narrow to captains at/above the required tier, preferring the lowest eligible
            // tier so strong captains are not consumed by cheaper work (upward-only fallback).
            if (requiredTier.HasValue)
            {
                List<Captain> tierEligible = eligible
                    .Where(c => CaptainTierSelector.EffectiveTier(c) >= requiredTier.Value)
                    .ToList();
                if (tierEligible.Count == 0)
                {
                    // No idle captain is strong enough for this mission's tier; leave it pending.
                    return null;
                }
                CaptainTierEnum bestTier = tierEligible.Min(CaptainTierSelector.EffectiveTier);
                eligible = tierEligible.Where(c => CaptainTierSelector.EffectiveTier(c) == bestTier).ToList();
            }

            // Prefer captains whose PreferredPersona matches
            if (!String.IsNullOrEmpty(persona))
            {
                foreach (Captain captain in eligible)
                {
                    if (!String.IsNullOrEmpty(captain.PreferredPersona) &&
                        PersonaCatalog.Matches(captain.PreferredPersona, persona))
                    {
                        return captain;
                    }
                }
            }

            // No preferred match -- return first eligible
            return eligible[0];
        }

        /// <summary>
        /// Resolve and persist the preferred captain and fallback tier for a mission from (in order) an
        /// explicit value already set, the voyage-level override for the mission's persona, or the persona's
        /// default captain. A dangling captain reference is left as-is and degrades to normal routing at
        /// assignment time. Best-effort: never throws for a missing persona/voyage.
        /// </summary>
        private async Task ResolvePreferredCaptainAsync(Mission mission, CancellationToken token)
        {
            if (mission == null) return;
            if (!String.IsNullOrEmpty(mission.RequestedCaptainId)) return;
            string routingPersona = RoutingPersona(mission);

            string? resolvedCaptainId = null;
            CaptainTierEnum? resolvedTier = null;

            List<CaptainAssignmentOverride> overrides = await ReadVoyageCaptainOverridesAsync(mission.VoyageId, token).ConfigureAwait(false);

            // An exact per-persona override wins; a wildcard override ("*" or empty persona) applies to every
            // step and is the fallback. The wildcard lets a dispatch pin one captain regardless of pipeline
            // (including "Inherit", where the resolved stages are not known at dispatch time).
            CaptainAssignmentOverride? exactMatch = null;
            CaptainAssignmentOverride? wildcardMatch = null;
            foreach (CaptainAssignmentOverride ov in overrides)
            {
                if (String.IsNullOrEmpty(ov.Persona) || ov.Persona == "*")
                {
                    if (wildcardMatch == null) wildcardMatch = ov;
                    continue;
                }

                if (PersonaCatalog.Matches(ov.Persona, routingPersona))
                {
                    exactMatch = ov;
                    break;
                }
            }

            CaptainAssignmentOverride? chosen = exactMatch ?? wildcardMatch;
            if (chosen != null)
            {
                resolvedCaptainId = String.IsNullOrEmpty(chosen.CaptainId) ? null : chosen.CaptainId;
                resolvedTier = chosen.FallbackTier;
            }

            if (String.IsNullOrEmpty(resolvedCaptainId))
            {
                Persona? persona = await ReadPersonaByNameAsync(mission.TenantId, routingPersona, token, mission.UserId).ConfigureAwait(false);
                if (persona != null && persona.CaptainPriorities.Count == 0 && !String.IsNullOrEmpty(persona.DefaultCaptainId))
                    resolvedCaptainId = persona.DefaultCaptainId;
            }

            if (String.IsNullOrEmpty(resolvedCaptainId) && resolvedTier == null) return;

            bool changed = false;
            if (!String.IsNullOrEmpty(resolvedCaptainId))
            {
                mission.RequestedCaptainId = resolvedCaptainId;
                changed = true;
            }

            if (mission.Tier == null)
            {
                if (resolvedTier != null)
                {
                    mission.Tier = resolvedTier;
                    changed = true;
                }
                else if (!String.IsNullOrEmpty(mission.RequestedCaptainId))
                {
                    CaptainTierEnum? preferredTier = await CaptainEffectiveTierAsync(mission.RequestedCaptainId, token).ConfigureAwait(false);
                    if (preferredTier != null)
                    {
                        mission.Tier = preferredTier;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }
        }

        private async Task<Persona?> ReadPersonaByNameAsync(string? tenantId, string personaName, CancellationToken token, string? userId = null)
        {
            if (!String.IsNullOrEmpty(tenantId))
            {
                Persona? scoped = await _Database.Personas.ReadByNameAsync(tenantId, personaName, token).ConfigureAwait(false);
                if (scoped != null && (scoped.UserId == null || scoped.UserId == userId || scoped.Scope == Armada.Core.Enums.ScopeEnum.TenantWide)) return scoped;
            }

            Persona? shared = await _Database.Personas.ReadByNameAsync(personaName, token).ConfigureAwait(false);
            return shared != null && shared.TenantId == tenantId &&
                (shared.UserId == null || shared.UserId == userId || shared.Scope == Armada.Core.Enums.ScopeEnum.TenantWide) ? shared : null;
        }

        private async Task<CaptainTierEnum?> CaptainEffectiveTierAsync(string captainId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(captainId)) return null;
            Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null) return null;
            return CaptainTierSelector.EffectiveTier(captain);
        }

        private async Task<List<CaptainAssignmentOverride>> ReadVoyageCaptainOverridesAsync(string? voyageId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(voyageId)) return new List<CaptainAssignmentOverride>();
            Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            if (voyage == null) return new List<CaptainAssignmentOverride>();
            return DeserializeCaptainOverrides(voyage.CaptainOverridesJson);
        }

        /// <summary>
        /// Deserialize a voyage's captain-override JSON into a list. Returns an empty list for null, empty, or
        /// malformed input rather than throwing.
        /// </summary>
        /// <param name="json">Serialized override array, or null.</param>
        /// <returns>The parsed overrides, or an empty list.</returns>
        public static List<CaptainAssignmentOverride> DeserializeCaptainOverrides(string? json)
        {
            if (String.IsNullOrWhiteSpace(json)) return new List<CaptainAssignmentOverride>();
            try
            {
                List<CaptainAssignmentOverride>? parsed = JsonSerializer.Deserialize<List<CaptainAssignmentOverride>>(json);
                return parsed ?? new List<CaptainAssignmentOverride>();
            }
            catch (JsonException)
            {
                return new List<CaptainAssignmentOverride>();
            }
        }

        /// <summary>
        /// Serialize a list of captain overrides for voyage persistence. Returns null when the list is null or
        /// empty so the column stays null rather than storing "[]".
        /// </summary>
        /// <param name="overrides">The overrides to serialize, or null.</param>
        /// <returns>The serialized JSON array, or null.</returns>
        public static string? SerializeCaptainOverrides(List<CaptainAssignmentOverride>? overrides)
        {
            if (overrides == null || overrides.Count == 0) return null;
            return JsonSerializer.Serialize(overrides);
        }

        private static bool CaptainAllowsPersona(Captain captain, string persona)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (String.IsNullOrEmpty(persona)) return true;
            if (String.IsNullOrEmpty(captain.AllowedPersonas)) return true;

            try
            {
                List<string>? allowedPersonas = JsonSerializer.Deserialize<List<string>>(captain.AllowedPersonas);
                if (allowedPersonas != null)
                {
                    foreach (string allowedPersona in allowedPersonas)
                    {
                        if (PersonaCatalog.Matches(allowedPersona, persona))
                            return true;
                    }

                    return false;
                }
            }
            catch (JsonException)
            {
            }

            // AllowedPersonas is a JSON array of persona names. A value that does not deserialize is not searched as
            // text: the captain is treated as allowing no restricted persona until the setting is corrected.
            return false;
        }

        private static bool UsesSharedLocalAndWorkingDirectory(Vessel vessel)
        {
            if (vessel == null) return false;
            if (String.IsNullOrWhiteSpace(vessel.LocalPath) || String.IsNullOrWhiteSpace(vessel.WorkingDirectory))
                return false;

            try
            {
                string localPath = Path.GetFullPath(vessel.LocalPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string workingDirectory = Path.GetFullPath(vessel.WorkingDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return String.Equals(localPath, workingDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return String.Equals(vessel.LocalPath.Trim(), vessel.WorkingDirectory.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Determine whether a dependent pipeline mission has had upstream handoff context applied.
        /// The handoff path stamps the downstream mission with the dependency's branch name,
        /// so branch equality is used as the minimum readiness signal before launch.
        /// </summary>
        private static bool IsPipelineHandoffPrepared(Mission mission, Mission dependency)
        {
            if (mission == null || dependency == null) return false;
            if (String.IsNullOrEmpty(mission.DependsOnMissionId)) return true;

            if (String.Equals(dependency.Persona, "Architect", StringComparison.OrdinalIgnoreCase))
            {
                return !String.IsNullOrEmpty(mission.Description) &&
                    mission.Description.Contains(ArchitectHandoffMarker, StringComparison.Ordinal);
            }

            // If the dependency never had a branch, there is no stronger handoff signal
            // available here; fall back to allowing assignment.
            if (String.IsNullOrEmpty(dependency.BranchName)) return true;

            return String.Equals(mission.BranchName, dependency.BranchName, StringComparison.Ordinal);
        }

        private async Task<bool> ShouldDeferArchitectSequencedMissionAsync(Mission mission, CancellationToken token)
        {
            if (mission == null) return false;
            if (!String.Equals(mission.Persona, "Worker", StringComparison.OrdinalIgnoreCase)) return false;
            if (String.IsNullOrEmpty(mission.VoyageId)) return false;

            // Structured flag only (set from the architect plan's waitForOtherMissions, or once from the legacy
            // marker-format description when the architect output was parsed). Description wording is never read here.
            if (!mission.WaitForVoyageWorkers && !mission.Title.StartsWith(_ReworkTitlePrefix, StringComparison.Ordinal)) return false;

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId, token).ConfigureAwait(false);
            if (!mission.WaitForVoyageWorkers)
            {
                // Reworks created before barrier inheritance was persisted still inherit their root Worker's flag.
                Dictionary<string, Mission> byId = voyageMissions.ToDictionary(m => m.Id, StringComparer.Ordinal);
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                Mission? root = mission;
                while (root != null && visited.Add(root.Id))
                {
                    if (PersonaCatalog.Matches(root.Persona, PersonaCatalog.Worker)
                        && !root.Title.StartsWith(_ReworkTitlePrefix, StringComparison.Ordinal)) break;
                    root = !String.IsNullOrEmpty(root.DependsOnMissionId) && byId.TryGetValue(root.DependsOnMissionId!, out Mission? parent)
                        ? parent : null;
                }
                if (root?.WaitForVoyageWorkers != true) return false;
            }
            HashSet<string> ownChain = new HashSet<string>(StringComparer.Ordinal) { mission.Id };
            bool grew;
            do
            {
                grew = false;
                foreach (Mission candidate in voyageMissions)
                    if (!String.IsNullOrEmpty(candidate.DependsOnMissionId) && ownChain.Contains(candidate.DependsOnMissionId!)
                        && ownChain.Add(candidate.Id)) grew = true;
            } while (grew);

            return voyageMissions.Any(m =>
                !ownChain.Contains(m.Id) &&
                !PersonaCatalog.Matches(m.Persona, PersonaCatalog.Architect) &&
                m.Status != MissionStatusEnum.Complete &&
                m.Status != MissionStatusEnum.WorkProduced &&
                m.Status != MissionStatusEnum.Failed &&
                m.Status != MissionStatusEnum.Cancelled &&
                m.Status != MissionStatusEnum.LandingFailed);
        }

        /// <summary>
        /// Legacy fallback for architect output in the [ARMADA:MISSION] marker format, which has no structured
        /// sequencing field: a description that asks to run after the other implementation missions is converted once,
        /// when the architect output is parsed, into <see cref="Mission.WaitForVoyageWorkers"/>. Never applied to
        /// missions created any other way, and never consulted at dispatch time.
        /// </summary>
        private static bool LegacyDescriptionRequestsDeferral(string? description)
        {
            if (String.IsNullOrEmpty(description)) return false;

            string lowered = description.ToLowerInvariant();
            return lowered.Contains("after both implementation missions complete") ||
                lowered.Contains("sequential after both implementation missions") ||
                lowered.Contains("after the implementation missions land") ||
                lowered.Contains("after the implementation details are settled") ||
                lowered.Contains("after implementation details are settled") ||
                lowered.Contains("after the implementation details are finalized");
        }

        /// <summary>
        /// The Harbor host of a Harbor-hosted dock, or null for a dock on this machine (which keeps using the local git
        /// service and disk exactly as before).
        /// </summary>
        private MissionLaunchFailure RecordLaunchFailure(Mission mission, Captain captain, Exception ex)
        {
            MissionLaunchFailure failure = _LaunchFailures.AddOrUpdate(mission.Id,
                _ => new MissionLaunchFailure { Count = 1 },
                (_, existing) => { existing.Count++; return existing; });
            lock (failure.FailedCaptainIds) failure.FailedCaptainIds.Add(captain.Id);
            HarborJobFailedException? harborFailure = FindInnerException<HarborJobFailedException>(ex);
            AgentRuntimeNotInstalledException? notInstalled = FindInnerException<AgentRuntimeNotInstalledException>(ex);
            string reason = notInstalled != null ? notInstalled.Message : harborFailure != null ? harborFailure.HarborMessage : ex.Message;
            failure.Reason = reason.Trim().TrimEnd('.') + ".";
            failure.LastUtc = DateTime.UtcNow;
            // 30s, 60s, 2m, 4m, then every 5 minutes.
            double seconds = Math.Min(300, 30 * Math.Pow(2, Math.Max(0, failure.Count - 1)));
            failure.NextAttemptUtc = DateTime.UtcNow.AddSeconds(seconds);
            _Logging.Warn(_Header + "mission " + mission.Id + ": launch " + failure.Count + " of captain " + captain.Id + " failed (" + failure.Reason + "); next attempt in " + seconds.ToString("0") + "s");
            return failure;
        }

        private static bool IsRuntimeMissing(Exception ex)
        {
            if (FindInnerException<AgentRuntimeNotInstalledException>(ex) != null) return true;
            HarborJobFailedException? harborFailure = FindInnerException<HarborJobFailedException>(ex);
            return harborFailure != null && harborFailure.HarborMessage.Contains("not installed", StringComparison.OrdinalIgnoreCase);
        }

        private static T? FindInnerException<T>(Exception? ex) where T : Exception
        {
            for (Exception? current = ex; current != null; current = current.InnerException)
                if (current is T match) return match;
            return null;
        }

        private const int _MaxJudgeReworkRounds = 2;

        private async Task<bool> TryScheduleJudgeReworkAsync(Mission judge, CancellationToken token, Action<string>? onUnavailable = null)
        {
            bool Unavailable(string reason) { onUnavailable?.Invoke(reason); return false; }
            if (String.IsNullOrEmpty(judge.VoyageId) || String.IsNullOrEmpty(judge.DependsOnMissionId))
                return Unavailable("Automatic Judge rework is unavailable because the review has no voyage or Worker dependency.");
            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(judge.VoyageId!, token).ConfigureAwait(false);
            Dictionary<string, Mission> byId = voyageMissions.ToDictionary(m => m.Id, StringComparer.Ordinal);

            // The Worker this Judge reviews, and how many rework rounds this chain has had.
            Mission? worker = null;
            int rounds = 0;
            bool escalated = false;
            HashSet<string> workerChain = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (Mission? cursor = judge; cursor != null && !String.IsNullOrEmpty(cursor.DependsOnMissionId) && seen.Add(cursor.Id);)
            {
                cursor = byId.TryGetValue(cursor.DependsOnMissionId!, out Mission? up) ? up : null;
                if (cursor == null) break;
                if (!PersonaCatalog.Matches(cursor.Persona, PersonaCatalog.Worker)) continue;
                if (worker == null) worker = cursor;
                workerChain.Add(cursor.Id);
                // A fresh Worker is the boundary of this implementation slice. Its dependency may be a
                // passing Judge from another Architect slice, whose rework budget is independent.
                if (!cursor.Title.StartsWith(_ReworkTitlePrefix, StringComparison.Ordinal)) break;
                rounds++;
                escalated |= cursor.Title.Contains(_EscalatedMarker, StringComparison.Ordinal);
            }

            if (worker == null) return Unavailable("Automatic Judge rework is unavailable because the reviewed Worker could not be found in the dependency chain.");

            // Give the Worker bounded rework, then try one different captain. Unchanged rejected work
            // switches earlier. Escalation also works in voyages with no planning stage.
            if (escalated || rounds > _MaxJudgeReworkRounds)
                return Unavailable("Automatic Judge rework limit reached after " + rounds + " rework round(s)" + (escalated ? ", including an escalated attempt." : "."));
            string? escalateTo = null;
            Mission? previousWorker = voyageMissions.FirstOrDefault(m => m.Id != worker.Id && PersonaCatalog.Matches(m.Persona, PersonaCatalog.Worker)
                && workerChain.Contains(m.Id) && !String.IsNullOrEmpty(m.CommitHash) && m.CommitHash == worker.CommitHash);
            bool stagnant = previousWorker != null && !MissionModeContract.IsReadOnly(worker.Mode);
            if (rounds == _MaxJudgeReworkRounds || stagnant)
            {
                string escalationTrigger = rounds == _MaxJudgeReworkRounds
                    ? "Automatic Judge rework budget exhausted for this slice (" + rounds + " of " + _MaxJudgeReworkRounds + " rounds). "
                    : "The Worker produced the same rejected commit after " + rounds + " rework round(s). ";
                if (await HasStrictCaptainOverrideAsync(worker, token).ConfigureAwait(false))
                    return Unavailable(escalationTrigger + "Strict captain selection prevents automatic escalation; no captain substitution was made.");
                Captain? alternative = await CaptainEscalationSelector.SelectAsync(_Database, worker, worker.CaptainId, voyageMissions, token).ConfigureAwait(false);
                if (alternative == null) return Unavailable(escalationTrigger + "No alternate eligible Worker captain is available for Judge-requested escalation. Check captain availability, scope, tier and persona priorities.");
                escalateTo = alternative.Id;
            }

            int round = rounds + 1;
            string baseTitle = worker.Title.Replace(" [Worker]", "", StringComparison.Ordinal);
            if (baseTitle.StartsWith(_ReworkTitlePrefix, StringComparison.Ordinal))
            {
                int colon = baseTitle.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0) baseTitle = baseTitle.Substring(colon + 2);
            }

            string taskSpec = StripPipelineHandoff(worker.Description);
            Mission rework = new Mission(_ReworkTitlePrefix + round + (escalateTo != null ? _EscalatedMarker : "") + ": " + baseTitle,
                taskSpec + "\n\n## Rework round " + round + " requested by the Judge\n"
                + "A reviewer checked this branch and asked for the changes in its review below (the previous stage output). "
                + "Resolve every reported finding, and check adjacent paths of the same feature against the original mission acceptance requirements. Reconcile findings from all preceding verifiers, including those labelled nonblocking: a reproducible violation of an explicit acceptance criterion or invariant remains blocking regardless of that label. For each unresolved finding, fix it with a regression or explain with evidence why it is outside this slice contract. "
                + "The findings are required corrections, not a replacement for the original scope or proof that unreported paths work. "
                + "Keep everything that is already right, add regression coverage that exercises the affected behavior, run the tests, and commit. "
                + "Report any original requirements that remain unverified, including publication and delivery. Do not start over or expand the mission scope.");
            rework.TenantId = judge.TenantId;
            rework.UserId = judge.UserId;
            rework.VoyageId = judge.VoyageId;
            rework.VesselId = judge.VesselId;
            rework.Persona = PersonaCatalog.Worker;
            rework.Tier = worker.Tier;
            rework.WaitForVoyageWorkers = worker.WaitForVoyageWorkers;
            rework.RequestedCaptainId = escalateTo;
            rework.DependsOnMissionId = judge.Id;
            rework.RequiresReview = false;
            rework = await _Database.Missions.CreateAsync(rework, token).ConfigureAwait(false);

            // Repeat the actual verification stages between this slice's Worker and Judge. Rework must
            // not silently downgrade FullPipeline to Reviewed. Use the original contract, not stale excerpts.
            List<Mission> verificationStages = new List<Mission>();
            Mission? prior = judge;
            HashSet<string> stageIds = new HashSet<string>(StringComparer.Ordinal);
            while (!String.IsNullOrEmpty(prior.DependsOnMissionId) && stageIds.Add(prior.Id)
                && byId.TryGetValue(prior.DependsOnMissionId, out Mission? preceding) && preceding.Id != worker.Id)
            {
                verificationStages.Add(preceding);
                prior = preceding;
            }
            verificationStages.Reverse();
            string reworkParentId = rework.Id;
            foreach (Mission original in verificationStages)
            {
                Mission repeated = new Mission("[" + original.Persona + "] Rework " + round + ": " + baseTitle, taskSpec);
                repeated.TenantId = original.TenantId;
                repeated.UserId = original.UserId;
                repeated.VoyageId = original.VoyageId;
                repeated.VesselId = original.VesselId;
                repeated.Persona = original.Persona;
                repeated.Mode = original.Mode;
                repeated.Tier = original.Tier;
                repeated.RequestedCaptainId = original.RequestedCaptainId;
                repeated.DependsOnMissionId = reworkParentId;
                repeated.RequiresReview = original.RequiresReview;
                repeated.ReviewDenyAction = original.ReviewDenyAction;
                repeated = await _Database.Missions.CreateAsync(repeated, token).ConfigureAwait(false);
                reworkParentId = repeated.Id;
            }

            Mission nextJudge = new Mission("[Judge] Review rework " + round + ": " + baseTitle, taskSpec);
            nextJudge.TenantId = judge.TenantId;
            nextJudge.UserId = judge.UserId;
            nextJudge.VoyageId = judge.VoyageId;
            nextJudge.VesselId = judge.VesselId;
            nextJudge.Persona = PersonaCatalog.Judge;
            nextJudge.Tier = judge.Tier;
            nextJudge.DependsOnMissionId = reworkParentId;
            nextJudge.RequiresReview = judge.RequiresReview;
            nextJudge.ReviewDenyAction = judge.ReviewDenyAction;
            nextJudge = await _Database.Missions.CreateAsync(nextJudge, token).ConfigureAwait(false);

            // The stages after this Judge (Recorder, landing) now follow the new Judge.
            foreach (Mission dependent in voyageMissions.Where(m => m.DependsOnMissionId == judge.Id))
            {
                dependent.DependsOnMissionId = nextJudge.Id;
                dependent.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(dependent, token).ConfigureAwait(false);
            }

            _Logging.Info(_Header + "judge " + judge.Id + ": rework round " + round + " as Worker mission " + rework.Id + ", re-review " + nextJudge.Id
                + (escalateTo != null ? "; escalating to " + escalateTo + (stagnant ? " after unchanged work" : " after the rework budget") : ""));
            return true;
        }

        private const string _ReworkTitlePrefix = "[Worker] Rework ";

        private const string _EscalatedMarker = " (escalated)";

        private static bool IsAncestorOf(Mission candidate, Mission descendant, Dictionary<string, Mission> byId)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (Mission? cursor = descendant; cursor != null && !String.IsNullOrEmpty(cursor.DependsOnMissionId) && seen.Add(cursor.Id);)
            {
                if (cursor.DependsOnMissionId == candidate.Id) return true;
                cursor = byId.TryGetValue(cursor.DependsOnMissionId!, out Mission? up) ? up : null;
            }

            return false;
        }

        private static string StripPipelineHandoff(string? description)
        {
            // A stage's description is the task plus what earlier stages handed it; keep the task.
            string text = description ?? String.Empty;
            int cut = text.IndexOf("\n\n---\n## Prior Stage Output", StringComparison.Ordinal);
            return cut > 0 ? text.Substring(0, cut).TrimEnd() : text;
        }

        /// <summary>
        /// The persona a mission is routed as: its own, or Worker for a mission dispatched without one (a plain dispatch
        /// with no pipeline, create_mission, a fleet action), so persona captain priorities and voyage captain
        /// assignments apply to it like any Worker stage.
        /// </summary>
        private static string RoutingPersona(Mission mission)
        {
            return String.IsNullOrWhiteSpace(mission.Persona) ? PersonaCatalog.Worker : mission.Persona!.Trim();
        }

        /// <inheritdoc />
        public async Task<string?> VerifyStageWorkAsync(Mission mission, Captain captain, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (captain.Runtime != AgentRuntimeEnum.Mux && captain.Runtime != AgentRuntimeEnum.ApiEndpoint) return null;
            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge) || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Architect)
                || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.ProductManager) || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Recorder))
                return null;
            if (String.IsNullOrEmpty(mission.DockId) || String.IsNullOrEmpty(mission.VesselId)) return null;

            try
            {
                Dock? dock = await _Database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);
                Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
                if (dock == null || vessel == null || String.IsNullOrEmpty(dock.WorktreePath)) return null;

                AuthContext system = new AuthContext { IsAuthenticated = true, IsAdmin = true, TenantId = vessel.TenantId };
                WorkflowProfile? profile = await new WorkflowProfileService(_Database, _Logging).ResolveForVesselAsync(system, vessel, null, token).ConfigureAwait(false);
                if (profile == null) return null;
                List<string> commands = new List<string>();
                if (!String.IsNullOrWhiteSpace(profile.BuildCommand)) commands.Add(profile.BuildCommand!.Trim());
                if (!String.IsNullOrWhiteSpace(profile.UnitTestCommand)) commands.Add(profile.UnitTestCommand!.Trim());
                if (commands.Count == 0) return null;

                DockHost? harborHost = HarborHostFor(dock);
                IHostCommandExecutor executor = harborHost != null ? harborHost.Commands : _LocalCommands;
                bool windows = OperatingSystem.IsWindows();
                if (harborHost != null && !String.IsNullOrEmpty(dock.HarborId))
                {
                    Harbor? harbor = await _Database.Harbors.ReadAsync(dock.HarborId!, token).ConfigureAwait(false);
                    windows = FleetActionShellCommandBuilder.IsWindowsPlatform(harbor?.OsPlatform);
                }

                foreach (string command in commands)
                {
                    HostCommandResult result = await executor.RunAsync(new HostCommandRequest
                    {
                        Executable = windows ? "cmd.exe" : "/bin/sh",
                        WorkingDirectory = dock.WorktreePath!,
                        Arguments = windows ? new List<string> { "/c", command } : new List<string> { "-lc", command },
                        TimeoutMs = (int)_StageVerificationTimeout.TotalMilliseconds,
                        Kind = HostCommandKindEnum.CheckRun,
                        Label = "Stage check: " + command
                    }, token).ConfigureAwait(false);
                    if (result.TimedOut)
                    {
                        _Logging.Warn(_Header + "stage check for mission " + mission.Id + " timed out running " + command);
                        return "The stage check timed out: " + command + ". Its success has not been verified. Investigate and rerun the check.";
                    }

                    if (result.ExitCode != 0)
                    {
                        string output = ((result.StandardOutput ?? "") + "\n" + (result.StandardError ?? "")).Trim();
                        if (output.Length > 3000) output = "...\n" + output.Substring(output.Length - 3000);
                        _Logging.Warn(_Header + "stage check for mission " + mission.Id + " failed: " + command + " exited " + result.ExitCode);
                        return "The stage check failed: `" + command + "` exited with code " + result.ExitCode + " in the working tree. Its output:\n```\n" + output + "\n```\nFix the cause, run the command again until it succeeds, and commit.";
                    }
                }

                _Logging.Info(_Header + "stage check for mission " + mission.Id + " passed (" + String.Join("; ", commands) + ")");
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
            {
                _Logging.Warn(_Header + "stage check for mission " + mission.Id + " could not run: " + ex.Message);
                return "The stage verification could not run: " + ex.Message + ". Restore dependencies and rerun the configured checks.";
            }
        }

        private static readonly TimeSpan _StageVerificationTimeout = TimeSpan.FromMinutes(15);

        /// <inheritdoc />
        public Task CommitDockWorkAsync(Mission mission, Captain captain, Dock dock, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (dock == null) throw new ArgumentNullException(nameof(dock));
            return CommitLeftoverWorkAsync(mission, captain, dock, token);
        }

        private async Task CommitLeftoverWorkAsync(Mission mission, Captain captain, Dock dock, CancellationToken token)
        {
            // A judge reports through its verdict and an architect through its plan: neither should change the branch.
            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge) || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Architect)) return;
            if (String.IsNullOrEmpty(dock.WorktreePath)) return;

            DockHost? harborHost = HarborHostFor(dock);
            IGitService? git = harborHost != null ? harborHost.Git : _Git;
            if (git == null) return;

            try
            {
                if (!await DockDirectoryExistsAsync(harborHost, dock.WorktreePath!, token).ConfigureAwait(false)) return;

                // The rescue is for a captain that never commits. A captain that committed its work this attempt has said
                // what belongs on the branch; what it left behind is typically build output (regenerated XML docs, lock
                // files), and committing that breaks the mission's file scope and sends the work back to rework.
                string? startCommit = TryReadDockStartCommit(dock.Id);
                string? headCommit = await git.GetHeadCommitHashAsync(dock.WorktreePath!, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(startCommit) && !String.IsNullOrEmpty(headCommit)
                    && !String.Equals(startCommit, headCommit, StringComparison.OrdinalIgnoreCase))
                {
                    _Logging.Info(_Header + "mission " + mission.Id + ": captain " + captain.Id + " committed its work; leaving any uncommitted changes in dock " + dock.Id + " (typically build output) off the branch");
                    return;
                }

                string message = "Armada: commit work left uncommitted by " + captain.Name + "\n\n"
                    + mission.Title + "\n\nMission " + mission.Id + (String.IsNullOrEmpty(mission.Persona) ? "" : " (" + mission.Persona + ")")
                    + ". The captain finished without committing these changes, so Armada committed them.";
                if (await git.CommitAllAsync(dock.WorktreePath!, message, token).ConfigureAwait(false))
                    _Logging.Info(_Header + "mission " + mission.Id + ": captain " + captain.Id + " left uncommitted changes in dock " + dock.Id + "; committed them");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is IOException)
            {
                _Logging.Warn(_Header + "mission " + mission.Id + ": could not commit leftover changes in dock " + dock.Id + ": " + ex.Message);
            }
        }

        private DockHost? HarborHostFor(Dock? dock)
        {
            if (DockHosts == null || !DockHostResolver.IsHarborDock(dock)) return null;
            return DockHosts.ForDock(dock);
        }

        /// <summary>
        /// Whether a dock's worktree folder exists on its host. A Harbor that is offline or does not answer counts as
        /// "not there", so callers fall back to what the Admiral already has (the diff snapshot).
        /// </summary>
        private async Task<bool> DockDirectoryExistsAsync(DockHost? harborHost, string path, CancellationToken token)
        {
            if (harborHost == null) return Directory.Exists(path);
            try
            {
                return await harborHost.Files.DirectoryExistsAsync(path, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is IOException)
            {
                _Logging.Debug(_Header + "could not check dock " + path + " on Harbor " + harborHost.HarborId + ": " + ex.Message);
                return false;
            }
        }

        private async Task HideTrackedInstructionsFileAsync(IHostCommandExecutor commands, string worktreePath, string instructionsFileName, CancellationToken token)
        {
            // git ignores exclude rules for files it already tracks. When the repository commits its own
            // instruction file (for example CLAUDE.md), mark it skip-worktree in this dock so the generated
            // mission instructions can never be staged or committed by the captain.
            // ls-files --error-unmatch exits 1 for a file git does not track: an answer, not a failure.
            int tracked = await RunGitExitCodeAsync(commands, worktreePath, new int[] { 1 }, token, "ls-files", "--error-unmatch", "--", instructionsFileName).ConfigureAwait(false);
            if (tracked != 0) return;

            int marked = await RunGitExitCodeAsync(commands, worktreePath, null, token, "update-index", "--skip-worktree", "--", instructionsFileName).ConfigureAwait(false);
            if (marked != 0)
            {
                _Logging.Warn(_Header + "could not mark " + instructionsFileName + " skip-worktree in " + worktreePath + " (git exit code " + marked + ")");
            }
        }

        private static async Task<int> RunGitExitCodeAsync(IHostCommandExecutor commands, string workingDirectory, int[]? expectedExitCodes, CancellationToken token, params string[] args)
        {
            HostCommandResult result = await commands.RunAsync(new HostCommandRequest
            {
                Executable = "git",
                WorkingDirectory = workingDirectory,
                Arguments = new List<string>(args),
                TimeoutMs = 30000,
                ExpectedExitCodes = expectedExitCodes != null ? new List<int>(expectedExitCodes) : new List<int>()
            }, token).ConfigureAwait(false);
            return result.ExitCode;
        }

        #endregion
    }
}
