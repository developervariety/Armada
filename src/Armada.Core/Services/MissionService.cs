namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
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

        /// <inheritdoc />
        public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }

        /// <inheritdoc />
        public Func<string, string?>? OnGetMissionOutput { get; set; }

        /// <inheritdoc />
        public Func<Mission, bool, Task>? OnMissionOutcome { get; set; }

        /// <summary>
        /// Optional callback invoked when a mission enters an approval-required review state.
        /// </summary>
        public Action<Mission>? OnReviewRequested { get; set; }

        /// <summary>
        /// Optional definition-of-done gate that runs in-dock build and unit-test commands
        /// before a Worker mission is accepted as complete. When set, the gate is evaluated
        /// after diff capture and before handoff/landing. A failing gate sets the mission to
        /// Failed and prevents landing.
        /// </summary>
        public DefinitionOfDoneGate? DefinitionOfDone
        {
            get => _DefinitionOfDoneGate;
            set => _DefinitionOfDoneGate = value;
        }

        #endregion

        #region Private-Members

        private string _Header = "[MissionService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService? _Git;
        private IDockService _Docks;
        private ICaptainService _Captains;
        private ICaptainQuarantineService _CaptainQuarantine;
        private IResourcePressureAdmission _ResourcePressureAdmission;
        private IPromptTemplateService? _PromptTemplates;
        private PrestagedFileCopier _Prestaging;
        private DefinitionOfDoneGate? _DefinitionOfDoneGate;
        private const string _CreditAuthQuarantineReason =
            "Provider credit, billing, payment, or authentication failure detected during mission execution.";
        private const string ArchitectHandoffMarker = "<!-- ARMADA:ARCHITECT-HANDOFF -->";
        private const string ReviewFeedbackMarker = "<!-- ARMADA:REVIEW-FEEDBACK -->";
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
        /// Tracks in-flight mission complete handler operations by mission ID.
        /// </summary>
        private System.Collections.Concurrent.ConcurrentDictionary<string, Task> _InFlightCompletions = new System.Collections.Concurrent.ConcurrentDictionary<string, Task>();

        /// <summary>
        /// Test-only accessor for the in-flight completion gate. Exposed so a unit test
        /// can simulate a sweep tick landing inside the DoD gate window without having
        /// to drive a real completion handler.
        /// </summary>
        internal System.Collections.Concurrent.ConcurrentDictionary<string, Task> InFlightCompletionsForTests => _InFlightCompletions;

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
            /// Optional dependency reference emitted by the architect.
            /// </summary>
            public string? DependsOnReference { get; set; } = null;
        }

        /// <summary>
        /// Verdict extracted from a judge mission's output.
        /// </summary>
        private enum JudgeVerdict
        {
            None,
            Pass,
            Fail,
            NeedsRevision
        }

        /// <summary>
        /// Maximum number of times a Judge mission that exits without an explicit verdict line is
        /// re-run in place before it is allowed to settle into a terminal failure. Bounds the
        /// spurious-failure recovery so an operationally dropped verdict (for example a backgrounded
        /// test run that terminated before the standalone verdict line) is retried rather than
        /// burning the auto-rescue budget on the first miss.
        /// </summary>
        private const int _MaxMissingJudgeVerdictRetries = 2;

        // Character budget for the code-retrieval goal quoted in mission instructions. Long enough to
        // carry the title and opening intent, short enough that the brief is not repeated wholesale.
        private const int _MaxCodeRetrievalGoalLength = 300;

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
        /// <param name="captainQuarantine">Optional captain quarantine service.</param>
        /// <param name="resourcePressureAdmission">Optional resource-pressure admission policy applied before launch.</param>
        public MissionService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IDockService docks,
            ICaptainService captains,
            IPromptTemplateService? promptTemplates = null,
            IGitService? git = null,
            ICaptainQuarantineService? captainQuarantine = null,
            IResourcePressureAdmission? resourcePressureAdmission = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git;
            _Docks = docks ?? throw new ArgumentNullException(nameof(docks));
            _Captains = captains ?? throw new ArgumentNullException(nameof(captains));
            _CaptainQuarantine = captainQuarantine ?? new CaptainQuarantineService(_Database, _Settings, _Logging);
            _ResourcePressureAdmission = resourcePressureAdmission
                ?? new ResourcePressureAdmission(_Settings.ResourcePressureAdmission, new HostResourcePressureProbe(), _Logging);
            _PromptTemplates = promptTemplates;
            _Prestaging = new PrestagedFileCopier(_Logging);
        }

        #endregion

        #region Public-Methods

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

                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "mission " + mission.Id + " belongs to terminal voyage " + voyage.Id +
                            " (" + voyage.Status + ") -- cancelling instead of assigning");
                        return false;
                    }
                }

                // A vessel whose bare-repo LocalPath is the SAME directory as the operator's
                // WorkingDirectory cannot be provisioned safely: dock creation would operate inside
                // the working checkout. Refuse the assignment and leave the mission Pending so the
                // misconfiguration is fixed rather than silently destroying the checkout.
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
            // dependencyIsCrossVessel is captured here and consumed by branch-inheritance logic
            // below: cross-vessel deps cannot share a branch (different repos), so the downstream
            // mission must always start on a fresh branch in its own vessel.
            bool dependencyIsCrossVessel = false;
            if (!String.IsNullOrEmpty(mission.DependsOnMissionId))
            {
                Mission? dependency = await _Database.Missions.ReadAsync(mission.DependsOnMissionId, token).ConfigureAwait(false);
                if (dependency == null)
                {
                    _Logging.Warn(_Header + "mission " + mission.Id + " depends on " + mission.DependsOnMissionId + " which was not found -- skipping assignment");
                    mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                    return false;
                }

                dependencyIsCrossVessel = !String.IsNullOrEmpty(dependency.VesselId)
                    && !String.IsNullOrEmpty(mission.VesselId)
                    && !String.Equals(dependency.VesselId, mission.VesselId, StringComparison.Ordinal);

                if (!IsDependencySatisfyingStatus(dependency.Status))
                {
                    // Dependency not yet satisfied -- don't assign. PullRequestOpen unblocks
                    // dependents per the breaking-change PR-fallback design: the captain
                    // branch is finalized + push at PR-open time, so downstreams can
                    // continue chaining off it without waiting for the PR to merge.
                    mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                    return false;
                }

                // Parallel-stage barrier. Same-order stages dispatch as siblings sharing one upstream
                // dependency, but DependsOnMissionId names only ONE of them -- so without this the next
                // order would start as soon as that single sibling finished while the rest of its group
                // was still running, letting a Judge review a diff its sibling reviewers had not
                // finished contributing to. The group is keyed on StageOrder, not on the shared parent
                // alone: Architect fan-out clones whole downstream chains whose stages also share a
                // parent but must run independently, and keying on the parent alone deadlocks them.
                if (!await DependencyGroupSatisfiedAsync(mission, dependency, token).ConfigureAwait(false))
                {
                    mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " +
                        mission.AssignmentState + " (parallel sibling stages still running)");
                    return false;
                }

                // Cross-vessel deps must wait until the upstream lands (Complete). A cross-vessel
                // dep in WorkProduced means the captain finished work but the merge queue hasn't
                // yet pushed to the upstream's master, so the downstream's vessel can't see those
                // changes via git anyway. The same-vessel handoff path (branch sharing) does not
                // apply across repos.
                if (dependencyIsCrossVessel && dependency.Status == MissionStatusEnum.WorkProduced)
                {
                    _Logging.Info(_Header + "mission " + mission.Id + " depends on " + dependency.Id +
                        " on a different vessel (" + dependency.VesselId + ") which is still WorkProduced -- waiting for Complete before assigning");
                    mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                    return false;
                }

                // Same-vessel deps in a handoff-eligible status (WorkProduced/Complete/PullRequestOpen,
                // per the gate above) still require the explicit handoff (branch + prior-stage context)
                // so the downstream stage doesn't launch with the original dispatch prompt before
                // architect/test/judge prep runs.
                if (!dependencyIsCrossVessel && !IsPipelineHandoffPrepared(mission, dependency))
                {
                    if (String.Equals(dependency.Persona, "Architect", StringComparison.OrdinalIgnoreCase))
                    {
                        // The Architect handoff is the special parse-and-materialize path
                        // (TryHandoffToNextStageAsync turns architect output into downstream mission
                        // rows); it cannot be reconstructed lazily here. Keep deferring until that
                        // batch handoff runs.
                        _Logging.Info(_Header + "mission " + mission.Id + " depends on architect " + dependency.Id +
                            " whose handoff is not prepared yet -- deferring assignment");
                        mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                        return false;
                    }

                    // The upstream reached a handoff-eligible status but the handoff context was never
                    // propagated onto this dependent -- e.g. a rescue dependent row created after the
                    // upstream had already transitioned to WorkProduced (the creation-order race).
                    // Rather than parking at WaitingForDependency forever, lazily run the handoff for
                    // this single dependent now (stamp the upstream branch + inject the prior-stage
                    // preamble/context) and continue assignment in the same pass. Idempotent: once the
                    // branch is stamped, IsPipelineHandoffPrepared returns true on later passes.
                    //
                    // Defer when the upstream's completion is currently inside the in-flight completion
                    // gate -- the batch handoff that runs AFTER the DoD gate will materialise the
                    // dependent rows from the same context, so self-healing here races the batch and
                    // either appends a redundant prepare or, worse, drops a mailbox block the batch
                    // has not yet consumed. Try again on the next sweep tick once the completion
                    // handler clears its entry.
                    if (_InFlightCompletions.ContainsKey(dependency.Id))
                    {
                        _Logging.Info(_Header + "mission " + mission.Id + " depends on " + dependency.Id +
                            " (" + dependency.Status + ") whose completion is still in flight -- deferring self-heal to next sweep tick");
                        mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                        return false;
                    }

                    _Logging.Info(_Header + "mission " + mission.Id + " depends on " + dependency.Id +
                        " (" + dependency.Status + ") but handoff context was not propagated -- self-healing handoff before assignment");
                    await SelfHealDependentHandoffAsync(dependency, mission, token).ConfigureAwait(false);
                }
            }

            if (await ShouldDeferArchitectSequencedMissionAsync(mission, token).ConfigureAwait(false))
            {
                _Logging.Info(_Header + "mission " + mission.Id +
                    " is architect-marked as sequential after implementation work -- deferring assignment");
                mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                return false;
            }

            // Check for vessel-level lock (broad-scope missions block new assignments).
            // Use lightweight summaries (id/title/status only) to avoid hydrating large
            // description, diff_snapshot, and agent_output columns for every pending check.
            List<ActiveMissionSummary> activeSummaries = await _Database.Missions.GetActiveVesselSummariesAsync(vessel.Id, token).ConfigureAwait(false);
            List<ActiveMissionSummary> broadMissions = activeSummaries.Where(m => IsBroadScope(m)).ToList();

            if (broadMissions.Count > 0)
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has a broad-scope mission in progress -- deferring assignment of " + mission.Id);
                mission.AssignmentState = MissionAssignmentStateEnum.WaitingForVesselMutex;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                return false;
            }

            // Check if this mission is broad-scope and vessel already has active work.
            // activeSummaries already contains only Assigned/InProgress missions.
            int concurrentCount = activeSummaries.Count;

            if (IsBroadScope(mission) && concurrentCount > 0)
            {
                _Logging.Warn(_Header + "broad-scope mission " + mission.Id + " deferred -- vessel " + vessel.Id + " has " + concurrentCount + " active mission(s)");
                mission.AssignmentState = MissionAssignmentStateEnum.WaitingForVesselMutex;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                return false;
            }

            // Enforce per-vessel serialization unless explicitly allowed
            if (!vessel.AllowConcurrentMissions && concurrentCount > 0)
            {
                _Logging.Info(_Header + "vessel " + vessel.Id + " already has " + concurrentCount + " active mission(s); deferring " + mission.Id + " (AllowConcurrentMissions=false)");
                mission.AssignmentState = MissionAssignmentStateEnum.WaitingForVesselMutex;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                return false;
            }

            // Warn about concurrent missions on same vessel when allowed
            if (vessel.AllowConcurrentMissions && concurrentCount > 0)
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " already has " + concurrentCount + " active mission(s) -- potential for conflicts (AllowConcurrentMissions=true)");
            }

            // Both admission controls use the global active workload count. A per-vessel count
            // misses simultaneous compiler and agent pressure from other repositories.
            Dictionary<MissionStatusEnum, int> statusCounts =
                await _Database.Missions.CountByStatusAsync(token).ConfigureAwait(false);
            int globalActive = 0;
            if (statusCounts != null)
            {
                if (statusCounts.TryGetValue(MissionStatusEnum.Assigned, out int assignedCount))
                    globalActive += assignedCount;
                if (statusCounts.TryGetValue(MissionStatusEnum.InProgress, out int inProgressCount))
                    globalActive += inProgressCount;
            }

            string? deferralReason = null;
            if (_Settings.MaxConcurrentCaptainWorkloads > 0 &&
                globalActive >= _Settings.MaxConcurrentCaptainWorkloads)
            {
                deferralReason = globalActive + " active captain workload(s) reached global limit "
                    + _Settings.MaxConcurrentCaptainWorkloads + " (MaxConcurrentCaptainWorkloads).";
            }
            else
            {
                ResourcePressureDecision admission = _ResourcePressureAdmission.Evaluate(globalActive);
                if (!admission.Admit)
                    deferralReason = admission.Reason;
            }

            if (!String.IsNullOrEmpty(deferralReason))
            {
                _Logging.Warn(_Header + "resource-pressure admission deferring mission " + mission.Id
                    + " on vessel " + vessel.Id + ": " + deferralReason);
                mission.AssignmentState = MissionAssignmentStateEnum.WaitingForResourcePressure;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                return false;
            }

            // Find an idle captain, preferring those matching the mission's persona,
            // honouring optional PreferredModel pin on the mission.
            Captain? captain = await FindAvailableCaptainAsync(mission, token).ConfigureAwait(false);
            if (captain == null)
            {
                _Logging.Warn(_Header + "no idle captains available for mission " + mission.Id +
                    (mission.Persona != null ? " (persona: " + mission.Persona + ")" : "") +
                    (!String.IsNullOrEmpty(mission.PreferredModel) ? " (preferredModel: " + mission.PreferredModel + ")" : ""));
                mission.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                return false;
            }

            // Downstream pipeline stages continue on the upstream branch prepared during handoff.
            // Standalone missions still get a fresh captain/mission branch. Cross-vessel deps
            // (different repos) cannot share a branch, so the downstream mission always starts
            // on a fresh branch in its own vessel even when DependsOnMissionId is populated.
            bool preserveInheritedBranch = !String.IsNullOrEmpty(mission.DependsOnMissionId)
                && !String.IsNullOrEmpty(mission.BranchName)
                && !dependencyIsCrossVessel;
            string branchName = preserveInheritedBranch
                ? mission.BranchName!
                : BuildMissionBranchName(captain, mission);
            mission.BranchName = branchName;
            mission.CaptainId = captain.Id;
            mission.Status = MissionStatusEnum.Assigned;
            mission.AssignmentState = MissionAssignmentStateEnum.Provisioning;
            mission.LastUpdateUtc = DateTime.UtcNow;

            // Persist Provisioning before the dock call. Provisioning can take seconds (worktree
            // creation, sibling checkouts), and without this write the mission row kept advertising
            // its pre-assignment state for that whole window -- so neither an operator nor the
            // assignment-state tests could observe Provisioning while it was actually happening.
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            // Provision dock (worktree) and launch agent
            Dock? dock;
            try
            {
                _Logging.Info(_Header + "provisioning dock for mission " + mission.Id + " on vessel " + vessel.Id + " with captain " + captain.Id);
                dock = await _Docks.ProvisionAsync(
                    vessel,
                    captain,
                    branchName,
                    mission.Id,
                    detachedWorktree: !PersonaRequiresBranchAttachment(mission.Persona),
                    token: token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "dock provisioning threw for mission " + mission.Id + " vessel " + vessel.Id + " captain " + captain.Id + ": " + ex.Message);

                // Revert mission to Pending; mark assignment as Failed for operator visibility
                mission.AssignmentState = MissionAssignmentStateEnum.Failed;
                mission.Status = MissionStatusEnum.Pending;
                mission.CaptainId = null;
                if (!preserveInheritedBranch)
                    if (!preserveInheritedBranch)
                        mission.BranchName = null;
                mission.DockId = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);

                // Release captain back to Idle
                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);

                return false;
            }

            if (dock == null)
            {
                // Provisioning failed - revert mission assignment; mark as Failed for operator visibility
                _Logging.Warn(_Header + "dock provisioning failed for captain " + captain.Id + " vessel " + vessel.Id + " mission " + mission.Id + " -- reverting to Pending");
                mission.AssignmentState = MissionAssignmentStateEnum.Failed;
                mission.Status = MissionStatusEnum.Pending;
                mission.CaptainId = null;
                if (!preserveInheritedBranch)
                    mission.BranchName = null;
                mission.DockId = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
                return false;
            }

            try
            {
                mission.DockId = dock.Id;
                await _Database.ExecuteInTransactionAsync(async () =>
                {
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                    bool claimed = await _Database.Captains.TryClaimAsync(captain.Id, mission.Id, dock.Id, token).ConfigureAwait(false);
                    if (!claimed)
                    {
                        throw new InvalidOperationException("Captain " + captain.Id + " was claimed by another mission.");
                    }
                }, token).ConfigureAwait(false);
                _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to commit assignment for mission " + mission.Id + ": " + ex.Message);

                mission.Status = MissionStatusEnum.Pending;
                mission.CaptainId = null;
                if (!preserveInheritedBranch)
                    mission.BranchName = null;
                mission.DockId = null;
                mission.LastUpdateUtc = DateTime.UtcNow;

                try
                {
                    await _Docks.ReclaimAsync(dock.Id, token: token).ConfigureAwait(false);
                    await _Database.Docks.DeleteAsync(dock.Id, token).ConfigureAwait(false);
                }
                catch (Exception reclaimEx)
                {
                    _Logging.Warn(_Header + "failed to reclaim dock " + dock.Id +
                        " after assignment commit failure for mission " + mission.Id + ": " + reclaimEx.Message);
                }

                return false;
            }

            // Stage any prestaged files into the worktree before the captain is launched.
            // The validator already ran at dispatch time; this is the host-side copy step.
            // Failures here mark the mission Failed and reclaim the dock without ever
            // claiming a captain or launching an agent process.
            if (mission.PrestagedFiles != null && mission.PrestagedFiles.Count > 0)
            {
                string? prestageFailure = _Prestaging.CopyAll(mission.PrestagedFiles, dock.WorktreePath ?? "");
                if (prestageFailure != null)
                {
                    _Logging.Error(_Header + "prestaging failed for mission " + mission.Id + ": " + prestageFailure);

                    mission.Status = MissionStatusEnum.Failed;
                    mission.FailureReason = "Prestaged file staging failed: " + prestageFailure;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                    try
                    {
                        await _Docks.ReclaimAsync(dock.Id, token: token).ConfigureAwait(false);
                    }
                    catch (Exception reclaimEx)
                    {
                        _Logging.Warn(_Header + "failed to reclaim dock " + dock.Id +
                            " after prestaging failure for mission " + mission.Id + ": " + reclaimEx.Message);
                    }

                    return false;
                }
            }

            // Refresh in-memory captain state to match the atomic update
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            captain.LastHeartbeatUtc = DateTime.UtcNow;
            captain.LastUpdateUtc = DateTime.UtcNow;

            // Create assignment signal
            Signal signal = new Signal(SignalTypeEnum.Assignment, mission.Title);
            signal.TenantId = mission.TenantId;
            signal.UserId = mission.UserId;
            signal.ToCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            // Generate runtime mission instructions into the worktree.
            await GenerateClaudeMdAsync(dock.WorktreePath!, mission, vessel, captain, token).ConfigureAwait(false);
            await EnsureMissionInstructionsPresentAsync(dock.WorktreePath!, mission, captain, token).ConfigureAwait(false);

            // Launch agent process via captain service
            if (_Captains.OnLaunchAgent != null)
            {
                try
                {
                    int processId = await _Captains.OnLaunchAgent.Invoke(captain, mission, dock).ConfigureAwait(false);
                    captain.ProcessId = processId;
                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

                    mission.ProcessId = processId;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.StartedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);

                    _Logging.Info(_Header + "launched agent process " + processId + " for captain " + captain.Id);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "failed to launch agent for captain " + captain.Id + ": " + ex.Message);

                    // Rollback captain state - release back to idle so it can accept future work
                    await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);

                    // Rollback mission state - revert to Pending for re-dispatch; mark Failed for operator visibility
                    mission.AssignmentState = MissionAssignmentStateEnum.Failed;
                    mission.Status = MissionStatusEnum.Pending;
                    mission.CaptainId = null;
                    mission.BranchName = null;
                    mission.DockId = null;
                    mission.ProcessId = null;
                    mission.StartedUtc = null;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "mission " + mission.Id + " assignment state -> " + mission.AssignmentState);

                    try
                    {
                        await _Docks.ReclaimAsync(dock.Id, token: token).ConfigureAwait(false);
                    }
                    catch (Exception reclaimEx)
                    {
                        _Logging.Warn(_Header + "failed to reclaim dock " + dock.Id +
                            " after launch failure for mission " + mission.Id + ": " + reclaimEx.Message);
                    }

                    Signal errorSignal = new Signal(SignalTypeEnum.Error, "Failed to launch agent: " + ex.Message);
                    errorSignal.TenantId = mission.TenantId;
                    errorSignal.UserId = mission.UserId;
                    errorSignal.FromCaptainId = captain.Id;
                    await _Database.Signals.CreateAsync(errorSignal, token).ConfigureAwait(false);

                    return false;
                }
            }
            else
            {
                // No launch handler configured - rollback assignment
                _Logging.Warn(_Header + "no OnLaunchAgent handler configured - cannot launch agent for captain " + captain.Id);

                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);

                mission.Status = MissionStatusEnum.Pending;
                mission.CaptainId = null;
                if (!preserveInheritedBranch)
                    mission.BranchName = null;
                mission.DockId = null;
                mission.ProcessId = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                try
                {
                    await _Docks.ReclaimAsync(dock.Id, token: token).ConfigureAwait(false);
                }
                catch (Exception reclaimEx)
                {
                    _Logging.Warn(_Header + "failed to reclaim dock " + dock.Id +
                        " after missing launch handler for mission " + mission.Id + ": " + reclaimEx.Message);
                }

                return false;
            }

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

            // In-flight deduplication: ensure only one completion handler runs per mission.
            // Both the process exit callback and the health check can trigger completion
            // concurrently for the same mission. TryAdd returns false if another caller
            // is already processing this mission.
            TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>();
            if (!_InFlightCompletions.TryAdd(missionId, gate.Task))
            {
                _Logging.Debug(_Header + "mission " + missionId + " completion already in flight -- skipping duplicate");
                return;
            }

            try
            {
                await HandleCompletionCoreAsync(captain, missionId, token).ConfigureAwait(false);
            }
            finally
            {
                gate.TrySetResult(true);
                // Remove after a delay so late-arriving duplicate calls still see the entry
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    _InFlightCompletions.TryRemove(missionId, out _);
                });
            }
        }

        /// <inheritdoc />
        public async Task<Mission> ApproveReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            Mission mission = await RequireReviewMissionAsync(missionId, token).ConfigureAwait(false);
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
        public async Task<Mission> DenyReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            Mission mission = await RequireReviewMissionAsync(missionId, token).ConfigureAwait(false);
            Dock? dock = await ReadMissionDockAsync(mission, token).ConfigureAwait(false);
            string reviewComment = NormalizeReviewComment(comment) ?? "Review denied. Rework is required before this stage can continue.";

            mission.ReviewComment = reviewComment;
            mission.ReviewedByUserId = reviewedByUserId;
            mission.ReviewedUtc = DateTime.UtcNow;

            if (dock != null)
            {
                await ReclaimMissionDockAsync(dock.Id, token).ConfigureAwait(false);
            }

            if (mission.ReviewDenyAction == ReviewDenyActionEnum.FailPipeline)
            {
                mission.Status = MissionStatusEnum.Failed;
                mission.FailureReason = BuildReviewDeniedFailureReason(reviewComment);
                mission.CompletedUtc = DateTime.UtcNow;
                mission.ProcessId = null;
                mission.DockId = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
                await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);

                // A FailPipeline review denial is a real terminal transition that never flows through
                // the HandleCompletionCoreAsync reap block, so reap its captain branch here too.
                await ReapTerminalMissionBranchAsync(mission, token).ConfigureAwait(false);
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
        /// Detect a captain "false complete" event: the captain emitted
        /// [ARMADA:RESULT] COMPLETE after running briefly with no commits and
        /// no real tool activity beyond reading the brief. This pattern shows up
        /// most often on the GLM 5.2 / Zyloo provider where the captain reads
        /// AGENTS.md, emits COMPLETE, and exits cleanly without doing the work.
        /// Without detection the mission transitions to WorkProduced and the
        /// pipeline downstream accepts an empty diff as real progress.
        /// </summary>
        internal static bool DetectNoOpCompletion(Mission mission, TimeSpan runtime, int diffLineCount, int agentOutputLength, bool hasAgentOutput)
        {
            if (mission == null) return false;
            // Read-only missions (Audit/Research) are exempt: a correct audit deliverable
            // can legitimately have no diff.
            if (!(mission.Mode == MissionModeEnum.Implementation)) return false;
            // A real captain writes a result line and an AgentOutput body. A unit-test
            // stub does not set AgentOutput at all. The presence of an AgentOutput is
            // the signal that a real captain ran (even briefly). Without that signal we
            // cannot distinguish a stub from a false-complete.
            if (!hasAgentOutput) return false;
            // Captured diff must be empty.
            if (diffLineCount > 0) return false;
            // Captain ran in less than the threshold (typical false-complete 8-30s).
            // A real Implementation mission always takes longer than that.
            const int noOpMaxSeconds = 60;
            if (runtime.TotalSeconds >= noOpMaxSeconds) return false;
            // AgentOutput should hold at least the [ARMADA:RESULT] COMPLETE line plus a
            // small summary. False-complete outputs are ~113 chars; legitimate summaries
            // are 200+ chars and describe the change.
            if (agentOutputLength >= 200) return false;
            return true;
        }

        internal static string BuildNoOpCompletionFailureReason(TimeSpan runtime, int agentOutputLength)
        {
            return "no_op_completion_detected: captain exited with [ARMADA:RESULT] COMPLETE after "
                + Math.Round(runtime.TotalSeconds, 1)
                + "s with an empty diff and "
                + agentOutputLength
                + " chars of AgentOutput. This is the false-complete pattern (typical for GLM 5.2 / Zyloo when the captain reads the brief and exits without working). The mission is re-queued rather than marked WorkProduced so the rescue path can retry with a different captain.";
        }

        /// <summary>
        /// Core completion logic, called under in-flight deduplication guard.
        /// </summary>
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

                // A caller may have persisted terminal Failed/Cancelled state BEFORE invoking the
                // completion handler (e.g. a process-exit failure path), so the earlier reap block
                // below never runs for it. Reap here as well -- the helper no-ops for any non-terminal
                // or non-Failed/Cancelled status (WorkProduced, Complete, LandingFailed, PullRequestOpen).
                await ReapTerminalMissionBranchAsync(mission, token).ConfigureAwait(false);
                return;
            }

            // Mark mission as work produced (agent finished, landing not yet attempted)
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.ProcessId = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
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
                _Logging.Info(_Header + "backfilled branch " + dock.BranchName + " onto mission " + mission.Id +
                    " from dock " + dock.Id + " before pipeline handoff");
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
                    _Logging.Warn(_Header + "error capturing diff for mission " + mission.Id + ": " + ex.Message);
                }
            }

            bool failedForScopeViolation = false;
            if (dock != null)
            {
                failedForScopeViolation = await TryFailMissionForScopeViolationAsync(mission, dock, token).ConfigureAwait(false);
            }

            // Capture accumulated agent stdout output before pipeline handoff.
            // MUST run before no-op completion detection, because that check
            // reads mission.AgentOutput which is populated from the runtime
            // output buffer. Without this ordering, AgentOutput is null and
            // DetectNoOpCompletion cannot distinguish a false-complete from a
            // legitimate short mission.
            if (OnGetMissionOutput != null)
            {
                string? agentOutput = OnGetMissionOutput(mission.Id);
                if (!String.IsNullOrEmpty(agentOutput))
                {
                    mission.AgentOutput = agentOutput;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                }
            }

            // Detect "false complete": a captain that emits [ARMADA:RESULT] COMPLETE after
            // running briefly with an empty diff and a tiny AgentOutput. The captain-side
            // fix is not always available (provider-side behavior), so the platform has
            // to catch it. A no-op completion that reaches WorkProduced corrupts the
            // downstream pipeline with empty progress and breaks rescue judgment.
            bool failedForNoOpCompletion = false;
            if (!failedForScopeViolation && mission.StartedUtc.HasValue)
            {
                TimeSpan runtime = (mission.CompletedUtc ?? DateTime.UtcNow) - mission.StartedUtc.Value;
                int diffLineCount = String.IsNullOrEmpty(mission.DiffSnapshot)
                    ? 0
                    : mission.DiffSnapshot.Split('\n').Length;
                int agentOutputLength = mission.AgentOutput?.Length ?? 0;
                bool hasAgentOutput = !String.IsNullOrEmpty(mission.AgentOutput);
                if (DetectNoOpCompletion(mission, runtime, diffLineCount, agentOutputLength, hasAgentOutput))
                {
                    failedForNoOpCompletion = true;
                    mission.Status = MissionStatusEnum.Failed;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission.FailureReason = BuildNoOpCompletionFailureReason(runtime, agentOutputLength);
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    await AppendMissionActivityAsync(mission.Id, "validation failed: " + mission.FailureReason, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "mission " + mission.Id + " failed no-op-completion check runtime="
                        + Math.Round(runtime.TotalSeconds, 1) + "s diffLines=" + diffLineCount + " agentOutputLen=" + agentOutputLength);
                }
            }

            // Definition-of-done gate: run in-dock build and unit-test before accepting Worker work.
            bool failedForDodGate = false;
            if (!failedForScopeViolation && !failedForNoOpCompletion && dock != null && _DefinitionOfDoneGate != null)
            {
                try
                {
                    await AppendMissionActivityAsync(mission.Id, "validation started: definition-of-done gate", token).ConfigureAwait(false);
                    DefinitionOfDoneResult dodResult = await _DefinitionOfDoneGate.EvaluateAsync(mission, dock, token).ConfigureAwait(false);
                    if (!dodResult.Passed && String.IsNullOrEmpty(dodResult.SkippedReason))
                    {
                        failedForDodGate = true;
                        mission.Status = MissionStatusEnum.Failed;
                        mission.CompletedUtc = DateTime.UtcNow;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        mission.FailureReason = BuildDodFailureReason(dodResult);
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        await AppendMissionActivityAsync(mission.Id, "validation failed: " + mission.FailureReason, token).ConfigureAwait(false);
                        _Logging.Warn(_Header + "mission " + mission.Id + " failed DoD gate classification=" +
                            dodResult.FailureClass);
                    }
                    else if (dodResult.Passed)
                    {
                        await AppendMissionActivityAsync(mission.Id, "validation passed: definition-of-done gate", token).ConfigureAwait(false);
                    }
                    else
                    {
                        await AppendMissionActivityAsync(mission.Id, "validation skipped: " + dodResult.SkippedReason, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception dodEx)
                {
                    failedForDodGate = true;
                    mission.Status = MissionStatusEnum.Failed;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission.FailureReason =
                        "DoD gate failed: classification=Infra; gate-evaluation command exited -1\n" +
                        "Gate evaluation could not be completed due to an infrastructure error.";
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    await AppendMissionActivityAsync(mission.Id, "validation failed: " + mission.FailureReason, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "infrastructure error in DoD gate for mission " + mission.Id +
                        " exceptionType=" + dodEx.GetType().Name);
                }
            }

            bool retryingMissingVerdict = false;
            if (!failedForScopeViolation && !failedForDodGate && String.Equals(mission.Persona, "Judge", StringComparison.OrdinalIgnoreCase))
            {
                JudgeVerdict verdict = ParseJudgeVerdict(mission.AgentOutput);
                string? verdictFailureReason = null;
                if (verdict == JudgeVerdict.Pass && !TryValidateJudgePassOutput(mission.AgentOutput, out verdictFailureReason))
                {
                    verdict = JudgeVerdict.NeedsRevision;
                }

                // A Judge that exits without any parseable verdict is an OPERATIONAL miss, not a
                // substantive rejection: the review may have reached a conclusion that was never
                // flushed (for example a backgrounded test run that scheduled a wakeup and then
                // terminated before the standalone [ARMADA:VERDICT] line). Re-run the Judge in
                // place a bounded number of times instead of marking it Failed -- a hard failure
                // opens an incident and burns the auto-rescue budget on verified-good work. The
                // RecoveryAttempts counter bounds the total automated recovery effort on this one
                // mission; explicit FAIL / NEEDS_REVISION verdicts skip this path and stay terminal.
                if (ShouldRetryMissingJudgeVerdict(verdict != JudgeVerdict.None, mission.RecoveryAttempts))
                {
                    mission.RecoveryAttempts++;
                    mission.Status = MissionStatusEnum.Pending;
                    mission.CaptainId = null;
                    mission.DockId = null;
                    mission.ProcessId = null;
                    mission.AgentOutput = null;
                    mission.DiffSnapshot = null;
                    mission.FailureReason = null;
                    mission.StartedUtc = null;
                    mission.CompletedUtc = null;
                    mission.TotalRuntimeMs = null;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    retryingMissingVerdict = true;
                    _Logging.Warn(_Header + "judge mission " + mission.Id +
                        " produced no verdict line; re-running in place (recovery attempt " +
                        mission.RecoveryAttempts + " of " + _MaxMissingJudgeVerdictRetries + ")");
                }
                else if (verdict != JudgeVerdict.Pass)
                {
                    mission.Status = MissionStatusEnum.Failed;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission.FailureReason = verdictFailureReason ?? verdict switch
                    {
                        JudgeVerdict.Fail => "Judge verdict: FAIL",
                        JudgeVerdict.NeedsRevision => "Judge verdict: NEEDS_REVISION",
                        _ => "Judge mission did not emit an explicit PASS verdict"
                    };
                    // Persist the Judge's written review as ReviewComment, not only as the one-line
                    // FailureReason. Autonomous recovery inlines ReviewComment into the Worker rescue
                    // brief as "Reviewer feedback to address"; if only FailureReason were set, the
                    // rescue worker would revise with an empty feedback block and risk reproducing
                    // the rejected work (rescue_produced_no_commits).
                    mission.ReviewComment = BuildJudgeReviewComment(mission.AgentOutput, mission.FailureReason);
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "judge mission " + mission.Id + " blocked landing with verdict " + verdict);
                }

                // Audit-flag wiring: NEEDS_REVISION OR a PASS with non-empty Suggested Follow-ups
                // marks the upstream Worker's merge entry as deep-picked so the next
                // armada_drain_audit_queue surfaces it. Judge SUGGESTS, orchestrator decides.
                string? followUps = ExtractSuggestedFollowUps(mission.AgentOutput);
                bool hasFollowUps = !String.IsNullOrEmpty(followUps);
                if (verdict == JudgeVerdict.NeedsRevision || (verdict == JudgeVerdict.Pass && hasFollowUps))
                {
                    await TryFlagUpstreamMergeEntryForAuditAsync(mission, verdict, followUps, token).ConfigureAwait(false);
                }
            }

            if (retryingMissingVerdict)
            {
                // Free the dock and captain so the in-place re-run can be dispatched fresh on the
                // next scheduling tick, then skip handoff / landing / outcome entirely -- this
                // mission is back to Pending and must not be treated as produced or failed work.
                if (dock != null)
                {
                    try
                    {
                        await _Docks.ReclaimAsync(dock.Id, token: token).ConfigureAwait(false);
                    }
                    catch (Exception reclaimEx)
                    {
                        _Logging.Warn(_Header + "error reclaiming dock " + dock.Id +
                            " for judge re-run of mission " + mission.Id + ": " + reclaimEx.Message);
                    }
                }

                Captain? retryCaptain = await _Database.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
                if (retryCaptain != null && retryCaptain.CurrentMissionId == mission.Id)
                {
                    await _Captains.ReleaseAsync(retryCaptain, token).ConfigureAwait(false);
                }
                return;
            }

            bool hasDependentPipelineStages = await HasDependentPipelineStages(mission.VoyageId, mission.Id, token).ConfigureAwait(false);
            bool awaitingManualReview = false;
            if (!failedForScopeViolation && !failedForDodGate &&
                mission.Status == MissionStatusEnum.WorkProduced &&
                mission.RequiresReview)
            {
                mission.Status = MissionStatusEnum.Review;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.ReviewRequestedUtc = DateTime.UtcNow;
                mission.ReviewComment = null;
                mission.ReviewedByUserId = null;
                mission.ReviewedUtc = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                OnReviewRequested?.Invoke(mission);
                awaitingManualReview = true;
            }

            // Pipeline handoff: if missions in the same voyage depend on this one, prepare them
            bool preparedDownstreamStages = false;
            if (!failedForScopeViolation && !failedForDodGate && !awaitingManualReview)
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

                // A mission that ends terminal Failed/Cancelled never runs the successful-land
                // cleanup (MergeQueueService.CleanupLandedBranchesAsync), so its captain branch
                // would otherwise pile up forever. Reap it here, honoring BranchCleanupPolicy and
                // the don't-reap-while-rescuing guard. The method guards its own status precondition,
                // so the LandingFailed branch (still needed by the landing-retry / rebase path) is
                // left untouched.
                await ReapTerminalMissionBranchAsync(mission, token).ConfigureAwait(false);
            }

            await EmitMissionOutcomeTelemetryAsync(mission, captain, token).ConfigureAwait(false);
            await EmitContextPackUsageTelemetryAsync(mission, captain, token).ConfigureAwait(false);

            bool shouldAttemptLanding =
                !preparedDownstreamStages &&
                !hasDependentPipelineStages &&
                (mission.Status == MissionStatusEnum.WorkProduced ||
                mission.Status == MissionStatusEnum.PullRequestOpen);

            if (!shouldAttemptLanding)
            {
                _Logging.Info(_Header + "skipping landing for mission " + mission.Id +
                    " because it is not a terminal landed stage yet (status: " + mission.Status + ")");
            }

            if (OnMissionOutcome != null)
            {
                try
                {
                    await OnMissionOutcome.Invoke(mission, shouldAttemptLanding).ConfigureAwait(false);
                }
                catch (Exception outcomeEx)
                {
                    _Logging.Warn(_Header + "error in OnMissionOutcome handler for " + mission.Id + ": " + outcomeEx.Message);
                }
            }

            // Invoke OnMissionComplete synchronously (Phase A: push branch, create PR, or enqueue).
            // Captain stays in Working state until the handoff completes, preventing the captain
            // from being reassigned while git operations are still in progress.
            if (shouldAttemptLanding && dock != null && OnMissionComplete != null)
            {
                _Logging.Info(_Header + "executing synchronous landing handoff for mission " + mission.Id);
                try
                {
                    await OnMissionComplete.Invoke(mission, dock).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error in mission complete handler for " + mission.Id + ": " + ex.Message);
                }

                // The landing handoff itself can drive a mission terminal AFTER the earlier reap block
                // already ran on the WorkProduced status -- a protected-path violation or an auto-rescue
                // that produced no commits both mark the mission Failed inside MissionLandingHandler.
                // Re-read and reap so those branches don't leak. The helper no-ops for any non-Failed/
                // Cancelled status (Complete on a clean land, LandingFailed on a retryable land failure).
                Mission? afterLanding = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                if (afterLanding != null)
                {
                    await ReapTerminalMissionBranchAsync(afterLanding, token).ConfigureAwait(false);
                }
            }

            // Reclaim the dock after the handoff completes (or if no handler was set)
            string? completionDockId = dock?.Id;
            bool cleanupArchitectBranch =
                preparedDownstreamStages &&
                String.Equals(mission.Persona, "Architect", StringComparison.OrdinalIgnoreCase);

            if (!String.IsNullOrEmpty(completionDockId))
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

            (SignalTypeEnum signalType, string signalPayload) = BuildMissionOutcomeSignal(mission);
            Signal signal = new Signal(signalType, signalPayload);
            signal.FromCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            // Release the captain to idle only AFTER the handoff and dock reclaim are done,
            // and only if the captain is still assigned to this mission. Orphan recovery can
            // finalize an older mission using a captain record that has already moved on.
            Mission? releaseMission = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            bool quarantineForCreditAuthFailure =
                releaseMission != null &&
                releaseMission.Status == MissionStatusEnum.Failed &&
                ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(releaseMission.FailureReason);
            Captain? latestCaptain = await _Database.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            if (latestCaptain != null && latestCaptain.CurrentMissionId == mission.Id)
            {
                if (quarantineForCreditAuthFailure)
                {
                    DateTime? retryAfterUtc = ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(
                        releaseMission!.FailureReason,
                        latestCaptain.Runtime,
                        DateTime.UtcNow);
                    await _CaptainQuarantine.QuarantineAsync(
                        latestCaptain,
                        _CreditAuthQuarantineReason,
                        retryAfterUtc,
                        token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "captain " + captain.Id +
                        " quarantined after provider credit or authentication failure on mission " + mission.Id);
                }
                else
                {
                    await _Captains.ReleaseAsync(latestCaptain, token).ConfigureAwait(false);
                }
            }
            else
            {
                _Logging.Info(_Header + "skipping captain release for mission " + mission.Id +
                    " because captain " + captain.Id + " is now assigned to " + (latestCaptain?.CurrentMissionId ?? "nothing"));
            }

            // Try to pick up next pending mission
            List<Mission> pendingMissions = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Pending, token).ConfigureAwait(false);
            if (pendingMissions.Any())
            {
                Mission nextMission = pendingMissions.OrderBy(m => m.Priority).ThenBy(m => m.CreatedUtc).First();
                if (!String.IsNullOrEmpty(nextMission.VesselId))
                {
                    Vessel? vessel = await _Database.Vessels.ReadAsync(nextMission.VesselId, token).ConfigureAwait(false);
                    if (vessel != null)
                    {
                        await TryAssignAsync(nextMission, vessel, token).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <inheritdoc />
        public bool IsBroadScope(Mission mission)
        {
            if (mission == null) return false;
            return IsBroadScopeTitle(mission.Title);
        }

        /// <summary>
        /// Overload for lightweight scheduler summaries -- avoids requiring a full Mission hydration.
        /// </summary>
        public bool IsBroadScope(ActiveMissionSummary summary)
        {
            if (summary == null) return false;
            return IsBroadScopeTitle(summary.Title);
        }

        private static bool IsBroadScopeTitle(string? title)
        {
            // Inspect only the title. The description routinely embeds project rules
            // and negated guardrails ("Do NOT restructure", "never refactor", etc.)
            // which produced false positives that blocked otherwise-independent
            // concurrent missions on the same vessel. The title is short and
            // intentionally describes the mission's nature -- the right place to
            // look for broad-scope intent.
            string text = (title ?? "").ToLowerInvariant();

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
        public async Task GenerateClaudeMdAsync(string worktreePath, Mission mission, Vessel vessel, Captain? captain = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            string? runtimeName = captain != null ? captain.Runtime.ToString() : null;
            string instructionsFileName = MissionPromptBuilder.GetInstructionsFileName(runtimeName);
            string rootInstructionsPath = Path.Combine(worktreePath, instructionsFileName);
            string instructionsRelativePath = File.Exists(rootInstructionsPath)
                ? MissionPromptBuilder.GetGeneratedInstructionsRelativePath(runtimeName)
                : instructionsFileName;
            string instructionsPath = Path.Combine(worktreePath, instructionsRelativePath);

            TestOwnershipEnum testOwnership = await ResolveTestOwnershipAsync(mission, vessel, token).ConfigureAwait(false);
            Dictionary<string, string> templateParams = MissionPromptBuilder.BuildTemplateParams(mission, vessel, captain, null, testOwnership);
            List<MissionPlaybookSnapshot> playbookSnapshots = await LoadMissionPlaybookSnapshotsAsync(mission, token).ConfigureAwait(false);

            string content = "";
            PromptModuleLedger ledger = new PromptModuleLedger();

            // Captain instructions
            if (captain != null && !String.IsNullOrEmpty(captain.SystemInstructions))
            {
                content += ledger.Track("mission.captain_instructions_wrapper", await ResolveSectionAsync("mission.captain_instructions_wrapper", templateParams, token).ConfigureAwait(false));
                content += "\n";
            }

            // Vessel context sections
            if (!String.IsNullOrEmpty(vessel.ProjectContext))
            {
                content += ledger.Track("mission.project_context_wrapper", await ResolveSectionAsync("mission.project_context_wrapper", templateParams, token).ConfigureAwait(false));
                content += "\n";
            }

            if (!String.IsNullOrEmpty(vessel.StyleGuide))
            {
                content += ledger.Track("mission.code_style_wrapper", await ResolveSectionAsync("mission.code_style_wrapper", templateParams, token).ConfigureAwait(false));
                content += "\n";
            }

            if (_Settings.LearnedFactsEnabled &&
                vessel.EnableModelContext &&
                !String.IsNullOrEmpty(vessel.ModelContext))
            {
                content += ledger.Track("mission.model_context_wrapper", await ResolveSectionAsync("mission.model_context_wrapper", templateParams, token).ConfigureAwait(false));
                content += "\n";
            }

            if (playbookSnapshots.Count > 0)
            {
                string playbooksMarkdown = await RenderSelectedPlaybooksMarkdownAsync(
                    worktreePath,
                    mission,
                    playbookSnapshots,
                    token).ConfigureAwait(false);

                // The renderer can drop every snapshot: a learned-fact playbook while learned facts are
                // disabled, or a body that holds only a heading or a placeholder line. The wrapper calls
                // its content required reading, so emitting it empty tells a captain to read material
                // that the brief does not contain.
                if (!String.IsNullOrWhiteSpace(playbooksMarkdown))
                {
                    templateParams["SelectedPlaybooksMarkdown"] = playbooksMarkdown;
                    content += ledger.Track("mission.playbooks_wrapper", await ResolveSectionAsync("mission.playbooks_wrapper", templateParams, token).ConfigureAwait(false));
                    content += "\n";
                }
            }

            if (_Settings.CodeIndex.Enabled)
            {
                content += ledger.Track("mission.code_index", BuildCodeRetrievalSection(worktreePath, mission));
                content += "\n";
            }

            // Shared durable memory, named once for every runtime. Previously a captain only learned
            // that AI-Memory existed when the vessel's own instruction file happened to mention it, so
            // the same fleet-wide memory was visible to some captains and invisible to others.
            if (!String.IsNullOrWhiteSpace(_Settings.AiMemoryRoot))
            {
                content += ledger.Track("mission.ai_memory", BuildAiMemorySection(_Settings.AiMemoryRoot!));
                content += "\n";
            }

            // Mission preamble and metadata -- resolve persona prompt first, then inject into metadata template
            // A read-only mission takes the mode-aware output contract instead of the persona template.
            // The producing templates carry implementation language of their own -- commit your scoped
            // changes, run checks before committing -- which contradicts the read-only rules further
            // down the same brief. Reviewer personas are unaffected: their contract already reports
            // rather than changes.
            string personaPrompt = mission.IsReadOnlyMode
                ? MissionPromptBuilder.GetPersonaOutputContract(mission.Persona, mission.Mode)
                : await ResolvePersonaPromptAsync(mission.Persona, templateParams, token).ConfigureAwait(false);
            templateParams["PersonaPrompt"] = personaPrompt;
            content += ledger.Track("mission.metadata", await ResolveSectionAsync("mission.metadata", templateParams, token).ConfigureAwait(false));
            content += "\n";

            // Rules, context conservation, merge conflicts, progress signals -- from templates or hardcoded fallback.
            //
            // An Audit or Research mission gets the read-only rule set instead of the implementation
            // one. The modules dropped here are the ones a read-only captain cannot use: commit and push
            // rules, merge-conflict avoidance (nothing is edited), and the learned-fact request. Keeping
            // them produces a brief that contradicts its own mission, which captains report as a conflict
            // rather than silently obeying.
            if (mission.IsReadOnlyMode)
            {
                content += ledger.Track("mission.rules_read_only", BuildReadOnlyRulesSection(mission.Mode));
            }
            else
            {
                content += ledger.Track("mission.rules", await ResolveSectionAsync("mission.rules", templateParams, token).ConfigureAwait(false));
            }

            content += "\n";

            // Context conservation is mode-aware. The implementation module forbids reading any file
            // over 200 lines, which is right for an edit but wrong for an audit: a read-only captain
            // comparing whole files against a reference then greps and measures each file before
            // reading it anyway, spending three turns where one would do, and re-reads files it was
            // told not to hold. The read-only variant trades that rule for a file budget.
            if (mission.IsReadOnlyMode)
            {
                content += ledger.Track("mission.context_conservation_read_only", BuildReadOnlyContextConservationSection(mission.Mode));
            }
            else
            {
                content += ledger.Track("mission.context_conservation", await ResolveSectionAsync("mission.context_conservation", templateParams, token).ConfigureAwait(false));
            }

            // Independent tool calls can share one turn on every runtime Armada drives, but no module
            // said so, and captains issued one call per turn as a result. Built in code rather than
            // added to a template, so an existing stored template row cannot silently drop it.
            content += "\n";
            content += ledger.Track("mission.tool_batching", BuildToolBatchingSection());

            if (!mission.IsReadOnlyMode)
            {
                content += "\n";
                content += ledger.Track("mission.merge_conflict_avoidance", await ResolveSectionAsync("mission.merge_conflict_avoidance", templateParams, token).ConfigureAwait(false));
            }

            content += "\n";
            content += ledger.Track("mission.progress_signals", await ResolveSectionAsync("mission.progress_signals", templateParams, token).ConfigureAwait(false));

            // Model context updates. A read-only mission discovers nothing durable about the repository
            // by definition, so it is never asked for learned-fact proposals.
            if (vessel.EnableModelContext && _Settings.LearnedFactsEnabled && !mission.IsReadOnlyMode)
            {
                content += "\n";
                content += ledger.Track("mission.model_context_updates", await ResolveSectionAsync("mission.model_context_updates", templateParams, token).ConfigureAwait(false));
            }

            // If there's an existing repository instruction file, preserve it as read-only
            // context and write Armada's generated mission file elsewhere. Do not overwrite
            // tracked root instruction files such as CLAUDE.md.
            if (File.Exists(rootInstructionsPath))
            {
                string existing = await File.ReadAllTextAsync(rootInstructionsPath).ConfigureAwait(false);
                string sanitizedExisting = SanitizeExistingInstructions(existing);

                // A root file that is itself a stale Armada model-context dump is not project
                // instructions and must not be re-fed to a captain. Such a file survives
                // SanitizeExistingInstructions because it carries no "Mission Instructions" header to
                // cut at, and it can reach tens of kilobytes of accumulated learned facts.
                if (IsGeneratedModelContextDump(sanitizedExisting))
                {
                    _Logging.Warn(_Header + "root instruction file " + rootInstructionsPath +
                        " looks like a stale Armada model-context dump (" + sanitizedExisting.Length +
                        " chars); not inlining it into the mission brief");
                }
                else if (!String.IsNullOrWhiteSpace(sanitizedExisting))
                {
                    if (!String.Equals(existing, sanitizedExisting, StringComparison.Ordinal))
                    {
                        _Logging.Info(_Header + "sanitized generated mission sections from existing instructions at " + rootInstructionsPath);
                    }

                    // When the runtime loads the root file by itself, inlining it here would deliver the
                    // same text twice. Point at it instead of repeating it.
                    if (MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile(runtimeName))
                    {
                        content += ledger.Track("mission.existing_instructions_pointer",
                            "\n## Existing Project Instructions\n" +
                            "Your runtime already loaded `" + instructionsFileName + "` from the working directory. " +
                            "It holds the durable project rules for this repository and is not repeated here. " +
                            "Those rules apply; this mission brief wins on conflict.\n");
                    }
                    else
                    {
                        templateParams["ExistingClaudeMd"] = sanitizedExisting;
                        content += ledger.Track("mission.existing_instructions_wrapper", await ResolveSectionAsync("mission.existing_instructions_wrapper", templateParams, token).ConfigureAwait(false));
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
            await File.WriteAllTextAsync(instructionsPath, content).ConfigureAwait(false);

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
                _Logging.Warn(_Header + "could not persist mission instructions snapshot for " + mission.Id + ": " + ex.Message);
            }

            // Ensure generated instruction and briefing artifacts are ignored locally so
            // agents do not commit Armada-owned context material. This does not hide
            // tracked files, so the write path above avoids modifying tracked root
            // instruction files in the first place.
            try
            {
                string? excludePath = ResolveGitInfoExcludePath(worktreePath);
                if (!String.IsNullOrEmpty(excludePath))
                {
                    await EnsureGitExcludeEntryAsync(excludePath, instructionsRelativePath.Replace("\\", "/"), token).ConfigureAwait(false);
                    await EnsureGitExcludeEntryAsync(excludePath, ".armada/instructions/", token).ConfigureAwait(false);
                    await EnsureGitExcludeEntryAsync(excludePath, "_briefing/", token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not update git exclude for " + instructionsFileName + ": " + ex.Message);
            }

            _Logging.Info(_Header + "generated mission instructions at " + instructionsPath);

            await RecordPromptBudgetAsync(mission, captain, ledger, instructionsRelativePath, content, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Persists what the admiral actually sent: the byte size of every module written into the generated
        /// instruction file plus the file total, as a mission.prompt_budget event. Warns when the file exceeds
        /// the configured budget. Telemetry must never fail a dispatch, so every error here is swallowed with
        /// a warning.
        /// </summary>
        /// <param name="mission">Mission the instructions were generated for.</param>
        /// <param name="captain">Captain the instructions were generated for; may be null.</param>
        /// <param name="ledger">Ledger populated while the file was assembled.</param>
        /// <param name="instructionsRelativePath">Dock-relative path the file was written to.</param>
        /// <param name="content">Final file content.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task RecordPromptBudgetAsync(
            Mission mission,
            Captain? captain,
            PromptModuleLedger ledger,
            string instructionsRelativePath,
            string content,
            CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));

            try
            {
                int fileBytes = System.Text.Encoding.UTF8.GetByteCount(content ?? "");
                int budget = _Settings.CaptainInstructionByteBudget;
                bool overBudget = budget > 0 && fileBytes > budget;

                List<KeyValuePair<string, int>> largestFirst = ledger.GetModulesLargestFirst();

                if (overBudget)
                {
                    string largest = largestFirst.Count > 0
                        ? largestFirst[0].Key + " at " + largestFirst[0].Value + " bytes"
                        : "no tracked module";
                    _Logging.Warn(_Header + "mission " + mission.Id + " instruction file is " + fileBytes +
                        " bytes, over the " + budget + " byte budget. Largest module: " + largest);
                }

                Dictionary<string, int> modules = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, int> entry in largestFirst) modules[entry.Key] = entry.Value;

                ArmadaEvent budgetEvent = new ArmadaEvent(
                    "mission.prompt_budget",
                    "Instruction file: " + fileBytes + " bytes across " + modules.Count + " modules");
                budgetEvent.TenantId = mission.TenantId;
                budgetEvent.UserId = mission.UserId;
                budgetEvent.EntityType = "mission";
                budgetEvent.EntityId = mission.Id;
                budgetEvent.CaptainId = captain?.Id;
                budgetEvent.MissionId = mission.Id;
                budgetEvent.VesselId = mission.VesselId;
                budgetEvent.VoyageId = mission.VoyageId;
                budgetEvent.Payload = JsonSerializer.Serialize(new
                {
                    MissionId = mission.Id,
                    Runtime = captain != null ? captain.Runtime.ToString() : null,
                    InstructionsRelativePath = instructionsRelativePath,
                    InstructionFileBytes = fileBytes,
                    TrackedModuleBytes = ledger.TotalBytes,
                    ModuleCount = modules.Count,
                    ByteBudget = budget,
                    OverBudget = overBudget,
                    Modules = modules
                });

                await _Database.Events.CreateAsync(budgetEvent, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record prompt budget telemetry for " + mission.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Builds the shared-memory pointer. It names the index only and inlines no memory content:
        /// memory grows without bound, so inlining it would re-create the prompt bloat this module is
        /// measured against, and a captain can read the parts it needs. The path is emitted exactly as
        /// configured, so it must be the path as it resolves on the host the captain runs on.
        /// </summary>
        /// <param name="memoryRoot">Configured AI-Memory root path.</param>
        /// <returns>The AI-Memory section.</returns>
        internal static string BuildAiMemorySection(string memoryRoot)
        {
            string root = (memoryRoot ?? "").TrimEnd('/', '\\');

            return
                "## Shared Memory\n" +
                "Durable, cross-mission knowledge for this fleet lives at `" + root + "`.\n" +
                "Read `" + root + "/shared/INDEX.md` first, then follow it to what your mission needs. " +
                "Do not read the whole tree.\n" +
                "It is reference material, not authority: playbooks, vessel instructions, and this mission brief win on conflict.\n";
        }

        /// <summary>
        /// Builds the rule set for a mission whose deliverable is a report rather than a change. It
        /// replaces the implementation rules, which require commits and pushes, with the boundaries a
        /// read-only mission actually needs. It also states the completion contract explicitly, so a
        /// captain is not left inferring that producing nothing is a failure.
        /// </summary>
        /// <param name="mode">The mission mode; named in the text so the captain knows why.</param>
        /// <returns>The rules section.</returns>
        internal static string BuildReadOnlyRulesSection(MissionModeEnum mode)
        {
            return
                "## Rules (" + mode + " mission, read-only)\n" +
                "- This mission delivers a report. Do not edit, create, or delete repository files.\n" +
                "- Do not commit, stage, or push anything. Producing no commit is the expected outcome, not a failure.\n" +
                "- Do not run builds, tests, or any command that changes repository state.\n" +
                "- Read anything you need. Prefer targeted reads over broad exploration.\n" +
                "- Report exact evidence: file paths, line numbers, command output, and counts you actually observed.\n" +
                "- If a question cannot be answered from the evidence available, say so plainly rather than estimating.\n" +
                "- Work only within this worktree, except for paths this mission explicitly names.\n" +
                "- Put your findings in your final message. That message is the deliverable.\n" +
                "- Exit with code 0 on success.\n" +
                "- Use only ASCII characters in all output. No ANSI colour codes or terminal formatting.\n";
        }

        /// <summary>
        /// Builds the context-conservation rules for a mission whose deliverable is a report. The
        /// implementation module cannot be reused here. Its central rule -- never read a file over 200
        /// lines, grep for the section first -- assumes the captain needs one region of one file. An
        /// audit compares whole files against a reference, so the same rule makes the captain grep and
        /// measure a file before reading all of it regardless, turning one read into three turns, and
        /// makes it re-read files it was told not to retain. That module also tells the captain to
        /// commit when scope runs long, which a read-only mission cannot do.
        ///
        /// The budget is therefore expressed in files rather than lines, and running out of budget
        /// resolves to a partial report instead of a commit.
        /// </summary>
        /// <param name="mode">The mission mode; named in the text so the captain knows why.</param>
        /// <returns>The context conservation section.</returns>
        internal static string BuildReadOnlyContextConservationSection(MissionModeEnum mode)
        {
            return
                "## Context Conservation (CRITICAL)\n" +
                "\n" +
                "You have a limited context window. Exceeding it will crash your process and fail the " +
                "mission. The mission mode is " + mode + ", so the budget is spent on files examined, not " +
                "on lines per file:\n" +
                "\n" +
                "1. **Read each file you need once, in full, and keep it.** When your mission compares a " +
                "file against a reference, the whole file is what you need. Do not grep it, measure it, " +
                "and then read it anyway -- that costs three steps for one file's worth of evidence.\n" +
                "\n" +
                "2. **Never read the same path twice.** You already have it. If you are unsure what it " +
                "said, say so in your report rather than reading it again.\n" +
                "\n" +
                "3. **Use grep instead of a read only when you want a specific value across many files.** " +
                "For a single named file you are going to analyze, read it.\n" +
                "\n" +
                "4. **Do not explore beyond the files your mission names.** Locate the paths you were " +
                "given, then stop searching and start reading.\n" +
                "\n" +
                "5. **If the file set is larger than about 15 files**, examine them in the order the " +
                "mission lists, and when context runs short, report what you verified and what you did " +
                "not reach. Name the unexamined files explicitly. A partial report with an honest " +
                "boundary is a success; a crash is not.\n";
        }

        /// <summary>
        /// Builds the tool-batching directive. Every runtime Armada drives can issue several
        /// independent tool calls in one turn, but nothing in the brief said so, and captains issued
        /// exactly one call per turn: a failed 96-turn audit made 95 tool calls. Turn count drives
        /// wall-clock time and provider request volume, so the omission was expensive.
        ///
        /// This is built in code rather than folded into the context-conservation template because the
        /// template can be overridden per deployment, and an existing stored row would silently drop a
        /// line appended to the seeded default.
        /// </summary>
        /// <returns>The tool batching section.</returns>
        internal static string BuildToolBatchingSection()
        {
            return
                "## Tool Batching\n" +
                "\n" +
                "Issue independent tool calls together in one step rather than one per step. If the next " +
                "call does not need the previous call's output, both belong in the same step.\n" +
                "\n" +
                "- Reading six files you already know the paths of is one step, not six.\n" +
                "- Several greps over different files are one step.\n" +
                "- A command whose input is the previous command's output must wait. Nothing else must.\n" +
                "\n" +
                "This reduces the number of round trips the mission needs. It does not change what you " +
                "read or how carefully you work.\n";
        }

        /// <summary>
        /// Builds the code-retrieval guidance. It names no MCP tool: captains do not receive the Armada
        /// MCP server, so an instruction to call one is an instruction the captain cannot follow. The
        /// staged context pack is a plain file in the dock and needs no tooling to read; when it is
        /// absent or incomplete, ordinary file search is the fallback.
        /// </summary>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <param name="mission">Mission the pack was generated for.</param>
        /// <returns>The code-retrieval section.</returns>
        private static string BuildCodeRetrievalSection(string worktreePath, Mission mission)
        {
            string contextPackPath = Path.Combine(worktreePath, "_briefing", "context-pack.md");
            bool hasPack = File.Exists(contextPackPath);

            string content = "## Code Index Context\n";
            if (hasPack)
            {
                content += "A generated code-index context pack is staged at `_briefing/context-pack.md`. " +
                    "Read it before broad code search.\n";
            }
            else
            {
                content += "No `_briefing/context-pack.md` is staged in this dock. " +
                    "Use ordinary file search, scoped as narrowly as the task allows.\n";
            }

            content +=
                "\n" +
                "Treat the pack as discovery evidence, not authority. Playbooks, vessel instructions, " +
                "project instructions, and this mission brief win on conflict.\n" +
                "\n" +
                "Snippets may reflect the default branch and must be verified against the current branch before editing.\n" +
                "\n" +
                "If the pack is absent or misses material files, search the worktree directly.\n" +
                "\n" +
                "Final report must include one `Pack:` line: `read before search`, `search before read`, " +
                "`not staged`, or `miss`, with a short reason.\n";

            return content;
        }

        /// <summary>
        /// Builds the code-retrieval goal quoted in the Code Index instructions. This is a semantic
        /// search query, not a restatement of the brief: the full description already appears verbatim
        /// under Mission Instructions, so embedding it here repeated the entire brief in every captain's
        /// prompt and diluted the retrieval signal with acceptance criteria and non-goals. Capped to the
        /// leading intent, single-line.
        /// </summary>
        /// <param name="mission">Mission whose retrieval goal is being built.</param>
        internal static string BuildCodeRetrievalGoal(Mission mission)
        {
            string title = (mission.Title ?? "").Trim();
            string description = (mission.Description ?? "").Trim();

            string goal;
            if (String.IsNullOrWhiteSpace(description)) goal = title;
            else if (String.IsNullOrWhiteSpace(title)) goal = description;
            else goal = title + " -- " + description;

            goal = goal.Replace("\r", " ").Replace("\n", " ");
            return TruncateRetrievalGoal(goal, _MaxCodeRetrievalGoalLength);
        }

        /// <summary>
        /// Truncates a retrieval goal at a word boundary when one falls in the back half of the budget,
        /// otherwise hard-cuts. Returns the input unchanged when it already fits.
        /// </summary>
        /// <param name="goal">Goal text.</param>
        /// <param name="maxLength">Maximum characters to keep.</param>
        internal static string TruncateRetrievalGoal(string? goal, int maxLength)
        {
            if (String.IsNullOrEmpty(goal)) return "";
            if (goal!.Length <= maxLength) return goal;

            int cut = goal.LastIndexOf(' ', Math.Min(maxLength, goal.Length - 1));
            if (cut < maxLength / 2) cut = maxLength;
            return goal.Substring(0, cut).TrimEnd() + " ...";
        }

        /// <summary>
        /// Loads rows from mission_playbook_snapshots. Those snapshots are persisted at dispatch time from the merged mission selections (see AdmiralService playbook persistence); instruction Markdown is assembled here, not recomputed solely from voyage-level voyage_playbooks lists.
        /// </summary>
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
                if (!_Settings.LearnedFactsEnabled && IsLearnedFactsPlaybook(snapshot.FileName)) continue;

                // A playbook whose body is only a heading, or a "no accepted notes yet"
                // placeholder, costs the captain a read (or prompt tokens) to learn nothing.
                if (!HasSubstantivePlaybookContent(snapshot.Content)) continue;

                string header = "### " + snapshot.FileName;
                string? description = String.IsNullOrWhiteSpace(snapshot.Description) ? null : snapshot.Description.Trim();

                switch (snapshot.DeliveryMode)
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
                        (string attachedPath, string relativePath) = await MaterializeWorktreePlaybookAsync(
                            worktreePath,
                            snapshot,
                            i,
                            token).ConfigureAwait(false);
                        if (!String.Equals(snapshot.ResolvedPath, attachedPath, StringComparison.Ordinal) ||
                            !String.Equals(snapshot.WorktreeRelativePath, relativePath, StringComparison.Ordinal))
                        {
                            snapshot.ResolvedPath = attachedPath;
                            snapshot.WorktreeRelativePath = relativePath;
                            snapshotStateChanged = true;
                        }

                        sections.Add(
                            header + "\n" +
                            (description != null ? description + "\n" : "") +
                            "Read and follow this attached playbook at `" + relativePath.Replace("\\", "/") + "`.");
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

        private static bool IsLearnedFactsPlaybook(string? fileName)
        {
            if (String.IsNullOrWhiteSpace(fileName)) return false;
            return fileName.Trim().EndsWith("-learned.md", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true when a mission status satisfies a downstream dependency. PullRequestOpen
        /// counts because the captain branch is finalized and pushed at PR-open time.
        /// </summary>
        /// <param name="status">Upstream mission status.</param>
        internal static bool IsDependencySatisfyingStatus(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.WorkProduced
                || status == MissionStatusEnum.PullRequestOpen;
        }

        /// <summary>
        /// Returns true when a sibling belongs to the same parallel stage group as the dependency:
        /// same voyage, same upstream dependency, and the same pipeline stage order. All three are
        /// required. Architect fan-out clones downstream chains whose stages share both voyage and
        /// upstream dependency, so StageOrder is what separates "parallel stages of one chain" from
        /// "cloned chains that must run independently". A mission with no StageOrder did not come from
        /// a pipeline stage and never participates in a barrier.
        /// </summary>
        /// <param name="sibling">Candidate sibling mission.</param>
        /// <param name="dependency">The mission named by the dependent's DependsOnMissionId.</param>
        internal static bool IsParallelStageSibling(Mission sibling, Mission dependency)
        {
            if (sibling == null || dependency == null) return false;
            if (!dependency.StageOrder.HasValue || !sibling.StageOrder.HasValue) return false;
            if (sibling.StageOrder.Value != dependency.StageOrder.Value) return false;
            if (!String.Equals(sibling.VoyageId, dependency.VoyageId, StringComparison.Ordinal)) return false;
            return String.Equals(sibling.DependsOnMissionId, dependency.DependsOnMissionId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true when every sibling in the dependency's parallel stage group has reached a
        /// satisfying status. The dependency itself is checked by the caller. Sequential pipelines put
        /// one stage per order, so their groups have a single member and this is always true.
        /// </summary>
        /// <param name="mission">Mission awaiting assignment.</param>
        /// <param name="dependency">The mission named by DependsOnMissionId.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task<bool> DependencyGroupSatisfiedAsync(Mission mission, Mission dependency, CancellationToken token)
        {
            if (!dependency.StageOrder.HasValue || String.IsNullOrEmpty(dependency.VoyageId)) return true;

            List<Mission> voyageMissions = await _Database.Missions
                .EnumerateByVoyageAsync(dependency.VoyageId, token).ConfigureAwait(false);

            foreach (Mission sibling in voyageMissions)
            {
                if (sibling == null) continue;
                if (String.Equals(sibling.Id, dependency.Id, StringComparison.Ordinal)) continue;
                if (String.Equals(sibling.Id, mission.Id, StringComparison.Ordinal)) continue;
                if (!IsParallelStageSibling(sibling, dependency)) continue;

                if (!IsDependencySatisfyingStatus(sibling.Status))
                {
                    _Logging.Debug(_Header + "mission " + mission.Id + " waiting on parallel sibling " +
                        sibling.Id + " (stage " + sibling.StageOrder + ", " + sibling.Status + ")");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when a persona must hold the mission branch attached, because it commits its
        /// work there. Git allows only one worktree per branch, so an attached stage that provisions
        /// while an earlier stage still holds the branch fails with exit 128 -- the collision behind
        /// the downstream-persona dock race. Personas that only read (Judge, Architect, the specialist
        /// reviewers) can run detached at the same commit instead, which is also what lets same-stage
        /// personas such as dual-Judge run concurrently.
        ///
        /// Unknown or blank personas default to attached: the worst case is today's behavior, whereas
        /// wrongly detaching a committing persona would orphan its commits.
        /// </summary>
        /// <param name="persona">Mission persona name.</param>
        internal static bool PersonaRequiresBranchAttachment(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return true;

            switch (persona.Trim().ToLowerInvariant())
            {
                case "judge":
                case "architect":
                case "product manager":
                case "usability engineer":
                case "diagnosticprotocolreviewer":
                case "tenantsecurityreviewer":
                case "migrationdatareviewer":
                case "performancememoryreviewer":
                case "portingreferenceanalyst":
                case "frontendworkflowreviewer":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Returns true when a playbook body carries instruction a captain can act on. A snapshot
        /// holding only headings, rules, or the reflection scaffolding placeholder (emitted for a
        /// playbook that has never had an accepted reflection) is not staged or referenced.
        /// </summary>
        /// <param name="content">Captured playbook markdown.</param>
        internal static bool HasSubstantivePlaybookContent(string? content)
        {
            if (String.IsNullOrWhiteSpace(content)) return false;

            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("---", StringComparison.Ordinal)) continue;
                if (IsPlaybookPlaceholderLine(line)) continue;
                return true;
            }

            return false;
        }

        private static bool IsPlaybookPlaceholderLine(string line)
        {
            string trimmed = line.TrimEnd('.').Trim();
            return trimmed.StartsWith("No accepted ", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith(" yet", StringComparison.OrdinalIgnoreCase);
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

        private async Task<(string ResolvedPath, string RelativePath)> MaterializeWorktreePlaybookAsync(
            string worktreePath,
            MissionPlaybookSnapshot snapshot,
            int selectionOrder,
            CancellationToken token)
        {
            string relativeDir = Path.Combine(".armada", "playbooks");
            string absoluteDir = Path.Combine(worktreePath, relativeDir);
            Directory.CreateDirectory(absoluteDir);

            string fileName = BuildMaterializedPlaybookFileName(selectionOrder, snapshot.FileName);
            string absolutePath = Path.Combine(absoluteDir, fileName);
            await File.WriteAllTextAsync(absolutePath, snapshot.Content, token).ConfigureAwait(false);

            string? excludePath = ResolveGitInfoExcludePath(worktreePath);
            if (!String.IsNullOrEmpty(excludePath))
            {
                await EnsureGitExcludeEntryAsync(excludePath, ".armada/playbooks/", token).ConfigureAwait(false);
            }

            return (absolutePath, Path.Combine(relativeDir, fileName));
        }

        private async Task EnsureGitExcludeEntryAsync(string excludePath, string entry, CancellationToken token)
        {
            if (String.IsNullOrEmpty(excludePath)) return;
            if (String.IsNullOrEmpty(entry)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
            string excludeContent = File.Exists(excludePath)
                ? await File.ReadAllTextAsync(excludePath, token).ConfigureAwait(false)
                : "";
            bool hasEntry = excludeContent
                .Split('\n')
                .Select(l => l.Trim())
                .Any(l => String.Equals(l, entry, StringComparison.Ordinal));
            if (hasEntry) return;

            string suffix = excludeContent.Length > 0 && !excludeContent.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
            await File.AppendAllTextAsync(excludePath, suffix + entry + "\n", token).ConfigureAwait(false);
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
        /// Resolve a persona prompt template by persona name. Falls back to default worker preamble.
        /// </summary>
        /// <summary>
        /// Resolves who owns tests for this mission. The dispatch pipeline id is not persisted on the
        /// mission or the voyage, so ownership is read from the stage missions the dispatch actually
        /// created, and only falls back to a pipeline definition when the voyage has no siblings yet.
        /// A brief must never fail a dispatch, so any lookup error resolves to sole ownership: telling
        /// a producing captain it owns tests is safe, while assuming a stage that may not exist is the
        /// failure this resolves.
        /// </summary>
        /// <param name="mission">Mission being briefed.</param>
        /// <param name="vessel">Vessel the mission runs on; may be null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolved ownership.</returns>
        private async Task<TestOwnershipEnum> ResolveTestOwnershipAsync(Mission mission, Vessel? vessel, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            try
            {
                if (!String.IsNullOrEmpty(mission.VoyageId))
                {
                    List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId!, token).ConfigureAwait(false);
                    if (voyageMissions != null && voyageMissions.Count > 0)
                        return TestOwnershipResolver.Resolve(mission, voyageMissions);
                }

                if (vessel == null) return TestOwnershipEnum.SoleTestOwner;

                string? pipelineId = vessel.DefaultPipelineId;
                if (String.IsNullOrEmpty(pipelineId)) return TestOwnershipEnum.SoleTestOwner;

                Pipeline? pipeline = await _Database.Pipelines.ReadAsync(pipelineId!, token).ConfigureAwait(false);
                return TestOwnershipResolver.Resolve(mission, pipeline);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not resolve test ownership for mission " + mission.Id + ": " + ex.Message);
                return TestOwnershipEnum.SoleTestOwner;
            }
        }

        private async Task<string> ResolvePersonaPromptAsync(string? persona, Dictionary<string, string> templateParams, CancellationToken token)
        {
            return await MissionPromptBuilder.ResolvePersonaPromptAsync(persona, templateParams, _PromptTemplates, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Preserve only stable project instructions from an existing runtime instruction file.
        /// Generated Armada mission blocks are stripped to avoid recursively injecting stale
        /// mission objectives into future captain prompts.
        /// </summary>
        /// <summary>
        /// Detects a root instruction file that is really a stale Armada-generated model-context dump
        /// rather than hand-written project rules. Such a file accumulates learned facts from earlier
        /// missions, can reach tens of kilobytes, and must never be inlined back into a captain brief.
        /// Matched on the generated header that opens every such dump.
        /// </summary>
        /// <param name="existing">Sanitized contents of a root instruction file.</param>
        /// <returns>True when the file is a generated model-context dump.</returns>
        internal static bool IsGeneratedModelContextDump(string? existing)
        {
            if (String.IsNullOrWhiteSpace(existing)) return false;

            // The generated dump opens with the Model Context heading followed by its provenance
            // sentence. A hand-written file that merely mentions model context does not match, because
            // the marker sentence is unique to Armada's own generator.
            const string marker = "context was accumulated by AI agents during previous missions";
            int markerIndex = existing.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return false;

            // Only treat it as a dump when the marker leads the file, so a project file that quotes it
            // far below real rules is still inlined.
            return markerIndex < 400;
        }

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
                        "5. **If the mission scope feels too large** (more than 8 files, or files with 500+ lines to " +
                        "read), commit what you have, report progress, and exit with code 0. Partial progress is " +
                        "better than crashing.\n";

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
                        "- `[ARMADA:STATUS] Testing` -- transition mission to Testing status\n" +
                        "- `[ARMADA:STATUS] Review` -- transition mission to Review status\n" +
                        "- `[ARMADA:MESSAGE] your message here` -- send a progress message\n" +
                        "- `[ARMADA:RESULT] COMPLETE` -- worker/test engineer mission finished successfully\n" +
                        "- `[ARMADA:VERDICT] PASS` -- judge approves the mission\n" +
                        "- `[ARMADA:VERDICT] FAIL` -- judge rejects the mission\n" +
                        "- `[ARMADA:VERDICT] NEEDS_REVISION` -- judge requests follow-up changes\n" +
                        "Architect missions must not emit `[ARMADA:RESULT]` or `[ARMADA:VERDICT]`; they must output only real `[ARMADA:MISSION]` blocks.\n";

                case "mission.model_context_updates":
                    return
                        "## Learned-Fact Proposals\n" +
                        "\n" +
                        "Legacy model context is enabled for this vessel. Before you finish your mission, " +
                        "review the existing model context above (if any) as read-only background and consider " +
                        "whether you have discovered key information that would help future agents work on this repository more effectively. " +
                        "Examples include: architectural insights, code style conventions, naming conventions, " +
                        "logging patterns, error handling patterns, testing patterns, build quirks, common pitfalls, " +
                        "important dependencies, interdependencies between modules, concurrency patterns, " +
                        "and performance considerations.\n" +
                        "\n" +
                        "If you have useful additions, emit one or more `[LEARNED-FACT-PROPOSAL]` blocks in your final answer. " +
                        "Each proposal should contain only durable, non-obvious repository knowledge, written as concise markdown. " +
                        "Do not call `armada_update_vessel_context` for mission discoveries; proposals are routed through " +
                        "the reviewed learned-facts pipeline instead of appending to raw ModelContext.\n" +
                        "\n" +
                        "If you have nothing to propose, skip this step.\n";

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

            if (vessel == null || String.IsNullOrEmpty(vessel.LocalPath))
            {
                _Logging.Warn(_Header + "unable to clean architect branch " + branchName +
                    " for mission " + mission.Id + " because vessel metadata is incomplete");
                return;
            }

            BranchCleanupPolicyEnum cleanupPolicy = vessel.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;
            if (cleanupPolicy == BranchCleanupPolicyEnum.None)
            {
                _Logging.Info(_Header + "branch cleanup policy is None - retaining architect branch " + branchName + " after handoff");
                return;
            }

            try
            {
                await _Git.DeleteLocalBranchAsync(vessel.LocalPath, branchName, token).ConfigureAwait(false);
                _Logging.Info(_Header + "deleted architect branch " + branchName + " from bare repo after successful handoff");
            }
            catch (Exception branchEx)
            {
                _Logging.Warn(_Header + "failed to delete architect branch " + branchName + " from bare repo: " + branchEx.Message);
            }

            if (cleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote)
            {
                if (String.IsNullOrEmpty(vessel.WorkingDirectory))
                {
                    _Logging.Warn(_Header + "cannot delete remote architect branch " + branchName +
                        " because vessel working directory is not configured");
                    return;
                }

                try
                {
                    await _Git.DeleteRemoteBranchAsync(vessel.WorkingDirectory, branchName, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "deleted remote architect branch " + branchName + " after successful handoff");
                }
                catch (Exception remoteBranchEx)
                {
                    _Logging.Warn(_Header + "failed to delete remote architect branch " + branchName + ": " + remoteBranchEx.Message);
                }
            }
        }

        /// <summary>
        /// Reap (delete) the captain branch for a mission that has reached a terminal Failed or
        /// Cancelled state and has no active rescue or retry depending on that branch. A successful
        /// land runs <c>MergeQueueService.CleanupLandedBranchesAsync</c>, but terminally failed or
        /// cancelled missions never reach that path, so their branches accumulate. This mirrors that
        /// cleanup: it honors the resolved BranchCleanupPolicy (None = retain; LocalOnly = bare repo
        /// only; LocalAndRemote = bare repo + remote). Git failures are logged at warning level but
        /// are NOT emitted as retriable <c>merge_queue.branch_cleanup_failed</c> events, because the
        /// land itself failed and preserving the branch for retry is expected, not a surfaced failure.
        /// Missions in any non-terminal status are ignored.
        /// </summary>
        /// <param name="mission">The mission whose captain branch should be reaped.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task ReapTerminalMissionBranchAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (_Git == null)
            {
                _Logging.Debug(_Header + "git service unavailable -- skipping terminal branch reap for mission " + mission.Id);
                return;
            }

            if (mission.Status != MissionStatusEnum.Failed && mission.Status != MissionStatusEnum.Cancelled)
            {
                return;
            }

            string? branchName = mission.BranchName;
            if (String.IsNullOrEmpty(branchName) || String.IsNullOrEmpty(mission.VesselId))
            {
                return;
            }

            // CRITICAL GUARD: never reap while an autonomous rescue / retry for this mission is still
            // active. A rescue reuses or branches from the same captain branch, so deleting it would
            // strand the recovery. Only reap a genuinely terminal mission with no pending recovery.
            if (await HasActiveRecoveryForBranchAsync(mission, branchName, token).ConfigureAwait(false))
            {
                _Logging.Info(_Header + "skipping terminal branch reap for mission " + mission.Id +
                    " because an active rescue/retry still depends on branch " + branchName);
                return;
            }

            Vessel? vessel = !String.IsNullOrEmpty(mission.TenantId)
                ? await _Database.Vessels.ReadAsync(mission.TenantId, mission.VesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);

            if (vessel == null || String.IsNullOrEmpty(vessel.LocalPath))
            {
                _Logging.Warn(_Header + "unable to reap terminal branch " + branchName +
                    " for mission " + mission.Id + " because vessel metadata is incomplete");
                return;
            }

            BranchCleanupPolicyEnum cleanupPolicy = vessel.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;
            if (cleanupPolicy == BranchCleanupPolicyEnum.None)
            {
                _Logging.Info(_Header + "branch cleanup policy is None - retaining terminal mission branch " + branchName);
                return;
            }

            try
            {
                await _Git.DeleteLocalBranchAsync(vessel.LocalPath, branchName, token).ConfigureAwait(false);
                _Logging.Info(_Header + "reaped captain branch " + branchName + " from bare repo after terminal " + mission.Status + " for mission " + mission.Id);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not reap captain branch " + branchName + " from bare repo after terminal " + mission.Status + " for mission " + mission.Id + ": " + ex.Message);
            }

            if (cleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote && !String.IsNullOrEmpty(vessel.WorkingDirectory))
            {
                try
                {
                    await _Git.DeleteRemoteBranchAsync(vessel.WorkingDirectory, branchName, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "reaped remote captain branch " + branchName + " after terminal " + mission.Status + " for mission " + mission.Id);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not reap remote captain branch " + branchName + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Determine whether any active (non-terminal) mission still depends on the given captain
        /// branch -- either an autonomous rescue child (ParentMissionId links back to this mission)
        /// or a sibling stage stamped with the same branch. Used to guard terminal branch reaping so
        /// an in-flight recovery is never stranded. Scoped to the vessel so cross-voyage rescues are
        /// caught.
        /// </summary>
        private async Task<bool> HasActiveRecoveryForBranchAsync(Mission mission, string branchName, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.VesselId)) return false;

            List<Mission> vesselMissions = !String.IsNullOrEmpty(mission.TenantId)
                ? await _Database.Missions.EnumerateByVesselAsync(mission.TenantId, mission.VesselId, token).ConfigureAwait(false)
                : await _Database.Missions.EnumerateByVesselAsync(mission.VesselId, token).ConfigureAwait(false);

            foreach (Mission other in vesselMissions)
            {
                if (String.Equals(other.Id, mission.Id, StringComparison.Ordinal)) continue;

                // A mission still capable of using the branch is anything not yet terminal.
                if (other.Status == MissionStatusEnum.Complete ||
                    other.Status == MissionStatusEnum.Failed ||
                    other.Status == MissionStatusEnum.Cancelled)
                {
                    continue;
                }

                bool isRescueChild = !String.IsNullOrEmpty(other.ParentMissionId) &&
                    String.Equals(other.ParentMissionId, mission.Id, StringComparison.Ordinal);
                bool sharesBranch = !String.IsNullOrEmpty(other.BranchName) &&
                    String.Equals(other.BranchName, branchName, StringComparison.Ordinal);

                if (isRescueChild || sharesBranch)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// After a mission produces work, check if any missions in the same voyage depend on it
        /// and prepare them for assignment (inject prior stage context into description).
        /// </summary>
        private async Task<bool> TryHandoffToNextStageAsync(Mission completedMission, CancellationToken token)
        {
            if (String.IsNullOrEmpty(completedMission.VoyageId)) return false;

            // Find missions that depend on this completed mission
            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(completedMission.VoyageId, token).ConfigureAwait(false);
            List<Mission> dependentMissions = voyageMissions.Where(m =>
                m.DependsOnMissionId == completedMission.Id &&
                m.Status == MissionStatusEnum.Pending).ToList();

            if (dependentMissions.Count == 0) return false;

            // Load unread mailbox signals once for all downstream missions in this handoff batch
            List<Signal> unreadMailboxSignals = await LoadUnreadMailboxSignalsAsync(token).ConfigureAwait(false);
            HashSet<string> appliedSignalIds = new HashSet<string>(StringComparer.Ordinal);

            // Special handling for Architect stage: parse output into new missions
            if (String.Equals(completedMission.Persona, "Architect", StringComparison.OrdinalIgnoreCase))
            {
                List<ParsedArchitectMission> parsed = ParseArchitectOutput(completedMission);
                if (parsed.Count > 0)
                {
                    await ProjectArchitectMissionsToLogAsync(completedMission, parsed, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "architect produced " + parsed.Count + " mission definitions");

                    foreach (Mission nextMission in dependentMissions)
                    {
                if (String.Equals(nextMission.Persona, "Worker", StringComparison.OrdinalIgnoreCase))
                {
                    // Update the first parsed mission into this existing Worker mission slot
                    ParsedArchitectMission first = parsed[0];
                    nextMission.Title = first.Title + " [Worker]";
                    string architectFirstDesc = ArchitectHandoffMarker + "\n" + first.Description;
                    List<Signal> firstApplicable = GetApplicableMailboxSignals(unreadMailboxSignals, nextMission.Id, nextMission.VoyageId);
                    if (firstApplicable.Count > 0)
                    {
                        architectFirstDesc = BuildMailboxNotesBlock(firstApplicable) + "\n\n" + architectFirstDesc;
                        foreach (Signal s in firstApplicable) appliedSignalIds.Add(s.Id);
                    }
                    nextMission.Description = architectFirstDesc;
                    nextMission.BranchName = null;
                    nextMission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(nextMission, token).ConfigureAwait(false);
                    await RetitleDependentChainAsync(voyageMissions, nextMission, first.Title, first.Description, token).ConfigureAwait(false);

                    // Find what depends on this worker mission (Judge, TestEngineer stages)
                    // Create additional worker missions for remaining parsed items
                    for (int i = 1; i < parsed.Count; i++)
                    {
                                string additionalDesc = ArchitectHandoffMarker + "\n" + parsed[i].Description;
                                // Additional workers share the same voyageId; voyage-level signals apply to them too
                                List<Signal> additionalApplicable = GetApplicableMailboxSignals(unreadMailboxSignals, null, completedMission.VoyageId);
                                if (additionalApplicable.Count > 0)
                                {
                                    additionalDesc = BuildMailboxNotesBlock(additionalApplicable) + "\n\n" + additionalDesc;
                                    foreach (Signal s in additionalApplicable) appliedSignalIds.Add(s.Id);
                                }
                                Mission additionalWorker = new Mission(parsed[i].Title + " [Worker]", additionalDesc);
                                additionalWorker.TenantId = completedMission.TenantId;
                                additionalWorker.UserId = completedMission.UserId;
                                additionalWorker.VoyageId = completedMission.VoyageId;
                                additionalWorker.VesselId = completedMission.VesselId;
                        additionalWorker.Persona = "Worker";
                        additionalWorker.DependsOnMissionId = completedMission.Id;
                        additionalWorker.BranchName = null;
                        additionalWorker = await _Database.Missions.CreateAsync(additionalWorker, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "architect created additional worker mission " + additionalWorker.Id + ": " + parsed[i].Title);

                        await CloneDependentChainAsync(voyageMissions, nextMission, additionalWorker, parsed[i].Title, parsed[i].Description, token).ConfigureAwait(false);
                    }
                }
            }

                    await ApplyArchitectMissionDependenciesAsync(completedMission, parsed, token).ConfigureAwait(false);
                    foreach (string signalId in appliedSignalIds)
                        await _Database.Signals.MarkReadAsync(signalId, token).ConfigureAwait(false);
                    return true; // Architect special handling complete, skip normal handoff
                }

                bool hadArchitectMarkers = !String.IsNullOrEmpty(completedMission.AgentOutput) &&
                    completedMission.AgentOutput.Contains("[ARMADA:MISSION]", StringComparison.Ordinal);

                string failureReason = hadArchitectMarkers
                    ? "Architect produced no valid [ARMADA:MISSION] definitions in output"
                    : "Architect produced no [ARMADA:MISSION] markers in output";

                _Logging.Warn(_Header + "architect mission " + completedMission.Id +
                    " produced no valid mission definitions -- marking as failed");
                completedMission.Status = MissionStatusEnum.Failed;
                completedMission.FailureReason = failureReason;
                completedMission.CompletedUtc = DateTime.UtcNow;
                completedMission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(completedMission, token).ConfigureAwait(false);
                return false;
            }

            foreach (Mission nextMission in dependentMissions)
            {
                await PrepareSingleDependentHandoffAsync(completedMission, nextMission, unreadMailboxSignals, appliedSignalIds, token).ConfigureAwait(false);
            }

            // Mark drained signals as read after all downstream missions are updated
            foreach (string signalId in appliedSignalIds)
                await _Database.Signals.MarkReadAsync(signalId, token).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Prepare a single downstream pipeline dependent for assignment by stamping it with the
        /// upstream stage's branch and injecting the persona preamble plus the prior-stage context
        /// (agent output, diff) into its description. Applicable unread mailbox signals are prepended
        /// at the top of the brief and their ids accumulated into <paramref name="appliedSignalIds"/>
        /// for the caller to mark read once the batch (or single lazy handoff) is complete. This is
        /// the shared core used by both the batch handoff (<see cref="TryHandoffToNextStageAsync"/>)
        /// and the lazy self-heal path in <see cref="TryAssignAsync"/>, so a handoff missed by the
        /// creation-order race is reconstructed identically rather than duplicated.
        /// </summary>
        /// <param name="completedMission">The upstream stage that produced the work.</param>
        /// <param name="nextMission">The downstream dependent to prepare; mutated and persisted.</param>
        /// <param name="unreadMailboxSignals">Unread mailbox signals loaded once by the caller.</param>
        /// <param name="appliedSignalIds">Accumulator of signal ids applied to a brief.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task PrepareSingleDependentHandoffAsync(
            Mission completedMission,
            Mission nextMission,
            List<Signal> unreadMailboxSignals,
            HashSet<string> appliedSignalIds,
            CancellationToken token = default)
        {
            // Build persona-specific preamble for the next stage
            string personaPreamble = "";
            switch (nextMission.Persona)
            {
                case "Worker":
                    personaPreamble = "## Your Role: Worker (Implement)\n\n" +
                        "You are implementing code changes based on the Architect's plan. " +
                        "Review the prior stage output below and implement the described changes.\n\n";
                    break;
                case "TestEngineer":
                    personaPreamble = "## Your Role: TestEngineer (Write Tests)\n\n" +
                        "You are writing tests for code changes made by the Worker. " +
                        "Review the diff below and write unit tests, integration tests, or test harness updates " +
                        "that cover the changes. Follow existing test patterns in the repository. " +
                        "Scope yourself only to this mission, not sibling missions in the same voyage. Cover the " +
                        "happy path, but also add negative or edge-path coverage for validation, timeout, cancellation, " +
                        "retry, cleanup, and error-handling branches when they are in scope. Include short " +
                        "`## Coverage Added`, `## Negative Paths`, and `## Residual Risks` sections. " +
                        "End with a standalone `[ARMADA:RESULT] COMPLETE` line and a short summary.\n\n";
                    break;
                case "Judge":
                    personaPreamble = "## Your Role: Judge (Review)\n\n" +
                        "You are reviewing the completed work for correctness, completeness, scope compliance, " +
                        "test adequacy, and failure-mode safety. Examine the diff below against the current mission " +
                        "description only, not sibling missions in the same voyage. Assume there may be at least " +
                        "one hidden bug. Your response must include `## Completeness`, `## Correctness`, `## Tests`, " +
                        "`## Failure Modes`, and `## Verdict` sections. A PASS is only allowed when tests are adequate, " +
                        "negative-path coverage for validation, timeout, cancellation, retry, cleanup, and error-handling " +
                        "changes is present or justified, and failure modes were explicitly reviewed. End with a standalone line " +
                        "`[ARMADA:VERDICT] PASS`, `[ARMADA:VERDICT] FAIL`, or `[ARMADA:VERDICT] NEEDS_REVISION`.\n\n";
                    break;
            }

            // Inject context from the completed stage into the next stage's description.
            // The block opens with a per-upstream marker so a repeated handoff for the same
            // completed mission REPLACES the previous block instead of appending a second copy.
            string handoffContext = "\n\n---\n" +
                BuildHandoffMarker(completedMission.Id) + "\n" +
                "## Prior Stage Output\n" +
                "The previous pipeline stage (" + (completedMission.Persona ?? "Worker") + ") " +
                "completed mission \"" + completedMission.Title + "\" (" + completedMission.Id + ").\n" +
                "Branch: " + (completedMission.BranchName ?? "unknown") + "\n";

            // Use the canonical persisted AgentOutput for handoff instead of reparsing
            // the mission log file. AgentOutput is captured from accumulated stdout by
            // HandleCompletionAsync and is the single source of truth for agent output.
            if (!String.IsNullOrEmpty(completedMission.AgentOutput))
            {
                string agentOutput = completedMission.AgentOutput.Trim();
                int maxOutputChars = 8000;
                if (agentOutput.Length > maxOutputChars)
                {
                    // Truncate from the end (keep the beginning which typically contains
                    // the plan/structure) rather than the beginning
                    agentOutput = agentOutput.Substring(0, maxOutputChars) + "\n...(truncated)";
                }
                handoffContext += "\n### Agent Output (from " + completedMission.Persona + " stage)\n```\n" + agentOutput + "\n```\n";
            }

            // Include the diff snapshot if available, scoped so a large generated-output diff (e.g. a
            // regenerated data-file snapshot) cannot overflow the reviewing model's context.
            if (!String.IsNullOrEmpty(completedMission.DiffSnapshot))
            {
                handoffContext += "\n### Diff from prior stage\n```diff\n" +
                    BuildReviewDiff(completedMission.DiffSnapshot!, _MaxReviewDiffChars) + "\n```\n";
            }
            else
            {
                handoffContext += "\n*No diff available from prior stage. The work is on the branch above.*\n";
            }

            // Idempotency: strip any prior handoff block for this same upstream mission, and do not
            // re-prepend a persona preamble that is already present. Without this, a handoff that runs
            // twice for the same pair (batch path plus the lazy self-heal path, or a rescue re-prepare)
            // duplicates the entire block, which can multiply a brief several times over.
            string existingDescription = StripHandoffBlock(nextMission.Description ?? "", completedMission.Id);

            string handoffDescription = personaPreamble.Length > 0 && !ContainsPersonaPreamble(existingDescription, personaPreamble)
                ? personaPreamble + existingDescription + handoffContext
                : existingDescription + handoffContext;

            // Drain unread mailbox signals and prepend at the absolute top of the brief
            List<Signal> applicableSignals = GetApplicableMailboxSignals(unreadMailboxSignals, nextMission.Id, nextMission.VoyageId);
            if (applicableSignals.Count > 0)
            {
                handoffDescription = BuildMailboxNotesBlock(applicableSignals) + "\n\n" + handoffDescription;
                foreach (Signal s in applicableSignals) appliedSignalIds.Add(s.Id);
            }

            if (handoffDescription.Length > _MaxMissionDescriptionChars)
            {
                _Logging.Warn(_Header + "pipeline handoff: mission " + nextMission.Id + " description of " +
                    handoffDescription.Length + " chars exceeds the " + _MaxMissionDescriptionChars +
                    " char budget; truncating the tail. The full change remains on branch " +
                    (completedMission.BranchName ?? "unknown"));
                handoffDescription = TruncateMissionDescription(handoffDescription, _MaxMissionDescriptionChars);
            }

            nextMission.Description = handoffDescription;
            nextMission.BranchName = completedMission.BranchName;
            nextMission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(nextMission, token).ConfigureAwait(false);

            _Logging.Info(_Header + "pipeline handoff: prepared mission " + nextMission.Id +
                " (" + nextMission.Persona + ") with context from " + completedMission.Id +
                " (" + completedMission.Persona + ")");
        }

        // Max characters of prior-stage diff embedded into the next stage's brief. A large generated-output diff
        // (e.g. a regenerated data-file snapshot with hundreds of files) can otherwise overflow the reviewing
        // model's context ("Prompt is too long"). The full change always remains on the branch for inspection.
        private const int _MaxReviewDiffChars = 60000;

        // Hard ceiling on a persisted mission description. The per-part caps above bound one handoff block
        // (8,000 chars of agent output plus _MaxReviewDiffChars of diff), but they cannot bound the total
        // once a brief carries a base description, a persona preamble, and a handoff block. This is the
        // backstop that keeps a runaway brief out of the captain prompt entirely.
        private const int _MaxMissionDescriptionChars = 90000;

        // Opening marker of a prior-stage handoff block, keyed by the upstream mission id. Present so a
        // repeated handoff replaces its own previous block rather than appending a duplicate.
        private const string _HandoffMarkerPrefix = "<!-- ARMADA:HANDOFF:";

        private const string _HandoffMarkerSuffix = " -->";

        /// <summary>
        /// Builds the idempotency marker that opens a prior-stage handoff block for one upstream mission.
        /// </summary>
        /// <param name="completedMissionId">Id of the upstream mission that produced the work.</param>
        /// <returns>The marker text.</returns>
        internal static string BuildHandoffMarker(string completedMissionId)
        {
            return _HandoffMarkerPrefix + (completedMissionId ?? "") + _HandoffMarkerSuffix;
        }

        /// <summary>
        /// Removes a previously injected handoff block for one upstream mission from a description, so the
        /// caller can re-append a fresh block without duplicating it. The block runs from the "\n\n---\n"
        /// separator that precedes its marker (or from the marker itself when the separator is absent) to
        /// the start of the next handoff marker, or to the end of the description. A description with no
        /// matching marker is returned unchanged.
        /// </summary>
        /// <param name="description">Existing mission description; may be null or empty.</param>
        /// <param name="completedMissionId">Id of the upstream mission whose block should be removed.</param>
        /// <returns>The description without that upstream mission's handoff block.</returns>
        internal static string StripHandoffBlock(string? description, string completedMissionId)
        {
            if (String.IsNullOrEmpty(description)) return description ?? "";
            if (String.IsNullOrEmpty(completedMissionId)) return description;

            string marker = BuildHandoffMarker(completedMissionId);
            int markerIndex = description.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return description;

            const string separator = "\n\n---\n";
            int blockStart = markerIndex;
            if (markerIndex >= separator.Length &&
                String.CompareOrdinal(description, markerIndex - separator.Length, separator, 0, separator.Length) == 0)
            {
                blockStart = markerIndex - separator.Length;
            }

            int nextMarkerIndex = description.IndexOf(_HandoffMarkerPrefix, markerIndex + marker.Length, StringComparison.Ordinal);
            if (nextMarkerIndex < 0)
                return description.Substring(0, blockStart).TrimEnd();

            int nextBlockStart = nextMarkerIndex;
            if (nextMarkerIndex >= separator.Length &&
                String.CompareOrdinal(description, nextMarkerIndex - separator.Length, separator, 0, separator.Length) == 0)
            {
                nextBlockStart = nextMarkerIndex - separator.Length;
            }

            return description.Substring(0, blockStart) + description.Substring(nextBlockStart);
        }

        /// <summary>
        /// Reports whether a description already carries a persona preamble, so a repeated handoff does not
        /// prepend a second copy. Matching is on the preamble's heading line, because the body below it can
        /// be reworded between releases while the heading stays stable.
        /// </summary>
        /// <param name="description">Existing mission description.</param>
        /// <param name="personaPreamble">Persona preamble that the caller is about to prepend.</param>
        /// <returns>True when the preamble heading is already present.</returns>
        internal static bool ContainsPersonaPreamble(string? description, string? personaPreamble)
        {
            if (String.IsNullOrEmpty(description) || String.IsNullOrEmpty(personaPreamble)) return false;

            int headingEnd = personaPreamble.IndexOf('\n');
            string heading = headingEnd > 0 ? personaPreamble.Substring(0, headingEnd).TrimEnd() : personaPreamble.TrimEnd();
            if (String.IsNullOrEmpty(heading)) return false;

            return description.Contains(heading, StringComparison.Ordinal);
        }

        /// <summary>
        /// Truncates an over-budget mission description from the tail, keeping the head where the actual
        /// brief lives, and leaves a visible note so the captain knows content was cut and where the whole
        /// change still is. Returns the input unchanged when it fits.
        /// </summary>
        /// <param name="description">Description to bound.</param>
        /// <param name="maxChars">Maximum characters allowed.</param>
        /// <returns>The bounded description.</returns>
        internal static string TruncateMissionDescription(string description, int maxChars)
        {
            if (String.IsNullOrEmpty(description) || description.Length <= maxChars) return description;

            const string note = "\n\n...(brief truncated to fit the mission description budget; the full change is on the branch above)\n";
            int allowed = Math.Max(0, maxChars - note.Length);
            return description.Substring(0, allowed).TrimEnd() + note;
        }

        /// <summary>
        /// Scopes a git diff so it fits a reviewing model's context. Under the budget it is returned unchanged.
        /// Over the budget, per-file sections are kept whole smallest-first (so small CODE diffs survive intact)
        /// and the largest files (typically bulk generated DATA) are elided to their header + a line-count note --
        /// so the reviewer still sees WHICH files changed and by how much, without the overflowing content.
        /// </summary>
        internal static string BuildReviewDiff(string diff, int maxChars)
        {
            if (String.IsNullOrEmpty(diff) || diff.Length <= maxChars) return diff;

            const string marker = "diff --git ";
            int first = diff.IndexOf(marker, StringComparison.Ordinal);
            if (first < 0)
            {
                // Not a standard git diff -- hard-truncate as a last resort.
                return diff.Substring(0, Math.Max(0, maxChars - 40)) + "\n...(diff truncated to fit review context)";
            }

            List<string> sections = new List<string>();
            if (first > 0) sections.Add(diff.Substring(0, first));
            int idx = first;
            while (idx >= 0)
            {
                int next = diff.IndexOf("\n" + marker, idx + marker.Length, StringComparison.Ordinal);
                sections.Add(next < 0 ? diff.Substring(idx) : diff.Substring(idx, next + 1 - idx));
                idx = next < 0 ? -1 : next + 1;
            }

            // Greedily keep whole sections, smallest-first, until the budget is exhausted.
            List<int> order = Enumerable.Range(0, sections.Count).OrderBy(i => sections[i].Length).ToList();
            HashSet<int> keepWhole = new HashSet<int>();
            int used = 0;
            foreach (int i in order)
            {
                if (used + sections[i].Length <= maxChars) { keepWhole.Add(i); used += sections[i].Length; }
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int elided = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                if (keepWhole.Contains(i)) { sb.Append(sections[i]); continue; }
                string section = sections[i];
                int nl = section.IndexOf('\n');
                string header = nl < 0 ? section : section.Substring(0, nl);
                int lines = section.Count(c => c == '\n');
                sb.Append(header).Append("\n... (").Append(lines)
                    .Append(" lines elided to fit review context; full change is on the branch)\n");
                elided++;
            }
            if (elided > 0)
            {
                sb.Append("\n[note] ").Append(elided)
                    .Append(" large file diff(s) were summarized above to keep the review within context; inspect them on the branch if the change is not obvious from the code diffs and file list.\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Lazily run the pipeline handoff for a single dependent whose upstream dependency already
        /// reached a handoff-eligible status but never had its branch/context propagated (the
        /// creation-order race where the dependent row was created after the upstream completed).
        /// Loads unread mailbox signals, prepares the dependent via
        /// <see cref="PrepareSingleDependentHandoffAsync"/>, and marks the applied signals read.
        /// Idempotent at the caller: once the branch is stamped, IsPipelineHandoffPrepared returns
        /// true and this path is not re-entered.
        /// </summary>
        /// <param name="dependency">The completed upstream stage.</param>
        /// <param name="dependent">The stranded downstream mission to prepare; mutated and persisted.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task SelfHealDependentHandoffAsync(Mission dependency, Mission dependent, CancellationToken token = default)
        {
            List<Signal> unreadMailboxSignals = await LoadUnreadMailboxSignalsAsync(token).ConfigureAwait(false);
            HashSet<string> appliedSignalIds = new HashSet<string>(StringComparer.Ordinal);
            await PrepareSingleDependentHandoffAsync(dependency, dependent, unreadMailboxSignals, appliedSignalIds, token).ConfigureAwait(false);
            foreach (string signalId in appliedSignalIds)
                await _Database.Signals.MarkReadAsync(signalId, token).ConfigureAwait(false);
        }

        private static readonly System.Text.Json.JsonSerializerOptions _MailboxJsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private async Task<List<Signal>> LoadUnreadMailboxSignalsAsync(CancellationToken token)
        {
            EnumerationQuery nudgeQuery = new EnumerationQuery();
            nudgeQuery.PageSize = 200;
            nudgeQuery.UnreadOnly = true;
            nudgeQuery.SignalType = "Nudge";

            EnumerationQuery mailQuery = new EnumerationQuery();
            mailQuery.PageSize = 200;
            mailQuery.UnreadOnly = true;
            mailQuery.SignalType = "Mail";

            EnumerationResult<Signal> nudgeResult = await _Database.Signals.EnumerateAsync(nudgeQuery, token).ConfigureAwait(false);
            EnumerationResult<Signal> mailResult = await _Database.Signals.EnumerateAsync(mailQuery, token).ConfigureAwait(false);

            List<Signal> all = new List<Signal>();
            all.AddRange(nudgeResult.Objects);
            all.AddRange(mailResult.Objects);
            return all;
        }

        private static List<Signal> GetApplicableMailboxSignals(List<Signal> signals, string? missionId, string? voyageId)
        {
            List<Signal> result = new List<Signal>();
            foreach (Signal signal in signals)
            {
                VoyageMailboxSignalPayload? payload = TryParseMailboxPayload(signal.Payload);
                if (payload == null) continue;

                if (!String.IsNullOrEmpty(payload.MissionId))
                {
                    // Mission-specific: only applies to the targeted mission
                    if (!String.IsNullOrEmpty(missionId) &&
                        String.Equals(payload.MissionId, missionId, StringComparison.Ordinal))
                        result.Add(signal);
                }
                else
                {
                    // Voyage-level: applies to all missions of the target voyage
                    if (!String.IsNullOrEmpty(voyageId) &&
                        !String.IsNullOrEmpty(payload.VoyageId) &&
                        String.Equals(payload.VoyageId, voyageId, StringComparison.Ordinal))
                        result.Add(signal);
                }
            }
            return result;
        }

        private static VoyageMailboxSignalPayload? TryParseMailboxPayload(string? payload)
        {
            if (String.IsNullOrEmpty(payload)) return null;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<VoyageMailboxSignalPayload>(payload, _MailboxJsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildMailboxNotesBlock(List<Signal> signals)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("[ORCHESTRATOR NOTES]");
            foreach (Signal signal in signals)
            {
                VoyageMailboxSignalPayload? payload = TryParseMailboxPayload(signal.Payload);
                if (payload != null && !String.IsNullOrEmpty(payload.Message))
                    sb.AppendLine(payload.Message);
            }
            sb.Append("[/ORCHESTRATOR NOTES]");
            return sb.ToString();
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
                 m.Status == MissionStatusEnum.Review ||
                 m.Status == MissionStatusEnum.WaitingForInput)).ToList();

            foreach (Mission dependent in directDependents)
            {
                dependent.Status = MissionStatusEnum.Cancelled;
                dependent.FailureReason = "Blocked by failed dependency " + failedMission.Id;
                dependent.CompletedUtc = DateTime.UtcNow;
                dependent.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(dependent, token).ConfigureAwait(false);
                _Logging.Info(_Header + "cancelled dependent mission " + dependent.Id +
                    " because upstream mission " + failedMission.Id + " ended in " + failedMission.Status);
                await CancelDependentPipelineStagesAsync(dependent, token).ConfigureAwait(false);
            }
        }

        internal async Task UpdateVoyageTerminalStatusAsync(string? voyageId, CancellationToken token)
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
                m.Status == MissionStatusEnum.WaitingForInput ||
                m.Status == MissionStatusEnum.PullRequestOpen);

            if (anyActive) return;

            bool allDone = missions.All(m =>
                m.Status == MissionStatusEnum.Complete ||
                m.Status == MissionStatusEnum.Failed ||
                m.Status == MissionStatusEnum.Cancelled ||
                m.Status == MissionStatusEnum.LandingFailed ||
                m.Status == MissionStatusEnum.WorkProduced);

            if (!allDone) return;

            bool anyFailed = missions.Any(m =>
                m.Status == MissionStatusEnum.Failed ||
                m.Status == MissionStatusEnum.LandingFailed);

            // Real-signal completion gate: a Judge PASS is the agent's own self-report. A voyage may
            // only reach Complete (which authorizes landing) when its Checks -- the Build/UnitTest run
            // from real command output -- reflect that. A failed Check overrides a Judge PASS;
            // unresolved Checks hold completion. Voyages with no Checks are unaffected (backward compatible).
            if (!anyFailed)
            {
                VoyageCheckGate gate = await EvaluateVoyageChecksAsync(voyageId, missions, token).ConfigureAwait(false);
                if (gate == VoyageCheckGate.HasFailed)
                {
                    anyFailed = true;
                    _Logging.Warn(_Header + "voyage " + voyageId + " has a failed Check -- overriding Judge verdict to Failed (real-signal gate)");
                }
                else if (gate == VoyageCheckGate.HasPending)
                {
                    _Logging.Info(_Header + "voyage " + voyageId + " missions are done but its Checks are not green yet -- holding completion until Checks resolve (real-signal gate)");
                    return;
                }
            }

            voyage.Status = anyFailed ? VoyageStatusEnum.Failed : VoyageStatusEnum.Complete;
            voyage.CompletedUtc = DateTime.UtcNow;
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);
            _Logging.Info(_Header + "voyage " + voyage.Id + " reached terminal status " + voyage.Status + " during mission completion");
        }

        /// <summary>Outcome of evaluating a voyage's Checks for the real-signal completion gate.</summary>
        private enum VoyageCheckGate
        {
            /// <summary>No (non-canceled) Checks attached -- gate does not apply.</summary>
            NoChecks,
            /// <summary>All Checks are Passed.</summary>
            AllGreen,
            /// <summary>At least one Check Failed.</summary>
            HasFailed,
            /// <summary>At least one Check is still Pending/Running (unresolved).</summary>
            HasPending
        }

        /// <summary>
        /// Evaluates the Checks attached to a voyage and its missions to decide whether the real
        /// signal permits the voyage to complete. Canceled Checks are ignored. This is the
        /// enforcement point for "a Judge PASS must be backed by green independent Checks".
        /// </summary>
        private async Task<VoyageCheckGate> EvaluateVoyageChecksAsync(
            string voyageId, List<Mission> missions, CancellationToken token)
        {
            Dictionary<string, CheckRun> checks = new Dictionary<string, CheckRun>();
            EnumerationResult<CheckRun> byVoyage = await _Database.CheckRuns
                .EnumerateAsync(new CheckRunQuery { VoyageId = voyageId }, token).ConfigureAwait(false);
            foreach (CheckRun c in byVoyage.Objects) checks[c.Id] = c;
            foreach (Mission m in missions)
            {
                EnumerationResult<CheckRun> byMission = await _Database.CheckRuns
                    .EnumerateAsync(new CheckRunQuery { MissionId = m.Id }, token).ConfigureAwait(false);
                foreach (CheckRun c in byMission.Objects) checks[c.Id] = c;
            }

            List<CheckRun> active = checks.Values
                .Where(c => c.Status != CheckRunStatusEnum.Canceled).ToList();
            if (active.Count == 0) return VoyageCheckGate.NoChecks;
            if (active.Any(c => c.Status == CheckRunStatusEnum.Failed)) return VoyageCheckGate.HasFailed;
            if (active.Any(c => c.Status == CheckRunStatusEnum.Pending || c.Status == CheckRunStatusEnum.Running)) return VoyageCheckGate.HasPending;
            return VoyageCheckGate.AllGreen;
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
                Mission clonedStage = new Mission(
                    parsedTitle + " [" + templateChild.Persona + "]",
                    parsedDescription);
                clonedStage.TenantId = templateChild.TenantId;
                clonedStage.UserId = templateChild.UserId;
                clonedStage.VoyageId = templateChild.VoyageId;
                clonedStage.VesselId = templateChild.VesselId;
                clonedStage.Persona = templateChild.Persona;
                clonedStage.DependsOnMissionId = newDependency.Id;
                // Deliberately NOT inherited. StageOrder identifies a parallel stage group, and every
                // cloned chain is an independent line of work that happens to share the template's
                // shape. Copying it would group the clones together and make each chain's downstream
                // wait on every other chain's stage of the same order -- a fan-out-wide deadlock.
                clonedStage.StageOrder = null;
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
                dependent.Title = parsedTitle + " [" + dependent.Persona + "]";
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
                if (String.IsNullOrWhiteSpace(dependencyReference)) continue;
                if (!workerRootsByIndex.TryGetValue(i + 1, out Mission? workerRoot)) continue;

                Mission? resolvedDependency = ResolveArchitectDependencyTerminalStage(
                    terminalStagesByIndex,
                    terminalStagesByTitle,
                    i + 1,
                    dependencyReference);
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

                _Logging.Info(_Header + "architect sequenced worker mission " + workerRoot.Id +
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
        /// Parse structured mission definitions from an architect's output.
        /// Looks for [ARMADA:MISSION] markers in the mission diff snapshot or description.
        /// </summary>
        private List<ParsedArchitectMission> ParseArchitectOutput(Mission architectMission)
        {
            List<ParsedArchitectMission> results = new List<ParsedArchitectMission>();
            HashSet<string> seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string?[] candidateSources =
            {
                architectMission.AgentOutput,
                architectMission.DiffSnapshot,
                architectMission.Description
            };

            foreach (string? candidateSource in candidateSources)
            {
                if (String.IsNullOrWhiteSpace(candidateSource)) continue;

                string source = candidateSource.Replace("\r\n", "\n");
                ParseArchitectMissionMarkers(source, results, seenTitles);
                if (results.Count > 0) break;

                ParseArchitectSummaryLines(source, results, seenTitles);
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

        private void ParseArchitectSummaryLines(
            string source,
            List<ParsedArchitectMission> results,
            HashSet<string> seenTitles)
        {
            if (String.IsNullOrWhiteSpace(source)) return;

            string[] lines = source.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (String.IsNullOrEmpty(line)) continue;
                if (IsAgentTelemetryLine(line)) continue;
                if (IsArchitectSummaryPreambleOrFooter(line)) continue;

                if (TryParseArchitectSummaryLine(line, out string? title, out string? description))
                {
                    List<string> descriptionLines = new List<string>();
                    if (!String.IsNullOrWhiteSpace(description))
                    {
                        descriptionLines.Add(description);
                    }

                    int nextIndex = i + 1;
                    while (nextIndex < lines.Length)
                    {
                        string nextLine = lines[nextIndex].Trim();
                        if (String.IsNullOrEmpty(nextLine))
                        {
                            nextIndex++;
                            continue;
                        }

                        if (IsAgentTelemetryLine(nextLine) || IsArchitectSummaryPreambleOrFooter(nextLine))
                        {
                            nextIndex++;
                            continue;
                        }

                        if (TryParseArchitectSummaryLine(nextLine, out _, out _))
                        {
                            break;
                        }

                        descriptionLines.Add(nextLine);
                        nextIndex++;
                    }

                    TryAddParsedArchitectMission(results, seenTitles, title, String.Join("\n", descriptionLines));
                    i = nextIndex - 1;
                }
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
            (normalizedDescription, string? dependencyReference) = ExtractArchitectDependencyReference(normalizedDescription);
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

        private static (string Description, string? DependsOnReference) ExtractArchitectDependencyReference(string description)
        {
            if (String.IsNullOrWhiteSpace(description)) return ("", null);

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

            return (String.Join("\n", keptLines).Trim(), String.IsNullOrWhiteSpace(dependencyReference) ? null : dependencyReference);
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

        private static bool TryParseArchitectSummaryLine(string line, out string? title, out string? description)
        {
            title = null;
            description = null;

            if (String.IsNullOrWhiteSpace(line)) return false;

            string trimmedLine = line.Trim();
            System.Text.RegularExpressions.Match missionHeadingMatch =
                System.Text.RegularExpressions.Regex.Match(
                    trimmedLine,
                    @"^(?:\*\*)?Mission\s+\d+\s*:\s*(?<title>.+?)(?:\*\*)?(?<tail>.*)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (missionHeadingMatch.Success)
            {
                title = TrimArchitectSummaryMetadata(missionHeadingMatch.Groups["title"].Value.Trim());
                description = ParseArchitectSummaryTail(missionHeadingMatch.Groups["tail"].Value);
                return !String.IsNullOrEmpty(title);
            }

            System.Text.RegularExpressions.Match numberedMatch =
                System.Text.RegularExpressions.Regex.Match(trimmedLine, @"^\d+\.\s+(?<rest>.+)$");
            if (!numberedMatch.Success) return false;

            string rest = numberedMatch.Groups["rest"].Value.Trim();
            if (String.IsNullOrEmpty(rest)) return false;

            if (rest.StartsWith("**", StringComparison.Ordinal))
            {
                System.Text.RegularExpressions.Match boldTitleMatch =
                    System.Text.RegularExpressions.Regex.Match(rest, @"^\*\*(?<title>.+?)\*\*(?<tail>.*)$");
                if (!boldTitleMatch.Success) return false;

                title = boldTitleMatch.Groups["title"].Value.Trim();
                description = ParseArchitectSummaryTail(boldTitleMatch.Groups["tail"].Value);
                return !String.IsNullOrEmpty(title);
            }

            if (TrySplitArchitectSummaryTitleAndDescription(rest, out string? parsedTitle, out string? parsedDescription))
            {
                title = parsedTitle;
                description = parsedDescription;
                return !String.IsNullOrEmpty(title);
            }

            title = rest;
            description = "";
            return true;
        }

        private static bool TrySplitArchitectSummaryTitleAndDescription(
            string summary,
            out string? title,
            out string? description)
        {
            title = null;
            description = null;

            if (String.IsNullOrWhiteSpace(summary)) return false;

            string[] separators = { " -- ", ": " };
            foreach (string separator in separators)
            {
                int separatorIndex = summary.IndexOf(separator, StringComparison.Ordinal);
                if (separatorIndex < 0) continue;

                string titlePart = summary.Substring(0, separatorIndex).Trim();
                string descriptionPart = summary.Substring(separatorIndex + separator.Length).Trim();
                if (String.IsNullOrEmpty(titlePart) || String.IsNullOrEmpty(descriptionPart)) continue;

                title = TrimArchitectSummaryMetadata(titlePart);
                description = descriptionPart;
                return !String.IsNullOrEmpty(title);
            }

            title = TrimArchitectSummaryMetadata(summary.Trim());
            description = "";
            return !String.IsNullOrEmpty(title);
        }

        private static string ParseArchitectSummaryTail(string tail)
        {
            if (String.IsNullOrWhiteSpace(tail)) return "";

            string remaining = tail.Trim();
            while (remaining.StartsWith("(", StringComparison.Ordinal))
            {
                int closingIndex = remaining.IndexOf(')');
                if (closingIndex < 0) break;
                remaining = remaining.Substring(closingIndex + 1).TrimStart();
            }

            if (remaining.StartsWith("--", StringComparison.Ordinal))
            {
                remaining = remaining.Substring(2).TrimStart();
            }
            else if (remaining.StartsWith(":", StringComparison.Ordinal))
            {
                remaining = remaining.Substring(1).TrimStart();
            }

            return remaining.Trim();
        }

        private static string TrimArchitectSummaryMetadata(string title)
        {
            if (String.IsNullOrWhiteSpace(title)) return "";

            string trimmed = title.Trim();
            while (trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                int openIndex = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
                if (openIndex < 0) break;
                trimmed = trimmed.Substring(0, openIndex).TrimEnd();
            }

            return trimmed.Trim();
        }

        private static bool IsArchitectSummaryPreambleOrFooter(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return true;

            string normalized = line.Trim().Trim('*', '_', '`').ToLowerInvariant();
            return normalized.StartsWith("vessel context updated", StringComparison.Ordinal) ||
                normalized.StartsWith("the architect mission is complete", StringComparison.Ordinal) ||
                normalized.StartsWith("here's a summary of", StringComparison.Ordinal) ||
                normalized.StartsWith("here is a summary of", StringComparison.Ordinal) ||
                normalized.StartsWith("missions ", StringComparison.Ordinal);
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
                changedFiles = await _Git.GetChangedFilesSinceAsync(dock.WorktreePath, startCommit, token).ConfigureAwait(false);
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
                _Logging.Warn(_Header + "could not read dock start commit metadata for " + dockId + ": " + ex.Message);
                return null;
            }
        }

        private static string NormalizeMissionPath(string path)
        {
            return (path ?? String.Empty).Trim().Replace('\\', '/');
        }

        private async Task EnsureMissionInstructionsPresentAsync(
            string worktreePath,
            Mission mission,
            Captain captain,
            CancellationToken token)
        {
            if (String.IsNullOrEmpty(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            string runtimeName = captain.Runtime.ToString();
            string instructionsFileName = MissionPromptBuilder.GetInstructionsFileName(runtimeName);
            string rootInstructionsPath = Path.Combine(worktreePath, instructionsFileName);
            string instructionsRelativePath = File.Exists(rootInstructionsPath)
                ? MissionPromptBuilder.GetGeneratedInstructionsRelativePath(runtimeName)
                : instructionsFileName;
            string instructionsPath = Path.Combine(worktreePath, instructionsRelativePath);
            if (File.Exists(instructionsPath)) return;

            string snapshotPath = Path.Combine(_Settings.LogDirectory, "instructions", mission.Id + "." + instructionsFileName);
            if (!File.Exists(snapshotPath))
            {
                _Logging.Warn(_Header + "mission instructions missing at " + instructionsPath +
                    " and no snapshot exists at " + snapshotPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(instructionsPath)!);
            await File.WriteAllTextAsync(instructionsPath, await File.ReadAllTextAsync(snapshotPath, token).ConfigureAwait(false), token).ConfigureAwait(false);
            _Logging.Warn(_Header + "restored missing mission instructions from snapshot to " + instructionsPath);
        }

        /// <summary>
        /// Extracts the body of the Judge's "## Suggested Follow-ups" section, trimmed.
        /// Returns null when the section is missing, empty, or contains only the
        /// `(none)` sentinel. The body is everything between this heading and the next
        /// `## ` heading (or end of output). Used by the audit-flag wiring to decide
        /// whether to surface a PASS verdict to the orchestrator's audit drain.
        /// </summary>
        private static string? ExtractSuggestedFollowUps(string? agentOutput)
        {
            if (String.IsNullOrEmpty(agentOutput)) return null;
            string normalized = agentOutput.Replace("\r\n", "\n");
            string[] lines = normalized.Split('\n');
            int startIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("## ", StringComparison.Ordinal) &&
                    trimmed.Substring(3).Trim().Equals("Suggested Follow-ups", StringComparison.OrdinalIgnoreCase))
                {
                    startIndex = i + 1;
                    break;
                }
            }
            if (startIndex < 0) return null;

            System.Text.StringBuilder body = new System.Text.StringBuilder();
            for (int i = startIndex; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("## ", StringComparison.Ordinal)) break;
                body.Append(line);
                body.Append('\n');
            }
            string text = body.ToString().Trim();
            if (String.IsNullOrEmpty(text)) return null;
            // The prompt asks captains to write `(none)` when there are no follow-ups.
            if (text.Equals("(none)", StringComparison.OrdinalIgnoreCase)) return null;
            return text;
        }

        /// <summary>
        /// When the Judge wants the orchestrator to look at the upstream Worker's merge
        /// entry (NEEDS_REVISION verdict, or PASS with non-empty Suggested Follow-ups),
        /// mark the entry deep-picked so <c>armada_drain_audit_queue</c> surfaces it.
        /// Worker mission id comes from the Judge's <see cref="Mission.DependsOnMissionId"/>;
        /// best-effort lookup -- enumeration scan is fine for typical merge_queue volume.
        /// </summary>
        private async Task TryFlagUpstreamMergeEntryForAuditAsync(Mission judgeMission, JudgeVerdict verdict, string? suggestedFollowUps, CancellationToken token)
        {
            try
            {
                if (String.IsNullOrEmpty(judgeMission.DependsOnMissionId)) return;
                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                MergeEntry? upstream = null;
                foreach (MergeEntry e in entries)
                {
                    if (!String.Equals(e.MissionId, judgeMission.DependsOnMissionId, StringComparison.Ordinal)) continue;
                    if (upstream == null || e.CreatedUtc > upstream.CreatedUtc) upstream = e;
                }
                if (upstream == null) return;

                string verdictLabel = verdict switch
                {
                    JudgeVerdict.NeedsRevision => "NEEDS_REVISION",
                    JudgeVerdict.Pass => "PASS_with_followups",
                    _ => verdict.ToString()
                };
                string notesPayload = "Judge " + verdictLabel + " (mission " + judgeMission.Id + ")";
                if (!String.IsNullOrEmpty(suggestedFollowUps))
                {
                    notesPayload += "\n\n## Suggested Follow-ups\n" + suggestedFollowUps;
                }

                upstream.AuditDeepPicked = true;
                upstream.AuditDeepNotes = notesPayload;
                upstream.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(upstream, token).ConfigureAwait(false);
                _Logging.Info(_Header + "judge mission " + judgeMission.Id + " flagged upstream merge entry " + upstream.Id + " for audit (" + verdictLabel + ")");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to flag upstream merge entry from judge mission " + judgeMission.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Decide whether a Judge mission that produced no parseable verdict should be re-run in
        /// place. A missing verdict is treated as an operational miss (retryable) and is distinct
        /// from an explicit FAIL / NEEDS_REVISION verdict, which is terminal and never retried.
        /// Retries are bounded by <paramref name="priorRetryCount"/>.
        /// </summary>
        /// <param name="hasParseableVerdict">True when a PASS / FAIL / NEEDS_REVISION verdict was extracted.</param>
        /// <param name="priorRetryCount">Number of in-place re-runs already consumed for this mission.</param>
        /// <returns>True when the mission should be re-dispatched in place.</returns>
        private static bool ShouldRetryMissingJudgeVerdict(bool hasParseableVerdict, int priorRetryCount)
        {
            if (hasParseableVerdict) return false;
            return priorRetryCount < _MaxMissingJudgeVerdictRetries;
        }

        private static JudgeVerdict ParseJudgeVerdict(string? agentOutput)
        {
            if (String.IsNullOrEmpty(agentOutput)) return JudgeVerdict.None;

            string[] lines = agentOutput.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim().Trim('\r');
                if (String.IsNullOrEmpty(line)) continue;

                JudgeVerdict? signalVerdict = ParseStructuredJudgeVerdictSignal(line);
                if (signalVerdict.HasValue) return signalVerdict.Value;

                if (IsAgentTelemetryLine(line)) continue;

                string normalized = line.Trim().Trim('*', '_', '`', '#', '>', '-', ' ');
                JudgeVerdict? explicitVerdict = ParseExplicitJudgeVerdictLine(normalized);
                if (explicitVerdict.HasValue) return explicitVerdict.Value;
            }

            return JudgeVerdict.None;
        }

        private bool TryValidateJudgePassOutput(string? agentOutput, out string? failureReason)
        {
            failureReason = null;

            if (String.IsNullOrWhiteSpace(agentOutput))
            {
                failureReason = "Judge PASS verdict missing review output";
                return false;
            }

            List<string> missingSections = new List<string>();
            if (!ContainsJudgeReviewSection(agentOutput, "Completeness")) missingSections.Add("Completeness");
            if (!ContainsJudgeReviewSection(agentOutput, "Correctness")) missingSections.Add("Correctness");
            if (!ContainsJudgeReviewSection(agentOutput, "Tests")) missingSections.Add("Tests");
            if (!ContainsJudgeReviewSection(agentOutput, "Failure Modes")) missingSections.Add("Failure Modes");

            if (missingSections.Count > 0)
            {
                failureReason = "Judge PASS verdict missing required review sections: " + String.Join(", ", missingSections);
                return false;
            }

            string substantiveReview = ExtractJudgeNarrative(agentOutput);
            if (substantiveReview.Length < 120)
            {
                failureReason = "Judge PASS verdict review is too short to justify approval";
                return false;
            }

            return true;
        }

        private static bool ContainsJudgeReviewSection(string agentOutput, string sectionName)
        {
            if (String.IsNullOrWhiteSpace(agentOutput) || String.IsNullOrWhiteSpace(sectionName)) return false;

            string pattern =
                @"(?im)^\s*(?:#{1,6}\s*)?(?:[-*]\s*)?(?:\d+\.\s*)?(?:\*\*|__|`)?"
                + System.Text.RegularExpressions.Regex.Escape(sectionName)
                + @"(?:\*\*|__|`)?\s*(?::|-)?(?:\s|$)";

            return System.Text.RegularExpressions.Regex.IsMatch(agentOutput, pattern);
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
                if (ParseStructuredJudgeVerdictSignal(line).HasValue) continue;

                string normalized = line.Trim('*', '_', '`', '#', '>', '-', ' ');
                if (ParseExplicitJudgeVerdictLine(normalized).HasValue) continue;

                lines.Add(line);
            }

            return String.Join(" ", lines);
        }

        private static JudgeVerdict? ParseStructuredJudgeVerdictSignal(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;

            // Accept both the canonical standalone line ([ARMADA:VERDICT] PASS) and the progress-
            // signal form the runtime echoes for an in-flight verdict ([verdict] PASS). The latter
            // is the extraction fallback: when the final standalone line is dropped (for example a
            // mid-review wakeup terminated the mission) but a verdict was reached and surfaced as a
            // progress signal, honor it rather than failing the mission for a missing verdict.
            System.Text.RegularExpressions.Match signal = System.Text.RegularExpressions.Regex.Match(
                line.Trim(),
                @"^\[(?:ARMADA:)?VERDICT\]\s+(?<verdict>PASS|FAIL|NEEDS_REVISION)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!signal.Success) return null;

            return signal.Groups["verdict"].Value.ToUpperInvariant() switch
            {
                "PASS" => JudgeVerdict.Pass,
                "FAIL" => JudgeVerdict.Fail,
                "NEEDS_REVISION" => JudgeVerdict.NeedsRevision,
                _ => null
            };
        }

        private static JudgeVerdict? ParseExplicitJudgeVerdictLine(string normalizedLine)
        {
            if (String.IsNullOrEmpty(normalizedLine)) return null;

            const System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            string candidate = normalizedLine.Trim();
            const string verdictSuffixPattern = @"(?:\s*$|\s*[\.,:;!?](?:\s+.+)?$|\s*-(?!-)\s+.+$)";

            System.Text.RegularExpressions.Match labeledVerdict = System.Text.RegularExpressions.Regex.Match(
                candidate,
                @"^VERDICT\s*(?::|=|-|IS)?\s*(?:\*\*|__|`)?(?<verdict>PASS|FAIL|NEEDS_REVISION)(?:\*\*|__|`)?"
                + verdictSuffixPattern,
                options);
            if (labeledVerdict.Success)
                return labeledVerdict.Groups["verdict"].Value.ToUpperInvariant() switch
                {
                    "PASS" => JudgeVerdict.Pass,
                    "FAIL" => JudgeVerdict.Fail,
                    "NEEDS_REVISION" => JudgeVerdict.NeedsRevision,
                    _ => null
                };

            System.Text.RegularExpressions.Match inlineLabeledVerdict = System.Text.RegularExpressions.Regex.Match(
                candidate,
                @"\bVERDICT\s*(?::|=|-|IS)?\s*(?:\*\*|__|`)?(?<verdict>PASS|FAIL|NEEDS_REVISION)(?:\*\*|__|`)?"
                + verdictSuffixPattern,
                options);
            if (inlineLabeledVerdict.Success)
                return inlineLabeledVerdict.Groups["verdict"].Value.ToUpperInvariant() switch
                {
                    "PASS" => JudgeVerdict.Pass,
                    "FAIL" => JudgeVerdict.Fail,
                    "NEEDS_REVISION" => JudgeVerdict.NeedsRevision,
                    _ => null
                };

            System.Text.RegularExpressions.Match bareVerdict = System.Text.RegularExpressions.Regex.Match(
                candidate,
                @"^(?:\*\*|__|`)?(?<verdict>PASS|FAIL|NEEDS_REVISION)(?:\*\*|__|`)?"
                + verdictSuffixPattern,
                options);
            if (bareVerdict.Success)
                return bareVerdict.Groups["verdict"].Value.ToUpperInvariant() switch
                {
                    "PASS" => JudgeVerdict.Pass,
                    "FAIL" => JudgeVerdict.Fail,
                    "NEEDS_REVISION" => JudgeVerdict.NeedsRevision,
                    _ => null
                };

            return null;
        }

        private async Task EmitMissionOutcomeTelemetryAsync(Mission mission, Captain captain, CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            (string eventType, string eventMessage) = BuildMissionOutcomeEvent(mission);

            try
            {
                ArmadaEvent outcomeEvent = new ArmadaEvent(eventType, eventMessage);
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

        private async Task EmitContextPackUsageTelemetryAsync(Mission mission, Captain captain, CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            try
            {
                string missionLogDir = Path.Combine(_Settings.LogDirectory, "missions");
                PackUsageMiner miner = new PackUsageMiner(missionLogDir);
                PackUsageTriple usage = await miner.MineAsync(mission, token).ConfigureAwait(false);

                ArmadaEvent usageEvent = new ArmadaEvent(
                    "mission.context_pack_usage",
                    "Context pack usage: " + usage.ContextPackCompliance);
                usageEvent.TenantId = mission.TenantId;
                usageEvent.UserId = mission.UserId;
                usageEvent.EntityType = "mission";
                usageEvent.EntityId = mission.Id;
                usageEvent.CaptainId = captain.Id;
                usageEvent.MissionId = mission.Id;
                usageEvent.VesselId = mission.VesselId;
                usageEvent.VoyageId = mission.VoyageId;
                usageEvent.Payload = JsonSerializer.Serialize(new
                {
                    usage.MissionId,
                    usage.LogAvailable,
                    usage.ContextPackStaged,
                    usage.ContextPackCompliance,
                    usage.FirstContextPackReadOffset,
                    usage.FirstSearchToolOffset,
                    usage.SearchToolCallCount,
                    usage.FilesReadFromPack,
                    usage.FilesIgnoredFromPack,
                    usage.FilesGrepDiscovered,
                    usage.FilesEdited
                });

                await _Database.Events.CreateAsync(usageEvent, token).ConfigureAwait(false);
            }
            catch (Exception evtEx)
            {
                _Logging.Warn(_Header + "error emitting context pack usage event for " + mission.Id + ": " + evtEx.Message);
            }
        }

        private static (SignalTypeEnum Type, string Payload) BuildMissionOutcomeSignal(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            return mission.Status switch
            {
                MissionStatusEnum.Complete => (SignalTypeEnum.Completion, "Mission completed: " + mission.Title),
                MissionStatusEnum.PullRequestOpen => (SignalTypeEnum.Completion, "Pull request open: " + mission.Title),
                MissionStatusEnum.Failed => (SignalTypeEnum.Error, BuildFailurePayload("Mission failed: ", mission)),
                MissionStatusEnum.LandingFailed => (SignalTypeEnum.Error, BuildFailurePayload("Landing failed: ", mission)),
                MissionStatusEnum.Cancelled => (SignalTypeEnum.Error, BuildFailurePayload("Mission cancelled: ", mission)),
                _ => (SignalTypeEnum.Completion, "Work produced: " + mission.Title)
            };
        }

        private static (string EventType, string EventMessage) BuildMissionOutcomeEvent(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            return mission.Status switch
            {
                MissionStatusEnum.Failed => ("mission.failed", BuildFailurePayload("Mission failed: ", mission)),
                MissionStatusEnum.LandingFailed => ("mission.landing_failed", BuildFailurePayload("Landing failed: ", mission)),
                MissionStatusEnum.Cancelled => ("mission.cancelled", BuildFailurePayload("Mission cancelled: ", mission)),
                _ => ("mission.work_produced", "Work produced: " + mission.Title)
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
                _Logging.Warn(_Header + "could not project architect mission definitions into log for " + architectMission.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Whether a captain satisfies optional persona allow-list and optional preferred model
        /// (literal or tier selector), ignoring idle state. Used for hard pins and pipeline
        /// stage pin resolution.
        /// </summary>
        /// <param name="captain">Captain row to evaluate.</param>
        /// <param name="missionPersona">Mission persona, if any.</param>
        /// <param name="preferredModel">Preferred model or tier selector, if any.</param>
        /// <param name="modelTierSettings">Optional tier membership configuration; null uses built-in defaults.</param>
        /// <returns>True when the captain may run the mission under the given pins.</returns>
        public static bool CaptainSatisfiesPreferredRouting(
            Captain captain,
            string? missionPersona,
            string? preferredModel,
            ModelTierSettings? modelTierSettings = null)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            if (!String.IsNullOrEmpty(preferredModel))
            {
                if (PreferredModelTierSelector.IsTierSelector(preferredModel))
                {
                    if (!PreferredModelTierSelector.ModelMatchesTierOrAbove(captain.Model, preferredModel, modelTierSettings))
                        return false;
                }
                else if (!String.Equals(captain.Model, preferredModel, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!String.IsNullOrEmpty(missionPersona))
            {
                if (String.IsNullOrEmpty(captain.AllowedPersonas))
                    return true;
                if (captain.AllowedPersonas.Contains("\"" + missionPersona + "\"", StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            return true;
        }

        private async Task<Captain?> FindAvailableCaptainAsync(Mission mission, CancellationToken token)
        {
            string? persona = mission?.Persona;
            string? preferredModel = mission?.PreferredModel;

            List<string> specialistPersonas = _Settings.ModelTier.SpecialistPersonas;
            IReadOnlyDictionary<string, List<string>> withinTierPreferenceOrder = _Settings.ModelTier.WithinTierPreferenceOrder;
            bool isSpecialist = _Settings.ModelTier.IsSpecialistPersona(persona);

            // Only idle captains are eligible for assignment
            List<Captain> idleCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle, token).ConfigureAwait(false);
            if (idleCaptains.Count == 0)
                return null;

            List<Captain> assignableCaptains = new List<Captain>();
            foreach (Captain idleCaptain in idleCaptains)
            {
                if (!_CaptainQuarantine.IsQuarantined(idleCaptain))
                {
                    assignableCaptains.Add(idleCaptain);
                }
            }

            idleCaptains = assignableCaptains;
            if (idleCaptains.Count == 0)
                return null;

            // Model filter: tier selector (random peer selection) or literal match
            if (!String.IsNullOrEmpty(preferredModel))
            {
                if (PreferredModelTierSelector.IsTierSelector(preferredModel))
                {
                    string? selectedModel = PreferredModelTierSelector.SelectModel(
                        preferredModel, idleCaptains, persona, n => Random.Shared.Next(n), specialistPersonas, withinTierPreferenceOrder, _Settings.ModelTier, mission?.CapabilityHint);
                    if (selectedModel == null) return null;
                    List<Captain> filtered = new List<Captain>();
                    foreach (Captain captain in idleCaptains)
                    {
                        if (!String.IsNullOrEmpty(captain.Model) &&
                            String.Equals(captain.Model, selectedModel, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered.Add(captain);
                        }
                    }
                    if (filtered.Count == 0) return null;
                    idleCaptains = filtered;
                }
                else
                {
                    // Literal/concrete model pin: try exact match first.
                    List<Captain> filtered = new List<Captain>();
                    foreach (Captain captain in idleCaptains)
                    {
                        if (!String.IsNullOrEmpty(captain.Model) &&
                            String.Equals(captain.Model, preferredModel, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered.Add(captain);
                        }
                    }
                    if (filtered.Count > 0)
                    {
                        idleCaptains = filtered;
                    }
                    else
                    {
                        // No exact match: classify the pinned model into a tier and re-resolve.
                        string? classifiedTier = PreferredModelTierSelector.ClassifyModel(preferredModel, _Settings.ModelTier);
                        if (classifiedTier != null)
                        {
                            string? fallbackModel = PreferredModelTierSelector.SelectModel(
                                classifiedTier, idleCaptains, persona, n => Random.Shared.Next(n), specialistPersonas, withinTierPreferenceOrder, _Settings.ModelTier, mission?.CapabilityHint);
                            if (fallbackModel == null) return null;
                            List<Captain> tierFiltered = new List<Captain>();
                            foreach (Captain captain in idleCaptains)
                            {
                                if (!String.IsNullOrEmpty(captain.Model) &&
                                    String.Equals(captain.Model, fallbackModel, StringComparison.OrdinalIgnoreCase))
                                {
                                    tierFiltered.Add(captain);
                                }
                            }
                            if (tierFiltered.Count == 0) return null;
                            idleCaptains = tierFiltered;
                        }
                        // Else: unclassified concrete model -- leave idleCaptains unrestricted;
                        // persona filtering below narrows to compatible candidates.
                    }
                }
            }
            else
            {
                // No preferredModel: route through the unified selector with a sensible
                // default tier (high for specialists, mid for everyone else) so a non-specialist
                // mission is never handed an idle high-tier captain while a mid/low one is free.
                // If the selector finds no classified captain, fall through unrestricted so
                // captains carrying custom/unclassified models still receive work.
                string defaultTier = isSpecialist ? PreferredModelTierSelector.HighTier : PreferredModelTierSelector.MidTier;
                string? defaultedModel = PreferredModelTierSelector.SelectModel(
                    defaultTier, idleCaptains, persona, n => Random.Shared.Next(n), specialistPersonas, withinTierPreferenceOrder, _Settings.ModelTier, mission?.CapabilityHint);
                if (defaultedModel != null)
                {
                    List<Captain> filtered = new List<Captain>();
                    foreach (Captain captain in idleCaptains)
                    {
                        if (!String.IsNullOrEmpty(captain.Model) &&
                            String.Equals(captain.Model, defaultedModel, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered.Add(captain);
                        }
                    }
                    if (filtered.Count > 0)
                        idleCaptains = filtered;
                }
            }

            // If no persona requirement, return any idle captain
            if (String.IsNullOrEmpty(persona))
                return idleCaptains[0];

            // Filter by AllowedPersonas (null = any persona is allowed)
            List<Captain> eligible = new List<Captain>();
            foreach (Captain captain in idleCaptains)
            {
                if (String.IsNullOrEmpty(captain.AllowedPersonas))
                {
                    // No restriction -- captain can fill any persona
                    eligible.Add(captain);
                }
                else
                {
                    // Check if the persona is in the allowed list
                    // AllowedPersonas is a JSON array string, e.g. '["Worker","Judge"]'
                    if (captain.AllowedPersonas.Contains("\"" + persona + "\"", StringComparison.OrdinalIgnoreCase))
                    {
                        eligible.Add(captain);
                    }
                }
            }

            if (eligible.Count == 0)
            {
                return null;
            }

            // Prefer captains whose PreferredPersona matches
            foreach (Captain captain in eligible)
            {
                if (!String.IsNullOrEmpty(captain.PreferredPersona) &&
                    String.Equals(captain.PreferredPersona, persona, StringComparison.OrdinalIgnoreCase))
                {
                    return captain;
                }
            }

            // No preferred match -- return first eligible
            return eligible[0];
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
            if (String.IsNullOrEmpty(mission.Description)) return false;

            string description = mission.Description.ToLowerInvariant();
            bool requestsDeferredExecution =
                description.Contains("after both implementation missions complete") ||
                description.Contains("sequential after both implementation missions") ||
                description.Contains("after the implementation missions land") ||
                description.Contains("after the implementation details are settled") ||
                description.Contains("after implementation details are settled") ||
                description.Contains("after the implementation details are finalized");

            if (!requestsDeferredExecution) return false;

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId, token).ConfigureAwait(false);
            return voyageMissions.Any(m =>
                m.Id != mission.Id &&
                String.Equals(m.Persona, "Worker", StringComparison.OrdinalIgnoreCase) &&
                m.Status != MissionStatusEnum.Complete &&
                m.Status != MissionStatusEnum.WorkProduced &&
                m.Status != MissionStatusEnum.Failed &&
                m.Status != MissionStatusEnum.Cancelled &&
                m.Status != MissionStatusEnum.LandingFailed);
        }

        private async Task<Mission> RequireReviewMissionAsync(string missionId, CancellationToken token)
        {
            Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null) throw new InvalidOperationException("Mission not found: " + missionId);
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

        /// <summary>
        /// Append Armada-owned validation lifecycle evidence to the same readable mission log
        /// used by every runtime. This makes a terminal gate failure visible where operators are
        /// already following agent progress instead of leaving it only in FailureReason.
        /// </summary>
        private async Task AppendMissionActivityAsync(string missionId, string message, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(missionId) || String.IsNullOrWhiteSpace(message)) return;

            try
            {
                string directory = System.IO.Path.Combine(_Settings.LogDirectory, "missions");
                System.IO.Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, missionId + ".log");
                const int maximumMessageLength = 24000;
                string boundedMessage = message.Length <= maximumMessageLength
                    ? message
                    : message.Substring(0, maximumMessageLength) + "\n...(truncated)";
                string record = "[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") +
                    "] [ARMADA:ACTIVITY] " + boundedMessage + Environment.NewLine;
                await System.IO.File.AppendAllTextAsync(path, record, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not append mission activity for " + missionId + ": " + ex.Message);
            }
        }

        private static string? NormalizeReviewComment(string? comment)
        {
            if (String.IsNullOrWhiteSpace(comment)) return null;
            return comment.Trim();
        }

        /// <summary>
        /// Build the ReviewComment stored on a Judge mission that did not pass. The Judge's full
        /// written review (AgentOutput) is the actionable critique; the one-line FailureReason is
        /// only a status. Prefer the review body, fall back to the failure reason, and bound the
        /// length so the downstream rescue brief stays a reasonable size.
        /// </summary>
        private static string BuildJudgeReviewComment(string? agentOutput, string? failureReason)
        {
            string review = NormalizeReviewComment(agentOutput)
                ?? NormalizeReviewComment(failureReason)
                ?? "Judge requested revision but recorded no written review.";

            const int maxChars = 8000;
            if (review.Length > maxChars)
                review = review.Substring(0, maxChars) + "\n...(truncated)";

            return review;
        }

        private static string BuildReviewDeniedFailureReason(string comment)
        {
            return "Review denied: " + comment;
        }

        /// <summary>
        /// True when the vessel's bare-repo path and the operator's working checkout resolve to the
        /// same directory. Compared on fully-resolved paths so ".." segments, trailing separators and
        /// case differences cannot hide the collision; falls back to a trimmed comparison when the
        /// path cannot be resolved.
        /// </summary>
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

        private static string BuildDodFailureReason(DefinitionOfDoneResult result)
        {
            string label = result.CommandLabel ?? "unknown";
            string tail = String.IsNullOrWhiteSpace(result.OutputTail) ? "" : "\n" + result.OutputTail.Trim();
            DefinitionOfDoneFailureClassEnum failureClass = result.FailureClass
                ?? DefinitionOfDoneFailureClassEnum.Infra;
            return "DoD gate failed: classification=" + failureClass + "; " + label +
                " command exited " + result.ExitCode + tail;
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

        private static string? ResolveGitInfoExcludePath(string worktreePath)
        {
            if (String.IsNullOrEmpty(worktreePath)) return null;

            string gitPath = Path.Combine(worktreePath, ".git");
            if (Directory.Exists(gitPath))
            {
                return Path.Combine(gitPath, "info", "exclude");
            }

            if (!File.Exists(gitPath))
            {
                return null;
            }

            string gitPointer = File.ReadAllText(gitPath).Trim();
            const string prefix = "gitdir:";
            if (!gitPointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string gitDir = gitPointer.Substring(prefix.Length).Trim();
            if (!Path.IsPathRooted(gitDir))
            {
                gitDir = Path.GetFullPath(Path.Combine(worktreePath, gitDir));
            }

            return Path.Combine(gitDir, "info", "exclude");
        }

        #endregion
    }
}
