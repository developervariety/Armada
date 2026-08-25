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

        /// <summary>
        /// Maximum in-place re-runs of a Judge mission whose PASS is held because its independent
        /// Checks are still Pending or Running. After this budget, the PASS is rejected as
        /// unresolved rather than waiting forever.
        /// </summary>
        private const int _MaxJudgeCheckWaitRetries = 3;

        /// <summary>
        /// Marker a Judge review must contain to document an environmental exclusion when no
        /// independent Checks exist (rule 31). Without it, a PASS with no Checks is rejected.
        /// </summary>
        private const string _JudgeCheckExclusionMarker = "[JUDGE-CHECK-EXCLUSION]";

        /// <summary>
        /// Failure reason for a Judge PASS rejected because no green independent Checks are
        /// attached and no exclusion was documented. It must not instruct the captain to run
        /// armada_run_check: captains receive the local MCP connection, but Check ownership stays
        /// with the operator unless the mission explicitly delegates it (six High papercuts).
        /// </summary>
        internal static readonly string JudgeNoChecksFailureReason =
            "Judge PASS rejected: no green independent Checks attached. Independent Checks are attached by the operator, not by captains. " +
            "To complete the review without them, document an environmental exclusion with the " +
            _JudgeCheckExclusionMarker + " marker in the review, or ask the operator to attach Build+UnitTest Checks.";

        /// <summary>
        /// A diff section must exceed this size before it is treated as bulk generated data that
        /// carries no review signal. Small data files stay reviewable.
        /// </summary>
        private const int _GeneratedDataElideThresholdChars = 12000;

        /// <summary>
        /// Path markers that identify generated output trees whose large data files carry no
        /// review signal (snapshot/bundle regeneration).
        /// </summary>
        private static readonly string[] _GeneratedDataPathMarkers = new string[]
        {
            "/output/", "-export", "/export/", "/bundle", "/generated", "/Output/"
        };

        /// <summary>
        /// Extensions treated as data files for the bulk-generated-data elision policy.
        /// </summary>
        private static readonly string[] _GeneratedDataExtensions = new string[]
        {
            ".json", ".csv", ".xml", ".dat", ".txt"
        };

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

            // Captured for the stage-base check after provisioning: a stage must be cut from the
            // commit its predecessor actually produced, and proving that needs the hash here.
            string? upstreamCommitHash = null;
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
                upstreamCommitHash = dependency.CommitHash;

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

            // Resolve preferred captain from voyage overrides or persona defaults before assignment.
            await ResolvePreferredCaptainAsync(mission, token).ConfigureAwait(false);

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

            // Prove the stage is cut from its predecessor's commit before a captain works in it.
            // Inheriting a branch NAME is not the same as inheriting its commit: a local ref can
            // predate the upstream stage's push, and the worktree then looks correct while missing
            // the work. One Worker's dock was cut without the preceding stage's commit, rebuilt on
            // a base still carrying errors that stage had already fixed, failed on them, and took
            // ten downstream missions with it - and every symptom pointed at the Worker's code.
            if (!await VerifyStageBaseAsync(mission, dock, captain, upstreamCommitHash, dependencyIsCrossVessel, preserveInheritedBranch, token).ConfigureAwait(false))
            {
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
        public async Task<int> RecoverDanglingHandoffsAsync(CancellationToken token = default)
        {
            int redriven = 0;
            List<Mission> workProduced = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.WorkProduced, token).ConfigureAwait(false);

            foreach (Mission produced in workProduced)
            {
                if (String.IsNullOrEmpty(produced.VoyageId)) continue;
                if (String.Equals(produced.Persona, "Architect", StringComparison.OrdinalIgnoreCase)) continue;

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
                    redriven++;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error re-driving handoff for mission " + produced.Id + ": " + ex.Message);
                }
            }

            return redriven;
        }

        /// <inheritdoc />
        public async Task<Mission> ApproveReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, bool conditional = false, CancellationToken token = default)
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

                // Entering a success state supersedes any earlier attempt's failure reason.
                mission.FailureReason = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                await TryHandoffToNextStageAsync(mission, token).ConfigureAwait(false);

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

            // Entering a success state supersedes any earlier attempt's failure reason.
            mission.FailureReason = null;
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
        public async Task<Mission> DenyReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, ReviewDenyActionEnum? actionOverride = null, CancellationToken token = default)
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

            // "More Work Required" and "Deny" surface as an explicit action from the reviewer; fall back to the
            // mission's configured deny action when the caller does not specify one.
            ReviewDenyActionEnum effectiveAction = actionOverride ?? mission.ReviewDenyAction;

            if (effectiveAction == ReviewDenyActionEnum.FailPipeline)
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
        /// Detect a captain "false complete" event: the captain ended its run within
        /// seconds having done no real work, and the mission would otherwise be
        /// accepted as WorkProduced with an empty diff. Two provider-side flavors are
        /// caught:
        /// <list type="bullet">
        /// <item>The GLM 5.2 flavor reads AGENTS.md, emits
        /// [ARMADA:RESULT] COMPLETE, and exits cleanly.</item>
        /// <item>The DeepSeek V4 Pro flavor reads AGENTS.md and exits 0
        /// after a brief acknowledgment, without any result marker.</item>
        /// </list>
        /// Both reach WorkProduced with an empty diff and a tiny AgentOutput, which the
        /// pipeline downstream would otherwise accept as real progress.
        /// </summary>
        /// <summary>
        /// Confirm a downstream pipeline stage's checkout contains its predecessor's commit.
        /// </summary>
        /// <remarks>
        /// A verdict of BaseMissing fails the mission LOUDLY rather than letting the captain start.
        /// The alternative is what used to happen: the stage runs, fails on problems that were
        /// already fixed upstream, and the diagnosis begins at the stage's own diff - which is
        /// correct - while the real fault is the checkout it was handed.
        /// </remarks>
        /// <returns>True when the stage may proceed.</returns>
        private async Task<bool> VerifyStageBaseAsync(
            Mission mission,
            Dock dock,
            Captain captain,
            string? upstreamCommitHash,
            bool dependencyIsCrossVessel,
            bool stageContinuesUpstreamBranch,
            CancellationToken token)
        {
            bool? containsUpstream = null;

            bool applicable = !String.IsNullOrEmpty(mission.DependsOnMissionId)
                && !dependencyIsCrossVessel
                && stageContinuesUpstreamBranch
                && !String.IsNullOrWhiteSpace(upstreamCommitHash);

            if (applicable && _Git != null && !String.IsNullOrWhiteSpace(dock.WorktreePath))
            {
                try
                {
                    containsUpstream = await _Git.TryIsAncestorAsync(
                        dock.WorktreePath!, upstreamCommitHash!, "HEAD", token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Leave the verdict Unverifiable rather than guessing. A failed ancestry probe
                    // is not evidence that the base is wrong.
                    _Logging.Warn(_Header + "stage-base ancestry probe failed for mission " + mission.Id + ": " + ex.Message);
                }
            }

            StageBaseVerdictEnum verdict = StageBaseVerifier.Evaluate(
                mission.DependsOnMissionId,
                upstreamCommitHash,
                dependencyIsCrossVessel,
                containsUpstream,
                stageContinuesUpstreamBranch);

            if (verdict == StageBaseVerdictEnum.BaseMissing)
            {
                string reason = StageBaseVerifier.BuildBaseMissingReason(
                    mission.DependsOnMissionId, upstreamCommitHash, mission.BranchName);

                _Logging.Warn(_Header + "mission " + mission.Id + " " + reason);

                mission.AssignmentState = MissionAssignmentStateEnum.Failed;
                mission.Status = MissionStatusEnum.Failed;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.FailureReason = reason;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                await AppendMissionActivityAsync(mission.Id, "validation failed: " + reason, token).ConfigureAwait(false);

                await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);
                return false;
            }

            if (verdict == StageBaseVerdictEnum.Unverifiable && applicable)
            {
                // Say so. A base that could not be proved must not read as one that was.
                _Logging.Warn(_Header + "mission " + mission.Id + " stage base UNVERIFIED against upstream commit "
                    + upstreamCommitHash + "; proceeding without proof");
            }

            return true;
        }

        /// <summary>
        /// Render a change set for a failure record, bounded so a wide diff cannot flood the field.
        /// </summary>
        /// <param name="changedPaths">Paths the mission changed.</param>
        /// <returns>Readable description of the change set.</returns>
        private static string DescribeChangedPaths(IReadOnlyList<string> changedPaths)
        {
            if (changedPaths == null || changedPaths.Count == 0) return "(no files changed).";

            const int maxListed = 10;
            int listed = Math.Min(maxListed, changedPaths.Count);
            string joined = String.Join(", ", changedPaths.Take(listed));
            if (changedPaths.Count > listed)
                joined += ", and " + (changedPaths.Count - listed) + " more";

            return changedPaths.Count + " file(s): " + joined + ".";
        }

        /// <summary>
        /// The line a Worker or TestEngineer writes to claim it finished its mission.
        /// </summary>
        internal const string CompletionMarker = "[ARMADA:RESULT] COMPLETE";

        /// <summary>
        /// The line a Judge writes instead. A Judge delivers a verdict rather than a commit,
        /// so its verdict IS its completion claim.
        /// </summary>
        internal const string VerdictMarker = "[ARMADA:VERDICT]";

        /// <summary>
        /// Report whether captain output claims the mission finished, by whichever marker the
        /// persona uses. Its ABSENCE is the signal, and it is the only one the platform has:
        /// a captain whose provider stream dies mid-run leaves no other trace, because the
        /// runtime wrapper still exits 0.
        /// </summary>
        /// <remarks>
        /// Every persona that claims completion must be represented here. A persona whose
        /// marker is missing from this check reads as a captain that never finished, and its
        /// missions fail as no-ops however well they ran. Architect is exempt one level up:
        /// it claims completion with [ARMADA:MISSION] blocks that the handoff parses itself.
        /// </remarks>
        internal static bool HasCompletionMarker(string? agentOutput)
        {
            if (String.IsNullOrEmpty(agentOutput)) return false;
            return agentOutput.Contains(CompletionMarker, StringComparison.Ordinal)
                || agentOutput.Contains(VerdictMarker, StringComparison.Ordinal);
        }

        internal static bool DetectNoOpCompletion(Mission mission, TimeSpan runtime, int diffLineCount, int agentOutputLength, bool hasAgentOutput)
        {
            return DetectNoOpCompletion(mission, runtime, diffLineCount, agentOutputLength, hasAgentOutput, diffLineCount > 0);
        }

        /// <summary>
        /// Detect a false completion using changes made during this dock assignment.
        /// The full branch diff can contain work from an earlier rescue or pipeline stage,
        /// so it must not be used as proof that the current captain produced work.
        /// </summary>
        internal static bool DetectNoOpCompletion(
            Mission mission,
            TimeSpan runtime,
            int diffLineCount,
            int agentOutputLength,
            bool hasAgentOutput,
            bool hasChangesSinceDockStart)
        {
            return DetectNoOpCompletion(
                mission,
                runtime,
                diffLineCount,
                agentOutputLength,
                hasAgentOutput,
                hasChangesSinceDockStart,
                HasCompletionMarker(mission?.AgentOutput));
        }

        /// <summary>
        /// Detect a false completion using changes made during this dock assignment and
        /// whether the captain claimed completion at all.
        /// </summary>
        /// <remarks>
        /// Runtime alone cannot discriminate. A captain can work for minutes, read the whole
        /// repository, and still deliver nothing when its provider stream dies mid-run -- the
        /// runtime wrapper exits 0 either way, so a long run is not evidence of a result.
        /// </remarks>
        internal static bool DetectNoOpCompletion(
            Mission mission,
            TimeSpan runtime,
            int diffLineCount,
            int agentOutputLength,
            bool hasAgentOutput,
            bool hasChangesSinceDockStart,
            bool hasCompletionMarker)
        {
            if (mission == null) return false;

            // Architect decomposition missions legitimately produce no code diff while
            // still producing a downstream mission plan; they are always exempt.
            if (String.Equals(mission.Persona, "Architect", StringComparison.OrdinalIgnoreCase)) return false;

            // A real captain writes a result line and an AgentOutput body. A unit-test
            // stub does not set AgentOutput at all. The presence of an AgentOutput is
            // the signal that a real captain ran (even briefly). Without that signal we
            // cannot distinguish a stub from a false-complete.
            if (!hasAgentOutput) return false;

            // The branch may contain changes from an earlier rescue or pipeline stage.
            // Only changes made since this dock was provisioned prove that this captain
            // did work. Keep diffLineCount in the signature for diagnostics and callers
            // that do not have dock metadata, but do not treat a stale branch diff as proof.
            // This is the one condition that exonerates a captain outright.
            if (hasChangesSinceDockStart) return false;

            if (mission.IsReadOnlyMode)
            {
                // Read-only (Audit/Research) missions deliver a report, never a diff, so the
                // empty-diff check cannot discriminate for them -- the report IS the
                // AgentOutput. A false-complete can restate a long brief and exceed a
                // small acknowledgment threshold, so require a substantive report before
                // accepting the mission. Real multi-item audit reports should be larger.
                // Do not exempt a long run: minutes spent reading and no report delivered
                // is the exact shape of a stream that died before the report was written.
                const int readOnlyMinReportChars = 1000;
                return agentOutputLength < readOnlyMinReportChars;
            }

            // An Implementation mission must produce a commit, and this one produced none.
            // A captain that never wrote its completion marker never claimed to have
            // finished, so no reading of the run makes the empty diff the intended outcome.
            // Neither runtime nor output length can discriminate here, because a stream that
            // dies mid-run leaves a long runtime and a long truncated narration behind.
            if (!hasCompletionMarker) return true;

            // The captain claims it finished with nothing committed. That flavor of
            // false-complete ends within seconds and writes only an acknowledgment.
            const int noOpMaxSeconds = 60;
            if (runtime.TotalSeconds >= noOpMaxSeconds) return false;

            const int implementationMaxNoOpChars = 200;
            return agentOutputLength < implementationMaxNoOpChars;
        }

        internal static string BuildNoOpCompletionFailureReason(Mission mission, TimeSpan runtime, int agentOutputLength)
        {
            string modeLabel = mission == null ? "mission" : mission.Mode.ToString() + " mission";
            bool hasMarker = HasCompletionMarker(mission?.AgentOutput);
            string signal = hasMarker
                ? "exited with a completion marker"
                : "exited 0 with no completion marker (" + CompletionMarker + " or " + VerdictMarker + ")";
            string cause = hasMarker
                ? "The captain claimed completion without producing work."
                : "The captain never claimed completion, so its run ended before it finished -- either it read the brief and exited, or its provider stream died mid-run and the runtime wrapper still exited 0.";
            return "no_op_completion_detected: captain " + signal + " after "
                + Math.Round(runtime.TotalSeconds, 1)
                + "s with an empty diff and "
                + agentOutputLength
                + " chars of AgentOutput on a " + modeLabel + ". " + cause
                + " The mission is re-queued rather than marked WorkProduced so the rescue path can retry with a different captain.";
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

            // A retried mission still carries the reason its earlier attempt failed. The requeue
            // paths keep that text on purpose so a Pending mission shows why it is being retried,
            // but once an attempt succeeds the text describes a superseded attempt and nothing
            // about the current state. Left in place it reads as a failure that did not happen:
            // a mission can sit in WorkProduced with a real commit and a stale provider error,
            // and any reader that checks FailureReason before Status concludes the mission failed.
            // The DoD gate below sets a fresh reason if it rejects this work.
            mission.FailureReason = null;
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

            // Detect "false complete": a captain that ends its run within seconds with an
            // empty diff and a tiny AgentOutput, with or without the [ARMADA:RESULT] COMPLETE
            // marker, and did no real work. The captain-side fix is not always available
            // (provider-side behavior), so the platform has to catch it. A no-op completion
            // that reaches WorkProduced corrupts the downstream pipeline with empty progress
            // and breaks rescue judgment.
            bool failedForNoOpCompletion = false;
            bool? dockProducedChanges = null;
            if (!failedForScopeViolation && mission.StartedUtc.HasValue)
            {
                TimeSpan runtime = (mission.CompletedUtc ?? DateTime.UtcNow) - mission.StartedUtc.Value;
                int diffLineCount = String.IsNullOrEmpty(mission.DiffSnapshot)
                    ? 0
                    : mission.DiffSnapshot.Split('\n').Length;
                int agentOutputLength = mission.AgentOutput?.Length ?? 0;
                bool hasAgentOutput = !String.IsNullOrEmpty(mission.AgentOutput);
                bool hasChangesSinceDockStart = await HasChangesSinceDockStartAsync(dock, diffLineCount, token).ConfigureAwait(false);
                dockProducedChanges = hasChangesSinceDockStart;
                bool hasCompletionMarker = HasCompletionMarker(mission.AgentOutput);
                if (DetectNoOpCompletion(mission, runtime, diffLineCount, agentOutputLength, hasAgentOutput, hasChangesSinceDockStart, hasCompletionMarker))
                {
                    failedForNoOpCompletion = true;
                    mission.Status = MissionStatusEnum.Failed;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission.FailureReason = BuildNoOpCompletionFailureReason(mission, runtime, agentOutputLength);
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    await AppendMissionActivityAsync(mission.Id, "validation failed: " + mission.FailureReason, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "mission " + mission.Id + " failed no-op-completion check runtime="
                        + Math.Round(runtime.TotalSeconds, 1) + "s diffLines=" + diffLineCount + " agentOutputLen=" + agentOutputLength);
                }
            }

            // Judge a rescue by what it CHANGED, not by whether it ran. A rescue that starts,
            // logs, and exits satisfies every liveness measure the platform keeps, and none of
            // them can tell a repaired defect from a day spent writing about one. The case this
            // exists for ran twenty-four hours, drew escalating stall nudges, died on a runtime
            // crash, and left a single changed documentation file behind.
            //
            // Only rescues are assessed. A first-attempt mission that produces docs may simply
            // have been dispatched to write docs; a RESCUE was dispatched against a named defect,
            // so a change set that cannot carry behavior is evidence on its own.
            bool failedForIneffectiveRescue = false;
            if (!failedForScopeViolation && !failedForNoOpCompletion && RescueMissionMarker.IsAutoRescue(mission))
            {
                IReadOnlyList<string> changedPaths = DiffPathExtractor.ExtractChangedPaths(mission.DiffSnapshot);
                Objective? rescuedObjective = await FindLinkedObjectiveAsync(mission, token).ConfigureAwait(false);
                RescueEffectivenessAssessment assessment = RescueEffectivenessEvaluator.Assess(
                    changedPaths,
                    RescueEffectivenessEvaluator.RequiresCodeChange(mission.Mode, rescuedObjective?.Kind));

                if (assessment.IsIneffective)
                {
                    failedForIneffectiveRescue = true;
                    mission.Status = MissionStatusEnum.Failed;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission.FailureReason = "ineffective_rescue: " + assessment.Reason
                        + " Change set: " + DescribeChangedPaths(changedPaths)
                        + " Dispatch a replacement with a brief that quotes the defect, rather than retrying this one.";
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    await AppendMissionActivityAsync(mission.Id, "validation failed: " + mission.FailureReason, token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "mission " + mission.Id + " failed ineffective-rescue check substance="
                        + assessment.Substance + " changedPaths=" + changedPaths.Count);
                }
            }

            // The gate runs the vessel's build and unit-test command inside the dock. When the
            // captain changed nothing, those commands measure the BASE COMMIT, so a pass reports
            // that the base was green and reports nothing at all about this mission. Recording
            // that as "validation passed" is worse than recording nothing: it reads later as a
            // verification that happened, which is how an empty result comes to look like
            // accepted work. Read-only missions are exempt -- an unchanged branch is their
            // intended outcome, and their own report gate judges them.
            bool dodGateHasWorkToVerify = !(dockProducedChanges == false && !mission.IsReadOnlyMode);

            if (!failedForScopeViolation && !failedForNoOpCompletion && !failedForIneffectiveRescue && dock != null
                && _DefinitionOfDoneGate != null && !dodGateHasWorkToVerify)
            {
                await AppendMissionActivityAsync(
                    mission.Id,
                    "validation skipped: definition-of-done gate cannot verify a mission that changed nothing; "
                    + "its commands would measure the base commit, not this captain's work",
                    token).ConfigureAwait(false);
                _Logging.Warn(_Header + "mission " + mission.Id
                    + " reached the DoD gate having changed nothing since dock start; gate skipped rather than passed");
            }

            // Definition-of-done gate: run in-dock build and unit-test before accepting Worker work.
            bool failedForDodGate = false;
            if (!failedForScopeViolation && !failedForNoOpCompletion && !failedForIneffectiveRescue && dock != null
                && _DefinitionOfDoneGate != null && dodGateHasWorkToVerify)
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
                bool judgeGateRejected = false;
                if (verdict == JudgeVerdict.Pass)
                {
                    if (!TryValidateJudgePassOutput(mission.AgentOutput, out verdictFailureReason))
                    {
                        // A PASS that fails structural validation degrades to a re-run request
                        // (NEEDS_REVISION semantics) so the mission stays non-terminal and the
                        // review is not recorded as a rejection.
                        verdict = JudgeVerdict.NeedsRevision;
                    }
                    else
                    {
                        // Real-signal gate: a Judge PASS must be backed by green independent Checks
                        // (Build/UnitTest from real command output the Judge did not produce), not by
                        // the agent's self-report or self-run tests. A failed Check overrides the PASS;
                        // unresolved Checks hold the PASS until they land (bounded in-place re-run);
                        // a PASS with no Checks at all is rejected unless the Judge documents an
                        // environmental exclusion with the explicit marker.
                        // Each rejection branch already terminalizes the mission with a SPECIFIC
                        // FailureReason (the generic "Judge verdict: ..." fall-through below would
                        // otherwise overwrite it and misreport a PASS as a judge rejection).
                        JudgeCheckGate checkGate = await EvaluateJudgeCheckGateAsync(mission, token).ConfigureAwait(false);
                        switch (checkGate)
                        {
                            case JudgeCheckGate.HasFailed:
                                mission.Status = MissionStatusEnum.Failed;
                                mission.CompletedUtc = DateTime.UtcNow;
                                mission.LastUpdateUtc = DateTime.UtcNow;
                                string blocking = DescribeBlockingChecks(_LastJudgeGateChecks, CheckRunStatusEnum.Failed);
                                mission.FailureReason =
                                    "Judge PASS rejected: an independent Check failed (real-signal gate; Judge self-report cannot override real command output)."
                                    + (String.IsNullOrEmpty(blocking)
                                        ? String.Empty
                                        : " Failed Checks: " + blocking + ".")
                                    + " Resolve or re-run EVERY failed Check on this voyage before the Judge re-runs; a single unresolved record rejects the PASS.";
                                mission.ReviewComment = BuildJudgeReviewComment(mission.AgentOutput, mission.FailureReason);
                                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                                verdict = JudgeVerdict.Fail;
                                judgeGateRejected = true;
                                _Logging.Warn(_Header + "judge mission " + mission.Id + " PASS rejected by a failed independent Check (real-signal gate)");
                                break;

                            case JudgeCheckGate.HasPending:
                                if (mission.RecoveryAttempts < _MaxJudgeCheckWaitRetries)
                                {
                                    await ResetMissionForReRunAsync(mission, token).ConfigureAwait(false);
                                    retryingMissingVerdict = true;
                                    string holding = DescribeUnresolvedChecks(_LastJudgeGateChecks, _LastJudgeReviewedCommit);
                                    _Logging.Info(_Header + "judge mission " + mission.Id +
                                        " PASS held: independent Checks not green for the reviewed commit yet; re-running in place (attempt " +
                                        mission.RecoveryAttempts + " of " + _MaxJudgeCheckWaitRetries + ")"
                                        + (String.IsNullOrEmpty(holding) ? String.Empty : "; holding: " + holding));
                                }
                                else
                                {
                                    mission.Status = MissionStatusEnum.Failed;
                                    mission.CompletedUtc = DateTime.UtcNow;
                                    mission.LastUpdateUtc = DateTime.UtcNow;
                                    string unresolved = DescribeUnresolvedChecks(_LastJudgeGateChecks, _LastJudgeReviewedCommit);
                                    mission.FailureReason =
                                        "Judge PASS rejected: independent Checks never resolved after "
                                        + _MaxJudgeCheckWaitRetries + " wait attempts (real-signal gate)."
                                        + (String.IsNullOrEmpty(unresolved)
                                            ? String.Empty
                                            : " Unresolved Checks: " + unresolved + ".")
                                        + " Inspect those Check records; the Judge captain is not the subject of this rejection.";
                                    mission.ReviewComment = BuildJudgeReviewComment(mission.AgentOutput, mission.FailureReason);
                                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                                    verdict = JudgeVerdict.Fail;
                                    judgeGateRejected = true;
                                    _Logging.Warn(_Header + "judge mission " + mission.Id + " PASS rejected: Checks unresolved after the wait budget");
                                }
                                break;

                            case JudgeCheckGate.NoChecksNoExclusion:
                                mission.Status = MissionStatusEnum.Failed;
                                mission.CompletedUtc = DateTime.UtcNow;
                                mission.LastUpdateUtc = DateTime.UtcNow;
                                mission.FailureReason = JudgeNoChecksFailureReason;
                                mission.ReviewComment = BuildJudgeReviewComment(mission.AgentOutput, mission.FailureReason);
                                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                                verdict = JudgeVerdict.Fail;
                                judgeGateRejected = true;
                                _Logging.Warn(_Header + "judge mission " + mission.Id + " PASS rejected: no independent Checks (real-signal gate)");
                                break;

                            case JudgeCheckGate.GreenChecks:
                            case JudgeCheckGate.NoChecksWithExclusion:
                            default:
                                break;
                        }
                    }
                }

                // A Judge that exits without any parseable verdict is an OPERATIONAL miss, not a
                // substantive rejection: the review may have reached a conclusion that was never
                // flushed (for example a backgrounded test run that scheduled a wakeup and then
                // terminated before the standalone [ARMADA:VERDICT] line). Re-run the Judge in
                // place a bounded number of times instead of marking it Failed -- a hard failure
                // opens an incident and burns the auto-rescue budget on verified-good work. The
                // RecoveryAttempts counter bounds the total automated recovery effort on this one
                // mission; explicit FAIL / NEEDS_REVISION verdicts skip this path and stay terminal.
                if (ShouldRetryMissingJudgeVerdict(verdict != JudgeVerdict.None, CountRetrySkipCaptains(mission.RetrySkipCaptainIds)))
                {
                    // Record the captain that produced the empty/no-verdict output so the in-place
                    // re-run dispatches to a DIFFERENT captain (native fallback) instead of
                    // re-selecting the same degraded provider, and track the re-run in the same
                    // persisted list so it does NOT consume the autonomous-rescue budget
                    // (RecoveryAttempts). An intermittent Judge provider must never block the rescue.
                    AppendRetrySkipCaptain(mission, captain != null ? captain.Id : mission.CaptainId);
                    await ResetMissionForReRunAsync(mission, token, countRecoveryBudget: false).ConfigureAwait(false);
                    retryingMissingVerdict = true;
                    _Logging.Warn(_Header + "judge mission " + mission.Id +
                        " produced no verdict line; re-running in place on a different captain (re-run " +
                        CountRetrySkipCaptains(mission.RetrySkipCaptainIds) + " of " + _MaxMissingJudgeVerdictRetries + ", skipping " +
                        (captain != null ? captain.Id : mission.CaptainId) + ")");
                }
                else if (verdict != JudgeVerdict.Pass && !judgeGateRejected)
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
            if (!failedForScopeViolation && !failedForDodGate && !failedForIneffectiveRescue && !awaitingManualReview)
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
            // A present root instruction file -- tracked repo content (CLAUDE.md) or an untracked
            // local file -- must never be overwritten, so the generated brief goes under
            // .armada/instructions/. An ABSENT root file means the runtime auto-loads the filename
            // (OpenCode/AGENTS.md) or the launch prompt points at it, so the brief lands at the root.
            // This existence-based decision is the WRITE side. EnsureMissionInstructionsPresentAsync
            // decides the RESTORE side by tracked status, so a root file this pass generated is never
            // duplicated under .armada/instructions/ (probe papercut, 2026-08-09).
            string instructionsRelativePath = File.Exists(rootInstructionsPath)
                ? MissionPromptBuilder.GetGeneratedInstructionsRelativePath(runtimeName)
                : instructionsFileName;
            string instructionsPath = Path.Combine(worktreePath, instructionsRelativePath);

            TestOwnershipEnum testOwnership = await ResolveTestOwnershipAsync(mission, vessel, token).ConfigureAwait(false);
            string? judgePrimaryLens = await ResolveJudgeLensAsync(mission, token).ConfigureAwait(false);
            Dictionary<string, string> templateParams = MissionPromptBuilder.BuildTemplateParams(mission, vessel, captain, null, testOwnership, judgePrimaryLens);
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
                    if (mission.IsReadOnlyMode)
                    {
                        content += ledger.Track("mission.playbooks_wrapper_read_only", BuildReadOnlyPlaybooksWrapperSection(playbooksMarkdown));
                    }
                    else
                    {
                        content += ledger.Track("mission.playbooks_wrapper", await ResolveSectionAsync("mission.playbooks_wrapper", templateParams, token).ConfigureAwait(false));
                    }
                    content += "\n";
                }
            }

            // The vessel's project-profile skills, injected as their own section. Tracked through the
            // ledger like every other module: an untracked section still costs the captain its bytes,
            // and an oversized brief must be visible in the accounting rather than shipping silently.
            string skillsMarkdown = await ResolveSkillsMarkdownAsync(vessel, token).ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(skillsMarkdown))
            {
                content += ledger.Track("mission.skills", "## Skills\n\n" + skillsMarkdown + "\n");
                content += "\n";
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
                string? memoryRepoFolder = ResolveMemoryRepoFolder(_Settings.AiMemoryRoot, vessel.Name);
                content += ledger.Track("mission.ai_memory", BuildAiMemorySection(_Settings.AiMemoryRoot!, memoryRepoFolder));
                content += "\n";

                // Facts about this vessel that cannot be fixed today. Empty by default, and meant to
                // stay empty: the block reports its own count so a growing list reads as a regression.
                // Read-only missions get it too, because a deferred fact misleads an auditor exactly as
                // much as it misleads an implementer.
                List<DeferredFact> deferredFacts = LoadDeferredFacts(_Settings.AiMemoryRoot, memoryRepoFolder);
                string deferredFactsSection = BuildDeferredFactsSection(deferredFacts, DateTime.UtcNow);
                if (!String.IsNullOrEmpty(deferredFactsSection))
                {
                    content += ledger.Track("mission.deferred_facts", deferredFactsSection);
                }
                content += "\n";
            }

            // Git anchors: the base commit, the recent history of the paths this mission names, and
            // whether its subject terms already exist here. Every mission mode gets them, including
            // read-only ones -- establishing what already exists IS most of an Audit's work, so an
            // audit captain benefits from them at least as much as an implementing one.
            GitAnchors gitAnchors = await ResolveGitAnchorsAsync(worktreePath, mission, vessel, token).ConfigureAwait(false);
            string gitAnchorsSection = BuildGitAnchorsSection(gitAnchors);
            if (!String.IsNullOrEmpty(gitAnchorsSection))
            {
                content += ledger.Track("mission.git_anchors", gitAnchorsSection);
                content += "\n";
            }

            // Mission preamble and metadata -- resolve persona prompt first, then inject into metadata template
            // A read-only mission takes the mode-aware output contract instead of the persona template.
            // The producing templates carry implementation language of their own -- commit your scoped
            // changes, run checks before committing -- which contradicts the read-only rules further
            // down the same brief. Reviewer personas are unaffected: their contract already reports
            // rather than changes.
            // Apply the vessel's project-profile persona override so per-project customization takes
            // effect. A read-only mission keeps its mode-aware output contract: the override customizes
            // the producing persona template, which that contract deliberately replaces.
            PersonaOverride? personaOverride = mission.IsReadOnlyMode
                ? null
                : await ResolvePersonaOverrideAsync(vessel, mission.Persona, token).ConfigureAwait(false);

            string personaPrompt = mission.IsReadOnlyMode
                ? MissionPromptBuilder.GetPersonaOutputContract(mission.Persona, mission.Mode, judgePrimaryLens)
                : await ResolvePersonaPromptAsync(mission.Persona, templateParams, personaOverride, token).ConfigureAwait(false);
            templateParams["PersonaPrompt"] = personaPrompt;

            // The metadata module embeds the mission description verbatim. A long accumulated
            // handoff chain (base brief plus a persona preamble plus prior-stage agent output
            // plus scoped diff per stage) once rendered a 53 KB metadata module against a
            // 32 KiB brief budget on a rescue Judge. Bound the embedded copy here so the
            // module fits the budget regardless of what the persisted description holds; the
            // full description stays in the mission record for reference. Rendering with a
            // private parameter copy keeps every later module on the unbound description.
            Dictionary<string, string> metadataParams = new Dictionary<string, string>(templateParams)
            {
                ["MissionDescription"] = BoundMetadataDescription(mission.Description)
            };
            content += ledger.Track("mission.metadata", await ResolveSectionAsync("mission.metadata", metadataParams, token).ConfigureAwait(false));
            content += "\n";

            // Objective scope, supplied once when the voyage links an objective: the objective's own
            // scope, acceptance criteria, and non-goals are the definition of done. Previously a linked
            // objective never reached the captain as distinct context -- the mission description alone
            // had to stand in for it -- and the Judge had no acceptance criteria to review against.
            string objectiveScope = await BuildObjectiveScopeSectionAsync(mission, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(objectiveScope))
            {
                content += ledger.Track("mission.objective_scope", objectiveScope);
                content += "\n";
            }

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
                // Only a PullRequest landing needs the captain's branch on the remote. Every other
                // mode lands from the bare repo, so a captain push buys nothing and leaves a remote
                // branch behind: cleanup under the LocalOnly policy deletes locally and never touches
                // origin, and a LocalMerge vessel opens no PR, so no host-side auto-delete collects
                // it either. Telling the captain not to push is what keeps the remote from growing
                // one dead branch per mission forever.
                string rulesModule = RequiresCaptainPush(vessel, _Settings) ? "mission.rules" : "mission.rules_no_push";
                content += ledger.Track(rulesModule, await ResolveSectionAsync(rulesModule, templateParams, token).ConfigureAwait(false));
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

            // Papercuts apply to every mission mode: an audit meets stale docs and dead links exactly as
            // an implementation does. Judge and Architect are excluded -- a judge reports what it finds
            // through its verdict, and an architect emits mission blocks only.
            if (!PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge) &&
                !PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Architect))
            {
                content += "\n";
                content += ledger.Track("mission.papercuts", BuildPapercutsSection());
                content += ledger.Track("mission.notes", BuildProgressNotesSection());
            }

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

            // Total-budget backstop: if the assembled brief still exceeds the captain instruction
            // budget after every per-module cap, elide the largest content modules in place so no
            // mission ships an over-budget brief. The persona prompt, rules, and metadata skeleton are
            // never elided -- only the content-bearing modules that repeat vessel or mission context.
            content = EnforceTotalBriefBudget(content, ledger, _Settings.CaptainInstructionByteBudget);

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
        /// Build the Objective Scope module for a mission whose voyage links an objective: the
        /// objective's own scope, acceptance criteria, and non-goals, supplied once, as the mission's
        /// definition of done. The Judge reviews against these criteria. Returns an empty string when
        /// the voyage links no objective or the lookup fails (a brief must never fail a dispatch).
        /// </summary>
        /// <param name="mission">Mission being briefed.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The Objective Scope section, or an empty string.</returns>
        /// <summary>
        /// Find the objective whose voyage list contains the mission's voyage. This is the one
        /// mission-to-objective lookup; the brief builder and the rescue gate both use it. Returns
        /// null when the mission has no voyage, the voyage links no objective, or the lookup fails.
        /// </summary>
        /// <param name="mission">Mission to resolve.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The linked objective, or null.</returns>
        private async Task<Objective?> FindLinkedObjectiveAsync(Mission? mission, CancellationToken token)
        {
            if (mission == null || String.IsNullOrEmpty(mission.VoyageId))
            {
                return null;
            }

            try
            {
                List<Objective> objectives = await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
                return objectives.FirstOrDefault(o =>
                    o != null
                    && o.VoyageIds != null
                    && o.VoyageIds.Contains(mission.VoyageId));
            }
            catch (Exception e)
            {
                _Logging.Warn(_Header + "linked objective lookup failed for mission " + mission.Id + ": " + e.Message);
                return null;
            }
        }

        private async Task<string> BuildObjectiveScopeSectionAsync(Mission mission, CancellationToken token)
        {
            if (mission == null || String.IsNullOrEmpty(mission.VoyageId))
            {
                return String.Empty;
            }

            try
            {
                Objective? linked = await FindLinkedObjectiveAsync(mission, token).ConfigureAwait(false);
                if (linked == null)
                {
                    return String.Empty;
                }

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("## Objective Scope (Definition of Done)\n\n");
                sb.Append("This mission belongs to a linked objective. The objective below is the definition of done; work that does not meet it is not complete, and the Judge reviews against these acceptance criteria and non-goals:\n\n");
                sb.Append("**Title:** ").Append(linked.Title).Append("\n\n");
                if (!String.IsNullOrWhiteSpace(linked.Description))
                {
                    sb.Append("**Scope:** ").Append(linked.Description.Trim()).Append("\n\n");
                }
                if (linked.AcceptanceCriteria != null && linked.AcceptanceCriteria.Count > 0)
                {
                    sb.Append("**Acceptance Criteria:**\n");
                    foreach (string ac in linked.AcceptanceCriteria)
                    {
                        if (!String.IsNullOrWhiteSpace(ac)) sb.Append("- ").Append(ac.Trim()).Append("\n");
                    }
                    sb.Append("\n");
                }
                if (linked.NonGoals != null && linked.NonGoals.Count > 0)
                {
                    sb.Append("**Non-Goals (out of scope):**\n");
                    foreach (string ng in linked.NonGoals)
                    {
                        if (!String.IsNullOrWhiteSpace(ng)) sb.Append("- ").Append(ng.Trim()).Append("\n");
                    }
                    sb.Append("\n");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not build objective scope for mission " + mission.Id + ": " + ex.Message);
                return String.Empty;
            }
        }

        /// <summary>
        /// Builds the shared-memory pointer. It names the index only and inlines no memory content:
        /// memory grows without bound, so inlining it would re-create the prompt bloat this module is
        /// measured against, and a captain can read the parts it needs. The path is emitted exactly as
        /// configured, so it must be the path as it resolves on the host the captain runs on.
        /// The wording must not contradict a repository instruction file the runtime auto-loads
        /// (a repo CLAUDE.md that names the active memory set): an absolute "do not read the whole
        /// tree" against an auto-loaded directive that says to read specific files reads as a
        /// contradiction the captain must resolve (probe papercut, 2026-08-09). Defer to the
        /// repository file instead.
        /// </summary>
        /// <param name="memoryRoot">Configured AI-Memory root path.</param>
        /// <returns>The AI-Memory section.</returns>
        internal static string BuildAiMemorySection(string memoryRoot, string? repoFolder)
        {
            string root = (memoryRoot ?? "").TrimEnd('/', '\\');

            string scope = String.IsNullOrEmpty(repoFolder)
                ? "This vessel has no folder under `" + root + "/repos/`, so there is nothing " +
                  "repository-specific to read.\n"
                : "This vessel's own memory is `" + root + "/repos/" + repoFolder + "/`. Read that as well.\n";

            return
                "## Shared Memory\n" +
                "Durable, cross-mission knowledge for this fleet lives at `" + root + "`.\n" +
                "Read every file under `" + root + "/shared/`. The index there is a map and holds no rules, " +
                "so reading it alone tells you nothing.\n" +
                scope +
                "Do not read another repository's folder under `" + root + "/repos/`, and do not read the host " +
                "notes. Neither applies to this mission, and both cost you context you will want for the source.\n" +
                "If the repository instruction file (CLAUDE.md / AGENTS.md) names the memory files that load " +
                "for this runtime, follow it. " +
                "AI-Memory is the authoritative durable memory for this fleet; the runtime's own file-memory " +
                "protocol is not shared state, so do not write to it. " +
                "It is reference material, not authority: playbooks, vessel instructions, and this mission brief win on conflict.\n";
        }

        /// <summary>
        /// Renders the deferred-facts block: things about this vessel that cannot be fixed today and
        /// that a captain would otherwise trip over or re-derive.
        ///
        /// Every entry reads as "known, and being fixed under &lt;objective&gt;". It never reads as "this is
        /// normal", because a fact that reads as normal stops being fixed. An entry past its expiry is
        /// marked stale rather than dropped: a fact that quietly disappears cannot be told apart from
        /// one that was resolved.
        ///
        /// The block states its own entry count, so a list that is growing is visible as a regression
        /// rather than as adoption.
        /// </summary>
        /// <param name="facts">Entries that passed validation.</param>
        /// <param name="nowUtc">Current time, supplied so expiry is testable.</param>
        /// <returns>The section, or an empty string when there is nothing to state.</returns>
        internal static string BuildDeferredFactsSection(List<DeferredFact> facts, DateTime nowUtc)
        {
            if (facts == null || facts.Count == 0) return "";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("## Known Deferred Facts (" + facts.Count + ")\n");
            builder.Append("These are known and already owned. Each names the objective that removes it. ");
            builder.Append("None of them is normal, and none of them is yours to fix in this mission ");
            builder.Append("unless the mission says so.\n\n");

            foreach (DeferredFact fact in facts)
            {
                if (fact.IsExpired(nowUtc))
                {
                    builder.Append("- STALE, unverified since " +
                        (String.IsNullOrEmpty(fact.LastVerifiedCommit) ? "an unrecorded commit" : "`" + fact.LastVerifiedCommit + "`") +
                        ": " + fact.Text + "\n");
                    builder.Append("  - Past its review date, so treat it as unknown rather than as current. " +
                        "Being fixed under " + fact.FixObjectiveId + ".\n");
                    continue;
                }

                builder.Append("- " + fact.Text + "\n");
                builder.Append("  - Known, and being fixed under " + fact.FixObjectiveId + ".");
                if (!String.IsNullOrEmpty(fact.LastVerifiedCommit))
                    builder.Append(" Last checked at `" + fact.LastVerifiedCommit + "`.");
                builder.Append("\n");
            }

            return BoundGitAnchorsSection(builder.ToString());
        }

        /// <summary>
        /// Reads and validates the vessel's deferred-facts file. Returns an empty list when the file is
        /// absent, which is the expected state: the list is meant to stay empty.
        /// </summary>
        /// <param name="memoryRoot">Configured AI-Memory root path.</param>
        /// <param name="repoFolder">Resolved folder for this vessel, or null.</param>
        /// <returns>Validated entries; never null.</returns>
        private List<DeferredFact> LoadDeferredFacts(string? memoryRoot, string? repoFolder)
        {
            List<DeferredFact> accepted = new List<DeferredFact>();
            if (String.IsNullOrWhiteSpace(memoryRoot) || String.IsNullOrEmpty(repoFolder)) return accepted;

            try
            {
                string path = Path.Combine(memoryRoot!.TrimEnd('/', '\\'), "repos", repoFolder!, "deferred-facts.md");
                if (!File.Exists(path)) return accepted;

                List<string> refusals;
                DeferredFactsParser.Parse(File.ReadAllText(path), out accepted, out refusals);

                // A refused entry is an operator error, not a captain problem. It never reaches the
                // brief, and saying so in the log is what stops it being silently ignored for weeks.
                foreach (string refusal in refusals)
                {
                    _Logging.Warn(_Header + "deferred facts for " + repoFolder + ": " + refusal);
                }

                return accepted;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read deferred facts for " + repoFolder + ": " + ex.Message);
                return new List<DeferredFact>();
            }
        }

        /// <summary>
        /// Resolves the memory folder that belongs to one vessel, or null when the vessel has none.
        ///
        /// A captain told only to read the index picks folders by guesswork, and a captain that guesses
        /// reads another repository's memory: material that applies to nothing it is doing, paid for on
        /// every mission of every vessel. Name the one folder that is its own instead.
        /// </summary>
        /// <param name="memoryRoot">Configured AI-Memory root path.</param>
        /// <param name="vesselName">Vessel name.</param>
        /// <returns>The folder name under repos/, or null when no such folder exists.</returns>
        internal static string? ResolveMemoryRepoFolder(string? memoryRoot, string? vesselName)
        {
            if (String.IsNullOrWhiteSpace(memoryRoot)) return null;

            string candidate = NormalizeMemoryRepoFolder(vesselName);
            if (String.IsNullOrEmpty(candidate)) return null;

            try
            {
                string path = Path.Combine(memoryRoot.TrimEnd('/', '\\'), "repos", candidate);
                return Directory.Exists(path) ? candidate : null;
            }
            catch (Exception)
            {
                // A memory root that cannot be probed is not a reason to fail a dispatch. Fall back to
                // naming no folder, which leaves the captain with the shared set only.
                return null;
            }
        }

        /// <summary>
        /// Reduces a vessel name to the folder-name form used under repos/: lower case, letters and
        /// digits only, so "Some-Vessel" becomes "somevessel".
        /// </summary>
        /// <param name="vesselName">Vessel name.</param>
        /// <returns>The normalized folder name, or an empty string.</returns>
        internal static string NormalizeMemoryRepoFolder(string? vesselName)
        {
            if (String.IsNullOrWhiteSpace(vesselName)) return "";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            foreach (char c in vesselName!)
            {
                if (Char.IsLetterOrDigit(c)) builder.Append(Char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Resolves the git facts a captain would otherwise discover itself: where its branch starts,
        /// what recently touched the paths the mission names, and whether the mission's subject terms
        /// already exist on the checkout.
        ///
        /// Resolution runs against the provisioned dock, which is a full worktree of the vessel
        /// repository at the mission's base. It never throws: an unresolved block states why it is
        /// empty, because a silently missing anchors section reads as "no prior art exists", which is
        /// a stronger claim than the admiral is entitled to make.
        ///
        /// The repository is probed once with a cheap command before any search runs. A search that
        /// cannot run and a search that found nothing are indistinguishable at the git exit code, so
        /// the probe is what separates them; without it a broken checkout would render as a
        /// "verified absent" line, which is a false fact stated with full confidence.
        /// </summary>
        /// <param name="worktreePath">Provisioned dock path.</param>
        /// <param name="mission">Mission whose subjects are resolved.</param>
        /// <param name="vessel">Vessel supplying the target branch.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Resolved anchors, or an unresolved instance carrying the reason.</returns>
        private async Task<GitAnchors> ResolveGitAnchorsAsync(
            string worktreePath,
            Mission mission,
            Vessel vessel,
            CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            if (_Git == null) return GitAnchors.Unresolved("no git service is configured on this admiral");
            if (String.IsNullOrEmpty(worktreePath)) return GitAnchors.Unresolved("no dock path was available");

            GitAnchors anchors = new GitAnchors();
            anchors.TargetBranch = vessel.DefaultBranch ?? "";

            try
            {
                string? head = await _Git.GetHeadCommitHashAsync(worktreePath, token).ConfigureAwait(false);
                if (String.IsNullOrEmpty(head))
                {
                    return GitAnchors.Unresolved("the dock did not answer git rev-parse HEAD");
                }

                // At brief-generation time the dock has just been provisioned, so its HEAD is the
                // commit this mission's work starts from. Rendered abbreviated, to match the target tip
                // below: showing one full hash beside one short hash reads as two different commits.
                string? shortHead = await _Git.GetRevisionShaAsync(worktreePath, "HEAD", token).ConfigureAwait(false);
                anchors.BaseCommit = String.IsNullOrEmpty(shortHead) ? head! : shortHead!;

                // The target tip is resolved separately and is not always the same commit: a dock cut
                // from an older base lands through the merge queue against a target that has since
                // moved, which is how sibling commits have been dropped before. A captain that can see
                // both values can see that gap.
                if (!String.IsNullOrEmpty(anchors.TargetBranch))
                {
                    string? tip = await _Git.GetRevisionShaAsync(worktreePath, anchors.TargetBranch, token).ConfigureAwait(false);
                    anchors.TargetTip = tip ?? "";
                }

                string missionText = (mission.Title ?? "") + "\n" + (mission.Description ?? "");
                List<string> paths = MissionSubjectExtractor.ExtractPaths(missionText);
                List<string> terms = MissionSubjectExtractor.ExtractTerms(missionText);

                foreach (string path in paths)
                {
                    GitAnchorFileHistory history = new GitAnchorFileHistory();
                    history.Path = path;
                    history.ExistsOnRevision = await _Git.PathExistsOnRevisionAsync(
                        worktreePath, "HEAD", path, token).ConfigureAwait(false);

                    // A mission names a file the way a reader says it, which is usually a suffix of the
                    // tracked path. Reporting that name as absent states a false fact about a present
                    // file, and a captain that believes it writes a second copy of landed work. Resolve
                    // the suffix before concluding anything, and anchor the history on what git tracks.
                    if (!history.ExistsOnRevision)
                    {
                        string? resolved = await _Git.ResolveTrackedPathSuffixAsync(
                            worktreePath, "HEAD", path, token).ConfigureAwait(false);

                        if (!String.IsNullOrEmpty(resolved))
                        {
                            history.RequestedPath = path;
                            history.Path = resolved!;
                            history.ExistsOnRevision = true;
                        }
                        else
                        {
                            history.IsExternalSourceTree = MissionSubjectExtractor.IsExternalSourceTreePath(path);
                        }
                    }

                    IReadOnlyList<GitAnchorCommit> commits = await _Git.GetCommitsTouchingPathAsync(
                        worktreePath, history.Path, MaxAnchorCommitsPerPath, token).ConfigureAwait(false);

                    history.Commits = new List<GitAnchorCommit>(commits);
                    anchors.Files.Add(history);
                }

                foreach (string term in terms)
                {
                    GitAnchorPriorArt priorArt = await _Git.SearchTrackedContentAsync(
                        worktreePath, term, MaxAnchorSampleLocations, token).ConfigureAwait(false);

                    anchors.PriorArt.Add(priorArt);
                }

                return anchors;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not resolve git anchors for mission " + mission.Id + ": " + ex.Message);

                // Keep whatever resolved before the failure and mark the block incomplete. Discarding
                // it would throw away correct facts, and the renderer states the shortfall rather than
                // presenting a half-finished block as a finished one.
                anchors.ResolutionError = "git anchor resolution failed: " + ex.Message;
                return anchors;
            }
        }

        /// <summary>
        /// Renders the git anchors block. The block states facts and points at locations; it never
        /// pastes file content, because the captain can read a named file far more cheaply than the
        /// brief can carry it.
        ///
        /// A negative prior-art result is rendered as explicitly as a positive one. Proving that
        /// something is absent is several turns of searching, and a captain that cannot prove it
        /// either duplicates landed work or stops to ask.
        /// </summary>
        /// <param name="anchors">Resolved anchors.</param>
        /// <returns>The git anchors section, or an empty string when there is nothing to state.</returns>
        internal static string BuildGitAnchorsSection(GitAnchors anchors)
        {
            if (anchors == null) return "";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("## Git Anchors\n");
            builder.Append("Resolved by the admiral at dispatch. These are facts about this checkout, ");
            builder.Append("supplied so you do not spend turns deriving them. Verify anything you intend to rely on.\n\n");

            // Nothing resolved: emit nothing. A block that says only "this failed" spends brief bytes
            // to leave the captain exactly where it started, and the absence of the section is the
            // state every captain already handles. The reason goes to the log and the budget
            // telemetry, where the operator reads it.
            if (!anchors.HasContent) return "";

            if (!String.IsNullOrEmpty(anchors.TargetBranch))
                builder.Append("- Target branch: `" + anchors.TargetBranch + "`\n");
            if (!String.IsNullOrEmpty(anchors.BaseCommit))
                builder.Append("- Your work starts at (this checkout's HEAD): `" + anchors.BaseCommit + "`\n");
            if (!String.IsNullOrEmpty(anchors.TargetTip))
            {
                builder.Append("- Target branch tip at dispatch: `" + anchors.TargetTip + "`\n");

                if (!String.IsNullOrEmpty(anchors.BaseCommit) &&
                    !IsSameCommit(anchors.BaseCommit, anchors.TargetTip))
                {
                    builder.Append("  - These differ: your checkout is not at the target tip. ");
                    builder.Append("Expect the landing to rebase or merge, and do not assume the tip's content is present here.\n");
                }
            }

            if (anchors.Files.Count > 0)
            {
                builder.Append("\n### Recent history of the paths this mission names\n");
                foreach (GitAnchorFileHistory file in anchors.Files)
                {
                    if (!file.ExistsOnRevision)
                    {
                        if (file.IsExternalSourceTree)
                        {
                            // A read-only sibling tree is absent from every checkout by design. Calling
                            // that new work tells a captain to create the source it was sent to read.
                            builder.Append("- `" + file.Path + "` is not tracked here and reads as a path in a " +
                                "sibling read-only source tree, which is provisioned beside the dock rather than in it. " +
                                "Read it there; never create it in this repository.\n");
                            continue;
                        }

                        builder.Append("- `" + file.Path + "` does not exist on this checkout. It is new work, not an edit.\n");
                        continue;
                    }

                    if (!String.IsNullOrEmpty(file.RequestedPath))
                    {
                        builder.Append("- `" + file.Path + "` (the mission names it `" + file.RequestedPath +
                            "`; the repository tracks it at the path above)\n");
                    }
                    else
                    {
                        builder.Append("- `" + file.Path + "`\n");
                    }
                    if (file.Commits.Count == 0)
                    {
                        builder.Append("  - no commit history found for this path\n");
                        continue;
                    }

                    foreach (GitAnchorCommit commit in file.Commits)
                    {
                        builder.Append("  - " + commit.ToBriefLine() + "\n");
                    }
                }
            }

            if (anchors.PriorArt.Count > 0)
            {
                builder.Append("\n### Prior art for this mission's subject terms\n");
                foreach (GitAnchorPriorArt priorArt in anchors.PriorArt)
                {
                    if (!priorArt.Found)
                    {
                        // Named against the commit the search actually ran on, which is this
                        // checkout's HEAD. Attributing it to the target tip would overstate the
                        // claim whenever the dock was cut from an older base.
                        builder.Append("- `" + priorArt.Term + "`: VERIFIED ABSENT from tracked content as of `" +
                            (String.IsNullOrEmpty(anchors.BaseCommit) ? "this checkout" : anchors.BaseCommit) +
                            "`.\n");
                        continue;
                    }

                    builder.Append("- `" + priorArt.Term + "`: present in " + priorArt.MatchingFileCount +
                        (priorArt.MatchingFileCount == 1 ? " file" : " files"));

                    if (priorArt.SampleLocations.Count > 0)
                    {
                        builder.Append("; for example " + String.Join(", ", priorArt.SampleLocations));
                    }

                    builder.Append("\n");
                }
            }

            // A partial resolution must say so. Without this line the captain reads a block that
            // resolved paths but never finished searching as a complete one, and "no absent line for
            // my term" then reads as "my term is present" -- the exact wrong conclusion.
            if (!String.IsNullOrEmpty(anchors.ResolutionError))
            {
                builder.Append("\nINCOMPLETE: anchor resolution stopped early (" + anchors.ResolutionError + "). ");
                builder.Append("What is listed above is accurate. What is missing was never checked, ");
                builder.Append("so treat any subject absent from this block as unknown, not as absent from the repository.\n");
            }

            return BoundGitAnchorsSection(builder.ToString());
        }

        /// <summary>
        /// Reports whether two commit strings name the same commit when one is abbreviated and the other
        /// is not. Git hands back a full hash from rev-parse HEAD and a short one from rev-parse --short,
        /// so an ordinal comparison of the two calls the same commit different.
        ///
        /// That mattered in production: the anchors block told captains "your checkout is not at the
        /// target tip" on every dispatch where it WAS at the tip. A confident false statement is worse
        /// than no statement, because a captain plans around it.
        /// </summary>
        /// <param name="left">First commit string; full or abbreviated.</param>
        /// <param name="right">Second commit string; full or abbreviated.</param>
        /// <returns>True when either is a prefix of the other.</returns>
        internal static bool IsSameCommit(string? left, string? right)
        {
            if (String.IsNullOrEmpty(left) || String.IsNullOrEmpty(right)) return false;

            string shorter = left!.Length <= right!.Length ? left! : right!;
            string longer = left!.Length <= right!.Length ? right! : left!;

            // Git will not abbreviate below four characters, and a shorter prefix would start matching
            // unrelated commits. Treat anything shorter as not comparable rather than guessing.
            if (shorter.Length < 4) return false;

            return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Caps the anchors block on a line boundary with a visible marker. The block is an aid, so it
        /// must never be the module that pushes a brief over budget; a truncated aid that says it was
        /// truncated is safe, while a silently cut one implies the list it shows is complete.
        /// </summary>
        /// <param name="section">Rendered section.</param>
        /// <returns>The section, capped.</returns>
        internal static string BoundGitAnchorsSection(string section)
        {
            if (String.IsNullOrEmpty(section)) return section ?? "";
            if (section.Length <= MaxGitAnchorsSectionChars) return section;

            string head = section.Substring(0, MaxGitAnchorsSectionChars);
            int lastNewline = head.LastIndexOf('\n');
            if (lastNewline > 0) head = head.Substring(0, lastNewline + 1);

            return head + "[git anchors truncated at " + MaxGitAnchorsSectionChars +
                " characters; run git log or git grep yourself for the rest]\n";
        }

        /// <summary>
        /// Whether this vessel's landing mode actually needs the captain to push its branch to origin.
        /// Only a PullRequest landing does: the PR is opened against the remote branch, so without the
        /// push there is nothing to review. LocalMerge, MergeQueue and None all land from the bare
        /// repository, where the captain's commit is already reachable.
        /// </summary>
        /// <remarks>
        /// This is a branch-accumulation control, not a convenience. A pushed branch outlives the
        /// mission: the cleanup paths honour BranchCleanupPolicy, and under LocalOnly they delete from
        /// the bare repo and leave origin alone. A LocalMerge vessel also never opens a PR, so the
        /// git host's own auto-delete-on-merge never fires. The remote therefore keeps one branch per
        /// mission, permanently, unless the push never happens.
        ///
        /// An unresolved mode returns true, preserving the historical instruction. The legacy boolean
        /// settings can still derive a PullRequest landing, and suppressing the push in that case
        /// would strand the branch with no way to open the PR.
        /// </remarks>
        /// <param name="vessel">The mission's vessel; may be null.</param>
        /// <param name="settings">Settings supplying the fallback landing mode.</param>
        /// <returns>True when the captain must push.</returns>
        internal static bool RequiresCaptainPush(Vessel? vessel, ArmadaSettings settings)
        {
            LandingModeEnum? resolved = vessel?.LandingMode ?? settings?.LandingMode;
            if (resolved == null)
            {
                return true;
            }
            return resolved.Value == LandingModeEnum.PullRequest;
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
        /// Builds the papercut directive. A captain that meets broken friction -- a stale doc, a dead
        /// link, a brief that contradicts itself, a missing sibling repository -- works around it and
        /// says nothing, so the next captain on the same vessel pays the same cost again. This asks for
        /// one line per problem and forbids fixing it out of scope, which keeps the report cheap and the
        /// diff clean.
        ///
        /// Built in code for the same reason as the tool-batching section: a stored template row that
        /// predates this module would silently drop it.
        /// </summary>
        /// <summary>
        /// Builds the playbook wrapper for a read-only mission. The stock wrapper calls its content
        /// required reading, but vessel playbooks describe implementation workflows (code style,
        /// test conventions) and often carry validation commands a read-only mission is forbidden to
        /// run; probe captains reported that as a delivered-unresolved contradiction (2026-08-10).
        /// The read-only wrapper demotes the playbooks to reference material and states the
        /// precedence explicitly.
        /// </summary>
        /// <param name="playbooksMarkdown">Rendered playbook content.</param>
        /// <returns>The read-only playbook wrapper section.</returns>
        internal static string BuildReadOnlyPlaybooksWrapperSection(string playbooksMarkdown)
        {
            return
                "## Playbooks\n" +
                "The playbooks below describe implementation workflows (code style, test conventions) and may " +
                "name validation commands. This is a report-only mission: read what applies, skip the rest, " +
                "and the mission rules win on conflict.\n" +
                "\n" +
                playbooksMarkdown;
        }

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
        /// Builds the progress-note directive. The output marker is the bounded, runtime-neutral
        /// reporting path even when the captain also receives the local MCP connection.
        /// </summary>
        /// <returns>The progress-notes section.</returns>
        internal static string BuildProgressNotesSection()
        {
            return
                "## Board Notes\n" +
                "\n" +
                "Post short progress notes to the shared coordination board so operator sessions know what " +
                "you are doing. One line each:\n" +
                "\n" +
                "`[ARMADA:NOTE] one-line note`\n" +
                "\n" +
                "- Use them at milestones: what you claimed, what you found, what landed, what is blocked.\n" +
                "- Plain text only. No credentials, tokens, or absolute host paths.\n" +
                "- Twenty per mission is the limit. They are visible to every session and the dashboard.\n";
        }

        /// <summary>
        /// Builds the code-retrieval guidance. The staged context pack is a plain file in the dock and
        /// needs no tooling to read; when it is absent or incomplete, ordinary file search is the
        /// fallback. The guidance stays useful when a deployment explicitly disables captain MCP.
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
        /// Returns true when a mission with this persona is REQUIRED to produce a code diff, so that an
        /// empty diff is a hard failure rather than a legitimate no-op. Only the Worker persona (the
        /// primary code producer, and the null/default per <see cref="Mission.Persona"/>) must produce
        /// changes. Architect emits stdout mission markers, and reviewer personas (Judge, TestEngineer,
        /// *Analyst, *Reviewer, etc.) approve or reject without committing, so an empty diff from them
        /// is a legitimate successful no-op. Kept in step with the landing gate in MissionLandingHandler
        /// so the completion-time check and the landing check cannot disagree.
        /// </summary>
        /// <param name="persona">Mission persona name.</param>
        internal static bool PersonaMustProduceChanges(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return true;
            string normalized = persona.Trim().ToLowerInvariant().Replace(" ", "");
            return normalized == "worker";
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
        /// Resolve a distinct primary review lens for a Judge mission that runs alongside other
        /// Judges on the same voyage and stage (perspective-diverse pool, anti-Goodhart). Judges are
        /// assigned lenses round-robin over the canonical lens list; a solo Judge gets no primary
        /// lens and keeps the combined three-lens instruction. The assignment is recorded as a
        /// <c>mission.judge_lens</c> event for later analysis.
        /// </summary>
        /// <param name="mission">Mission being briefed.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The assigned primary lens, or null when the mission is not a parallel Judge.</returns>
        internal async Task<string?> ResolveJudgeLensAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) return null;
            if (!String.Equals(mission.Persona, "Judge", StringComparison.OrdinalIgnoreCase)) return null;
            if (String.IsNullOrEmpty(mission.VoyageId)) return null;

            try
            {
                List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId!, token).ConfigureAwait(false);
                List<Mission> parallelJudges = voyageMissions
                    .Where(m => m != null
                        && String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase)
                        && m.StageOrder == mission.StageOrder)
                    .OrderBy(m => m.CreatedUtc)
                    .ToList();

                if (parallelJudges.Count <= 1)
                {
                    return null;
                }

                int index = parallelJudges.FindIndex(m => String.Equals(m.Id, mission.Id, StringComparison.Ordinal));
                if (index < 0) index = 0;
                string lens = MissionPromptBuilder.JudgeLensNames[index % MissionPromptBuilder.JudgeLensNames.Length];

                try
                {
                    ArmadaEvent evt = new ArmadaEvent
                    {
                        EventType = "mission.judge_lens",
                        EntityType = "mission",
                        EntityId = mission.Id,
                        MissionId = mission.Id,
                        VoyageId = mission.VoyageId,
                        Message = "Judge mission " + mission.Id + " assigned primary lens " + lens,
                        Payload = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            missionId = mission.Id,
                            lens,
                            parallelJudgeCount = parallelJudges.Count,
                            index
                        })
                    };
                    await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not record judge lens for mission " + mission.Id + ": " + ex.Message);
                }

                return lens;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not resolve judge lens for mission " + mission.Id + ": " + ex.Message);
                return null;
            }
        }

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

        private async Task<string> ResolvePersonaPromptAsync(
            string? persona,
            Dictionary<string, string> templateParams,
            PersonaOverride? personaOverride,
            CancellationToken token)
        {
            return await MissionPromptBuilder.ResolvePersonaPromptAsync(persona, templateParams, _PromptTemplates, personaOverride, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve the effective per-project persona override for a mission's persona by selecting the
        /// vessel's project profile (vessel then fleet then global). Best-effort: returns null on any
        /// error so a profile lookup never blocks dispatch.
        /// </summary>
        /// <param name="vessel">Vessel the mission runs against.</param>
        /// <param name="persona">Persona name the mission runs as.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The persona override to apply, or null when no profile customizes this persona.</returns>
        internal async Task<PersonaOverride?> ResolvePersonaOverrideAsync(Vessel vessel, string? persona, CancellationToken token)
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
                _Logging.Warn(_Header + "error resolving persona override for vessel " + vessel.Id + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolve the vessel's project-profile skills into a combined markdown block for prompt
        /// injection. Best-effort: returns an empty string on any error or when no profile or skill
        /// applies. Skill references in the profile may be skill ids or skill names.
        /// </summary>
        /// <param name="vessel">Vessel the mission runs against.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rendered skills markdown, or an empty string when nothing applies.</returns>
        internal async Task<string> ResolveSkillsMarkdownAsync(Vessel vessel, CancellationToken token)
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
                _Logging.Warn(_Header + "error resolving skills for vessel " + vessel.Id + ": " + ex.Message);
                return String.Empty;
            }
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

                case "mission.rules_no_push":
                    return
                        "## Rules\n" +
                        "- Work only within this worktree directory\n" +
                        "- Commit all changes to the current branch\n" +
                        "- Do NOT push. This vessel lands from its own repository, so your commit is already reachable once you make it. A push creates a remote branch nothing will ever delete.\n" +
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

            // Park the branch under refs/armada-preserved/ before deleting it. A terminal mission's
            // branch is frequently UNMERGED, so the delete would otherwise leave its commit as a
            // dangling object -- recoverable only by someone who wrote the SHA down first. Preserving
            // to a ref keeps the work reachable by name while removing it from the branch list, which
            // is what makes reaping unlanded work safe rather than destructive.
            //
            // Best-effort and ordered first: if preservation fails, the reap is skipped rather than
            // risking the only pointer to a captain's commit.
            bool preserved = false;
            string preservedRef = "refs/armada-preserved/" + branchName;
            try
            {
                await _Git.CopyRefAsync(vessel.LocalPath, "refs/heads/" + branchName, preservedRef, token).ConfigureAwait(false);
                preserved = true;
                _Logging.Info(_Header + "preserved terminal mission branch " + branchName + " as " + preservedRef + " for mission " + mission.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception preserveEx)
            {
                _Logging.Warn(_Header + "could not preserve terminal mission branch " + branchName +
                    " as " + preservedRef + " for mission " + mission.Id + ": " + preserveEx.Message +
                    " -- retaining the branch rather than reaping unpreserved work");
            }

            if (!preserved)
            {
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
                        // A read-only voyage must stay read-only end to end: missions spawned from an
                        // Architect's plan inherit the Architect mission's mode, so an audit or research
                        // voyage never silently turns its plan blocks into implementing missions.
                        additionalWorker.Mode = completedMission.Mode;
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
        /// Build the persona preamble injected at the top of a downstream handoff brief. The preamble is
        /// mode-aware: an Audit or Research downstream stage produces a report, so its Worker and
        /// TestEngineer preambles must say "validate and report" rather than "implement and write tests",
        /// or the same brief would order a captain to do the opposite of its own output contract.
        /// </summary>
        /// <param name="persona">Persona of the downstream mission.</param>
        /// <param name="mode">Mode of the downstream mission.</param>
        /// <returns>The persona preamble, or an empty string for a persona without one.</returns>
        internal static string BuildPersonaPreamble(string? persona, MissionModeEnum mode)
        {
            bool reportOnly = (mode == MissionModeEnum.Audit || mode == MissionModeEnum.Research);

            switch (persona)
            {
                case "Worker":
                    if (reportOnly)
                    {
                        return "## Your Role: Worker (Investigate and Report)\n\n" +
                            "This is a report-only " + mode + " mission: your deliverable is a report, not a code change. " +
                            "Do not edit, commit, or push. Investigate the scope below, gather exact evidence for every claim, " +
                            "and state plainly when the evidence does not settle a question.\n\n";
                    }

                    return "## Your Role: Worker (Implement)\n\n" +
                        "You are implementing code changes based on the Architect's plan. " +
                        "Review the prior stage output below and implement the described changes.\n\n";

                case "TestEngineer":
                    if (reportOnly)
                    {
                        return "## Your Role: TestEngineer (Validate the Report)\n\n" +
                            "This is a report-only " + mode + " mission: your deliverable is a verified report, not tests. " +
                            "Do not write tests, and do not edit, commit, or push. Review the prior stage output below and validate " +
                            "that the evidence supports every claim, that cited paths and source references resolve, and that the " +
                            "report is complete and internally consistent. Call out anything the prior stage asserts without evidence. " +
                            "End with a standalone `[ARMADA:RESULT] COMPLETE` line and a short summary.\n\n";
                    }

                    return "## Your Role: TestEngineer (Write Tests)\n\n" +
                        "You are writing tests for code changes made by the Worker. " +
                        "Review the diff below and write unit tests, integration tests, or test harness updates " +
                        "that cover the changes. Follow existing test patterns in the repository. " +
                        "Scope yourself only to this mission, not sibling missions in the same voyage. Cover the " +
                        "happy path, but also add negative or edge-path coverage for validation, timeout, cancellation, " +
                        "retry, cleanup, and error-handling branches when they are in scope. Include short " +
                        "`## Coverage Added`, `## Negative Paths`, and `## Residual Risks` sections. " +
                        "End with a standalone `[ARMADA:RESULT] COMPLETE` line and a short summary.\n\n";

                case "Judge":
                    return "## Your Role: Judge (Review)\n\n" +
                        "You are reviewing the completed work for correctness, completeness, scope compliance, " +
                        "test adequacy, and failure-mode safety. Examine the diff below against the current mission " +
                        "description only, not sibling missions in the same voyage. Assume there may be at least " +
                        "one hidden bug. Your response must include `## Completeness`, `## Correctness`, `## Tests`, " +
                        "`## Failure Modes`, and `## Verdict` sections. A PASS is only allowed when tests are adequate, " +
                        "negative-path coverage for validation, timeout, cancellation, retry, cleanup, and error-handling " +
                        "changes is present or justified, and failure modes were explicitly reviewed. End with a standalone line " +
                        "`[ARMADA:VERDICT] PASS`, `[ARMADA:VERDICT] FAIL`, or `[ARMADA:VERDICT] NEEDS_REVISION`.\n\n";

                default:
                    return "";
            }
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
            string personaPreamble = BuildPersonaPreamble(nextMission.Persona, nextMission.Mode);

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
            // Strip this upstream's own previous block first, so a repeated handoff replaces rather than
            // duplicates it. Then shrink every OTHER handoff block that is already present. What remains
            // is the base brief plus short references to earlier stages, and the new block below is the
            // only one carried in full. Without the second step the total cap is spent on the oldest
            // stage's diff, and the newest stage's diff is what gets cut.
            string existingDescription = CompactOlderHandoffBlocks(
                StripHandoffBlock(nextMission.Description ?? "", completedMission.Id));

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

            // Voyage-tagged board notes are the one case where the coordination board reaches a
            // captain brief: an operator note naming this voyage targets this work. General fleet
            // chatter stays advisory and never injects.
            if (!String.IsNullOrEmpty(nextMission.VoyageId))
            {
                try
                {
                    DateTime noteCutoff = completedMission.StartedUtc ?? completedMission.CreatedUtc;
                    List<CoordinationMessage> voyageNotes = await _Database.CoordinationMessages
                        .EnumerateByVoyageAsync(nextMission.VoyageId!, noteCutoff, 10, token).ConfigureAwait(false);
                    voyageNotes.Reverse();
                    handoffDescription = RemoveVoyageBoardNotesSection(handoffDescription);
                    string notesBlock = BuildVoyageBoardNotesBlock(voyageNotes);
                    if (notesBlock.Length > 0)
                    {
                        handoffDescription = handoffDescription + "\n\n" + notesBlock;
                    }
                }
                catch (NotSupportedException)
                {
                    // Board notes are SQLite/PostgreSQL-only today; other backends skip injection.
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "voyage board-note injection failed for mission " + nextMission.Id + ": " + ex.Message);
                }
            }

            if (handoffDescription.Length > _MaxMissionDescriptionChars)
            {
                _Logging.Warn(_Header + "pipeline handoff: mission " + nextMission.Id + " description of " +
                    handoffDescription.Length + " chars exceeds the " + _MaxMissionDescriptionChars +
                    " char budget; truncating the tail. The full change remains on branch " +
                    (completedMission.BranchName ?? "unknown"));
                handoffDescription = TruncateMissionDescription(
                    handoffDescription, _MaxMissionDescriptionChars, completedMission.BranchName);
            }

            nextMission.Description = handoffDescription;
            nextMission.BranchName = completedMission.BranchName;
            nextMission.PrestagedFiles = MergePrestagedFiles(completedMission.PrestagedFiles, nextMission.PrestagedFiles);
            nextMission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(nextMission, token).ConfigureAwait(false);

            // Stage-lag hardening: a prior stage whose dock is detached (for example the
            // PortingReferenceAnalyst persona) commits on a detached HEAD, so its produced commit
            // may never reach the shared mission branch ref -- it only lands in
            // refs/armada-preserved/<branch> when the dock is reclaimed. Without this, the next
            // attached stage (TestEngineer, Judge) cuts its dock from the stale branch head and
            // silently loses the prior stage's source-fidelity work. Force the shared branch ref
            // (and its origin twin) forward to the dependency's actual produced commit while its
            // dock is still alive, so every downstream stage starts from it.
            await AdvanceHandoffBranchToProducedCommitAsync(completedMission, token).ConfigureAwait(false);

            _Logging.Info(_Header + "pipeline handoff: prepared mission " + nextMission.Id +
                " (" + nextMission.Persona + ") with context from " + completedMission.Id +
                " (" + completedMission.Persona + ")");
        }

        /// <summary>
        /// Advances the shared mission branch ref (and its origin twin) to the produced commit of a
        /// completed pipeline stage whose dock is detached, so downstream attached stages never start
        /// from a stale branch head that predates the stage's work.
        /// </summary>
        /// <remarks>
        /// A stage whose persona returns <c>false</c> from
        /// <see cref="PersonaRequiresBranchAttachment"/> (for example PortingReferenceAnalyst) is
        /// provisioned a DETACHED worktree. Its captain commits on a detached HEAD, so a plain
        /// <c>git push origin HEAD</c> cannot advance the mission branch; the produced commit is only
        /// captured to <c>refs/armada-preserved/&lt;branch&gt;</c> when the dock is reclaimed. The
        /// next attached stage (TestEngineer, Judge) then cuts its dock from the stale branch ref and
        /// silently loses the prior stage's source-fidelity work. This method resolves the produced
        /// commit from the still-alive dock HEAD and force-advances both the local branch ref and
        /// <c>origin/&lt;branch&gt;</c> to it, so the next stage's stage-lag guard fast-forwards to
        /// the complete work.
        /// </remarks>
        /// <param name="completedMission">The upstream stage that produced the work.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task AdvanceHandoffBranchToProducedCommitAsync(Mission completedMission, CancellationToken token)
        {
            if (completedMission == null) throw new ArgumentNullException(nameof(completedMission));
            if (_Git == null) return;
            if (String.IsNullOrEmpty(completedMission.BranchName)) return;
            if (String.IsNullOrEmpty(completedMission.VesselId)) return;

            Dock? dock = await ReadMissionDockAsync(completedMission, token).ConfigureAwait(false);
            if (dock == null) return;
            if (String.IsNullOrEmpty(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath)) return;

            string? producedCommit = await _Git.GetHeadCommitHashAsync(dock.WorktreePath, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(producedCommit)) return;

            Vessel? vessel = !String.IsNullOrEmpty(completedMission.TenantId)
                ? await _Database.Vessels.ReadAsync(completedMission.TenantId, completedMission.VesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(completedMission.VesselId, token).ConfigureAwait(false);
            if (vessel == null || String.IsNullOrEmpty(vessel.LocalPath)) return;

            try
            {
                string branchRef = "refs/heads/" + completedMission.BranchName;
                string? currentTip = await _Git.GetRevisionShaAsync(vessel.LocalPath, branchRef, token).ConfigureAwait(false);

                // Only advance when the produced commit is actually ahead of the current branch tip.
                // An already-pushed stage (attached personas that did push) is left untouched, and a
                // produced commit behind or equal to the tip (for example an empty or reverted dock)
                // must never move the branch backwards.
                if (!String.IsNullOrEmpty(currentTip)
                    && String.Equals(currentTip, producedCommit, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await _Git.ForceUpdateBranchRefAsync(vessel.LocalPath, completedMission.BranchName, producedCommit, token).ConfigureAwait(false);
                await _Git.PushRefSpecAsync(vessel.LocalPath, branchRef, branchRef, token).ConfigureAwait(false);

                _Logging.Info(_Header + "stage-lag hardening: advanced branch " + completedMission.BranchName +
                    " to produced commit " + producedCommit + " from stage " + completedMission.Id +
                    " (" + completedMission.Persona + ") before downstream handoff");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The handoff must not fail because the branch could not be advanced; the downstream
                // stage still proceeds and the operator log records the failure to inspect.
                _Logging.Warn(_Header + "stage-lag hardening could not advance branch " +
                    completedMission.BranchName + " to " + producedCommit + " from stage " +
                    completedMission.Id + ": " + ex.Message);
            }
        }

        // Max characters of prior-stage diff embedded into the next stage's brief. A large generated-output diff
        // (e.g. a regenerated data-file snapshot with hundreds of files) can otherwise overflow the reviewing
        // model's context ("Prompt is too long"). The full change always remains on the branch for inspection.
        private const int _MaxReviewDiffChars = 60000;

        // Hard ceiling on a persisted mission description. The per-part caps above bound one handoff block
        // (8,000 chars of agent output plus _MaxReviewDiffChars of diff), but they cannot bound the total
        // once a brief carries a base description, a persona preamble, and a handoff block. This is the
        // backstop that keeps a runaway brief out of the captain prompt entirely. The value is sized so a
        // persisted description cannot by itself exceed the captain instruction budget (32 KiB): the
        // metadata module embeds it, so a description larger than the whole brief would always be cut at
        // render time. The render-time bound in <see cref="BoundMetadataDescription"/> is the first line of
        // defense; this backstop keeps the persisted record itself from growing without limit.
        private const int _MaxMissionDescriptionChars = 20000;

        // Cap on the description embedded in the mission.metadata module. The module also carries the
        // persona prompt and the mission header, so the embedded description is capped well below the
        // total captain-instruction budget. Head and tail are both preserved: the head holds the base
        // brief, and the tail holds the newest handoff block, which carries the diff a reviewing stage
        // needs. Middle content is elided with a visible marker.
        // Bounds on the git anchors module. The module exists to SAVE a captain context, so it must
        // stay small enough that it never competes with the source the captain came to read: a few
        // commits per path is enough to show who last worked there, and a few sample locations is
        // enough to decide whether to open a file.
        internal const int MaxAnchorCommitsPerPath = 5;
        internal const int MaxAnchorSampleLocations = 3;
        internal const int MaxGitAnchorsSectionChars = 4000;

        internal const int _MaxMetadataDescriptionChars = 12000;
        internal const int _MaxMetadataDescriptionHeadChars = 4000;

        // Opening marker of a prior-stage handoff block, keyed by the upstream mission id. Present so a
        // repeated handoff replaces its own previous block rather than appending a duplicate.
        private const string _HandoffMarkerPrefix = "<!-- ARMADA:HANDOFF:";

        private const string _HandoffMarkerSuffix = " -->";

        private static List<PrestagedFile>? MergePrestagedFiles(
            List<PrestagedFile>? inherited,
            List<PrestagedFile>? existing)
        {
            if ((inherited == null || inherited.Count == 0) && (existing == null || existing.Count == 0))
                return null;

            List<PrestagedFile> merged = new List<PrestagedFile>();
            HashSet<string> destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (existing != null)
            {
                foreach (PrestagedFile entry in existing)
                {
                    if (entry == null) continue;
                    merged.Add(ClonePrestagedFile(entry));
                    destinations.Add(NormalizePrestagedDestination(entry.DestPath));
                }
            }

            if (inherited != null)
            {
                foreach (PrestagedFile entry in inherited)
                {
                    if (entry == null) continue;
                    string destination = NormalizePrestagedDestination(entry.DestPath);
                    if (destinations.Contains(destination)) continue;
                    merged.Add(ClonePrestagedFile(entry));
                    destinations.Add(destination);
                }
            }

            return merged;
        }

        private static PrestagedFile ClonePrestagedFile(PrestagedFile entry)
        {
            return new PrestagedFile(entry.SourcePath ?? "", entry.DestPath ?? "")
            {
                Content = entry.Content,
                ReadOnly = entry.ReadOnly
            };
        }

        private static string NormalizePrestagedDestination(string destination)
        {
            return (destination ?? "").Replace('\\', '/').TrimStart('/');
        }

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
        /// Shrinks every handoff block already present in a description down to a short reference.
        ///
        /// A pipeline of four stages leaves three handoff blocks in the last stage's description. Each one
        /// can hold thousands of characters of agent output and tens of thousands of characters of diff.
        /// Only the newest block is worth carrying in full: it holds the work the next stage must act on.
        /// The older blocks describe work that is already on the branch, where the captain can read it in
        /// full and at no cost to its context.
        ///
        /// Without this, the total cap is still respected, but it is spent on the OLDEST content, and the
        /// newest block is what gets cut. That is the wrong way round.
        ///
        /// The caller runs this before it appends the new block, so the result is: newest block in full,
        /// every older block a reference line. A block that is already compact is left alone, so running
        /// this twice changes nothing.
        /// </summary>
        /// <param name="description">Existing mission description; may be null or empty.</param>
        /// <param name="keepNewestFull">
        /// When true, the last block in the description is left at full size and only the ones before it
        /// are reduced. The pipeline handoff passes false, because it is about to append a newer block of
        /// its own. A rescue passes true, because no newer block is coming and the last block is the most
        /// recent context the rescued mission had.
        /// </param>
        /// <returns>The description with existing handoff blocks reduced to references.</returns>
        public static string CompactOlderHandoffBlocks(string? description, bool keepNewestFull = false)
        {
            if (String.IsNullOrEmpty(description)) return description ?? "";
            if (!description.Contains(_HandoffMarkerPrefix, StringComparison.Ordinal)) return description;

            const string separator = "\n\n---\n";

            int newestMarkerIndex = keepNewestFull
                ? description.LastIndexOf(_HandoffMarkerPrefix, StringComparison.Ordinal)
                : -1;

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            int cursor = 0;

            while (true)
            {
                int markerIndex = description.IndexOf(_HandoffMarkerPrefix, cursor, StringComparison.Ordinal);
                if (markerIndex < 0)
                {
                    builder.Append(description.Substring(cursor));
                    break;
                }

                int suffixIndex = description.IndexOf(_HandoffMarkerSuffix, markerIndex, StringComparison.Ordinal);
                if (suffixIndex < 0)
                {
                    // A marker with no terminator is malformed. Copy the rest verbatim rather than
                    // guessing where the block ends; losing real brief text is worse than a large brief.
                    builder.Append(description.Substring(cursor));
                    break;
                }

                int idStart = markerIndex + _HandoffMarkerPrefix.Length;
                string missionId = description.Substring(idStart, suffixIndex - idStart);
                int markerEnd = suffixIndex + _HandoffMarkerSuffix.Length;

                int blockStart = markerIndex;
                if (markerIndex >= separator.Length &&
                    String.CompareOrdinal(description, markerIndex - separator.Length, separator, 0, separator.Length) == 0)
                {
                    blockStart = markerIndex - separator.Length;
                }

                int nextMarkerIndex = description.IndexOf(_HandoffMarkerPrefix, markerEnd, StringComparison.Ordinal);
                int blockEnd = nextMarkerIndex < 0 ? description.Length : nextMarkerIndex;
                if (nextMarkerIndex >= separator.Length &&
                    nextMarkerIndex >= 0 &&
                    String.CompareOrdinal(description, nextMarkerIndex - separator.Length, separator, 0, separator.Length) == 0)
                {
                    blockEnd = nextMarkerIndex - separator.Length;
                }

                builder.Append(description.Substring(cursor, blockStart - cursor));

                string blockText = description.Substring(blockStart, blockEnd - blockStart);
                builder.Append(markerIndex == newestMarkerIndex
                    ? blockText
                    : BuildCompactHandoffBlock(missionId, blockText));

                cursor = blockEnd;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Builds the short reference that replaces one older handoff block. It keeps the facts a later
        /// stage needs to FIND the work — the upstream mission id and the branch — and drops the pasted
        /// agent output and diff, which are both still on the branch.
        /// </summary>
        /// <param name="missionId">Upstream mission id taken from the block marker.</param>
        /// <param name="block">The full block text, including its leading separator when present.</param>
        /// <returns>The compact replacement block, or the original when it is already compact.</returns>
        private static string BuildCompactHandoffBlock(string missionId, string block)
        {
            if (block.Contains(_CompactedHandoffHeading, StringComparison.Ordinal)) return block;

            string branch = "unknown";
            string persona = "prior";

            string[] lines = block.Split('\n');
            foreach (string line in lines)
            {
                if (line.StartsWith("Branch: ", StringComparison.Ordinal))
                {
                    branch = line.Substring("Branch: ".Length).Trim();
                }
                else if (line.StartsWith("The previous pipeline stage (", StringComparison.Ordinal))
                {
                    int open = line.IndexOf('(');
                    int close = line.IndexOf(')', open + 1);
                    if (open >= 0 && close > open) persona = line.Substring(open + 1, close - open - 1);
                }
            }

            string compacted = "\n\n---\n" +
                BuildHandoffMarker(missionId) + "\n" +
                _CompactedHandoffHeading + "\n" +
                "Stage: " + persona + ". Mission: " + missionId + ". Branch: " + branch + ".\n" +
                "Its full output and diff are not repeated here. They are on that branch, " +
                "and the mission record holds the complete log. Read them only if this stage needs them.\n";

            // A stage that produced very little leaves a block smaller than this reference. Replacing it
            // would add bytes to save bytes, and would also throw away real content to do it. Keep
            // whichever is shorter; the point of this method is a smaller brief, not a compacted one.
            return compacted.Length < block.Length ? compacted : block;
        }

        // Heading that marks an already-compacted handoff block. Present so compaction is idempotent and
        // so a reader can tell a shortened block from a stage that simply produced little output.
        private const string _CompactedHandoffHeading = "## Prior Stage (compacted)";

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
        /// Truncates an over-budget mission description, preserving the head (the base brief) and the tail
        /// (the newest handoff block, which carries the diff a reviewing stage needs) and eliding the middle
        /// with a visible marker. Returns the input unchanged when it fits. A tail-only cut would drop the
        /// newest prior-stage block, which is exactly the content the downstream stage must review.
        /// </summary>
        /// <param name="description">Description to bound.</param>
        /// <param name="maxChars">Maximum characters allowed.</param>
        /// <returns>The bounded description.</returns>
        internal static string TruncateMissionDescription(string description, int maxChars, string? branchName = null)
        {
            if (String.IsNullOrEmpty(description) || description.Length <= maxChars) return description;

            string branchNote = String.IsNullOrEmpty(branchName)
                ? " the full change is on the branch"
                : " the full change is on branch " + branchName;
            string marker = "\n\n...(mission brief truncated to fit the budget; the middle of the prior-stage diff is elided;" + branchNote + ")\n";
            int headChars = Math.Max(0, (maxChars - marker.Length) / 3);
            return BuildBoundedDescription(description, maxChars, headChars, marker);
        }

        /// <summary>
        /// Bounds the description embedded in the mission.metadata module so a single module can never
        /// exceed the captain instruction budget, whatever the persisted description holds. Preserves the
        /// head (the base brief) and the tail (the newest handoff block, which carries the diff a
        /// reviewing stage needs) and elides the middle with a visible marker. Returns the input unchanged
        /// when it fits, and a default string when the description is null or empty so the module never
        /// renders a blank Description section.
        /// </summary>
        /// <param name="description">Persisted mission description.</param>
        /// <returns>A bounded copy fit for the metadata module.</returns>
        internal static string BoundMetadataDescription(string? description)
        {
            if (String.IsNullOrEmpty(description)) return "No additional description provided.";

            if (description.Length <= _MaxMetadataDescriptionChars) return description;

            const string marker = "\n\n...(middle of the mission description elided to fit the captain brief; the full description is in the mission record)\n";
            return BuildBoundedDescription(description, _MaxMetadataDescriptionChars, _MaxMetadataDescriptionHeadChars, marker);
        }

        /// <summary>
        /// Shared head-and-tail elision: keeps the first <paramref name="headChars"/> characters and the
        /// newest tail of the description on line boundaries, joined by a visible marker. The head holds
        /// the base brief; the tail holds the newest handoff block. Returns the input unchanged when it
        /// already fits within <paramref name="maxChars"/>.
        /// </summary>
        /// <param name="description">Description to bound; assumed non-null and over budget.</param>
        /// <param name="maxChars">Maximum characters allowed in the result.</param>
        /// <param name="headChars">Characters kept from the head before eliding.</param>
        /// <param name="marker">Visible elision marker placed between head and tail.</param>
        /// <returns>The bounded description.</returns>
        private static string BuildBoundedDescription(string description, int maxChars, int headChars, string marker)
        {
            int tailBudget = Math.Max(0, maxChars - marker.Length - headChars);
            int head = Math.Min(headChars, description.Length);
            int tail = Math.Min(tailBudget, description.Length - head);

            // Snap the head cut to a line boundary so the elision never splits a sentence mid-line.
            int headCut = description.LastIndexOf('\n', Math.Min(head, description.Length - 1)) + 1;
            if (headCut <= 0 || headCut > head) headCut = head;
            head = headCut;

            int tailStart = Math.Max(head, description.Length - tail);
            if (tailStart < description.Length && tailStart > 0)
            {
                // Snap the tail start forward to the next line boundary.
                int newline = description.IndexOf('\n', tailStart);
                if (newline > 0 && newline < description.Length) tailStart = newline + 1;
            }

            // Never begin the visible tail in the middle of a diff hunk: a `diff --git ` line opens a
            // file section, so snap forward to the next one when it exists. Without this an elided brief
            // opens mid-file inside a hunk, which reads as a truncated review even though the marker names
            // where the full change lives. The handoff diff is the last thing in the description, so this
            // mainly applies when the cut lands between two file sections of the same diff.
            if (tailStart < description.Length)
            {
                int diffHeader = description.IndexOf("\ndiff --git ", tailStart, StringComparison.Ordinal);
                if (diffHeader >= 0 && diffHeader + 1 < description.Length)
                {
                    tailStart = diffHeader + 1;
                }
            }

            if (tailStart <= head)
            {
                return description.Substring(0, head).TrimEnd() + marker + "(tail unavailable; see the mission record)";
            }

            return description.Substring(0, head).TrimEnd()
                + marker
                + description.Substring(tailStart).TrimStart();
        }

        // Modules that carry vessel or mission context and may be elided by the total-budget backstop
        // when a brief exceeds the captain instruction budget. Never includes the persona prompt, the
        // rules, or the metadata skeleton -- those are the mission itself.
        private static readonly string[] _ElidableBriefModules =
        {
            "mission.objective_scope",
            "mission.existing_instructions_wrapper",
            "mission.project_context_wrapper",
            "mission.code_style_wrapper",
            "mission.model_context_wrapper",
            "mission.playbooks_wrapper"
        };

        // Per-module cap applied by the total-budget backstop, in characters. A single module is never
        // elided below this floor: a module that cannot fit within its share is dropped entirely by the
        // caller instead of shredding it into unreadable fragments.
        private const int _MinElidedModuleChars = 800;

        // Head budget used when eliding a content module, in characters.
        private const int _ElidedModuleHeadChars = 600;

        /// <summary>
        /// Total-budget backstop: when the assembled brief still exceeds the captain instruction budget
        /// after every per-module cap, elides the largest content-bearing modules in place -- smallest
        /// first, so the highest-signal modules survive whole -- until the file fits the budget. Modules
        /// are elided head+tail on line boundaries with a visible marker, and the ledger is updated so
        /// the budget telemetry reports what was actually written. Returns the bounded content.
        /// </summary>
        /// <param name="content">Assembled brief content.</param>
        /// <param name="ledger">Ledger holding the assembled module texts and sizes.</param>
        /// <param name="budgetBytes">Captain instruction byte budget; 0 or negative disables the backstop.</param>
        /// <returns>The bounded content, identical when already within budget.</returns>
        internal static string EnforceTotalBriefBudget(string content, PromptModuleLedger ledger, int budgetBytes)
        {
            if (String.IsNullOrEmpty(content)) return content ?? "";
            if (budgetBytes <= 0) return content;

            int currentBytes = System.Text.Encoding.UTF8.GetByteCount(content);
            if (currentBytes <= budgetBytes) return content;

            string working = content;
            bool changed = true;

            while (changed && System.Text.Encoding.UTF8.GetByteCount(working) > budgetBytes)
            {
                changed = false;
                List<KeyValuePair<string, int>> largestFirst = ledger.GetModulesLargestFirst();

                foreach (KeyValuePair<string, int> entry in largestFirst)
                {
                    if (System.Text.Encoding.UTF8.GetByteCount(working) <= budgetBytes) break;

                    string name = entry.Key;
                    if (!IsElidableBriefModule(name)) continue;
                    if (entry.Value <= _MinElidedModuleChars) continue;

                    string? moduleText = ledger.GetModuleText(name);
                    if (String.IsNullOrEmpty(moduleText)) continue;

                    // Elide the module down to fit its share of the remaining budget: head + tail with
                    // a marker, sized so the module no longer exceeds its proportional allotment.
                    int overBytes = System.Text.Encoding.UTF8.GetByteCount(working) - budgetBytes;
                    int currentChars = moduleText.Length;
                    int targetChars = Math.Max(_MinElidedModuleChars, currentChars - overBytes - 64);

                    string marker = "\n\n...(content elided to fit the captain brief budget; see the mission record)\n";
                    string bounded = BuildBoundedDescription(moduleText, targetChars, _ElidedModuleHeadChars, marker);
                    if (bounded.Length >= moduleText.Length) continue;

                    // Replace in the assembled content and in the ledger so both stay in lockstep.
                    int idx = working.IndexOf(moduleText, StringComparison.Ordinal);
                    if (idx < 0) continue;

                    working = working.Substring(0, idx) + bounded + working.Substring(idx + moduleText.Length);
                    ledger.ReplaceModuleText(name, bounded);
                    changed = true;
                }
            }

            return working;
        }

        /// <summary>
        /// Reports whether a module may be elided by the total-budget backstop. Content-bearing modules
        /// that repeat vessel or mission context are elidable; the persona prompt, rules, and metadata
        /// skeleton are not.
        /// </summary>
        /// <param name="moduleName">Ledger module name.</param>
        /// <returns>True when the module may be elided.</returns>
        internal static bool IsElidableBriefModule(string? moduleName)
        {
            if (String.IsNullOrEmpty(moduleName)) return false;
            foreach (string elidable in _ElidableBriefModules)
            {
                if (String.Equals(moduleName, elidable, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Scopes a git diff so it fits a reviewing model's context. Under the budget it is returned unchanged.
        /// Over the budget, per-file sections are kept whole smallest-first (so small CODE diffs survive intact)
        /// and the largest files (typically bulk generated DATA) are elided to their header + a line-count note --
        /// so the reviewer still sees WHICH files changed and by how much, without the overflowing content.
        /// Bulk generated data files (large JSON/CSV/XML under an output/export/bundle path) are ALWAYS elided
        /// to a summary regardless of budget: a snapshot-regeneration voyage changes hundreds of data rows that
        /// carry no review signal, and feeding them to a Judge overflows its context. The reviewer
        /// gets the file list, the line counts, and the manifest/code diffs instead.
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
            // Bulk generated data files are excluded from the keep-whole pool entirely.
            List<int> order = Enumerable.Range(0, sections.Count).OrderBy(i => sections[i].Length).ToList();
            HashSet<int> keepWhole = new HashSet<int>();
            int used = 0;
            foreach (int i in order)
            {
                if (IsBulkGeneratedDataSection(sections[i])) continue;
                if (used + sections[i].Length <= maxChars) { keepWhole.Add(i); used += sections[i].Length; }
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int elided = 0;
            int generatedElided = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                if (keepWhole.Contains(i)) { sb.Append(sections[i]); continue; }
                string section = sections[i];
                int nl = section.IndexOf('\n');
                string header = nl < 0 ? section : section.Substring(0, nl);
                int lines = section.Count(c => c == '\n');
                if (IsBulkGeneratedDataSection(section))
                {
                    sb.Append(header).Append("\n... (generated data file: ").Append(lines)
                        .Append(" lines elided; review the code and manifest, inspect the data rows on the branch)\n");
                    generatedElided++;
                }
                else
                {
                    sb.Append(header).Append("\n... (").Append(lines)
                        .Append(" lines elided to fit review context; full change is on the branch)\n");
                }
                elided++;
            }
            if (elided > 0)
            {
                sb.Append("\n[note] ").Append(elided)
                    .Append(" large file diff(s) were summarized above to keep the review within context; inspect them on the branch if the change is not obvious from the code diffs and file list.");
                if (generatedElided > 0)
                {
                    sb.Append(" ").Append(generatedElided)
                        .Append(" of them are bulk generated data files omitted by policy (snapshot/bundle regeneration); their row content is not review signal.");
                }
                sb.Append("\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Whether a diff section is a bulk generated data file: a JSON/CSV/XML/DAT file under a
        /// generated-output path (output, export, bundle, generated) that is large enough to carry
        /// no review signal and to overflow a reviewing model's context when present in bulk.
        /// </summary>
        private static bool IsBulkGeneratedDataSection(string section)
        {
            if (section.Length < _GeneratedDataElideThresholdChars)
            {
                return false;
            }

            int nl = section.IndexOf('\n');
            string header = nl < 0 ? section : section.Substring(0, nl);
            int markerIdx = header.IndexOf(" b/", StringComparison.Ordinal);
            string? path = null;
            if (markerIdx > 0)
            {
                string aPart = header.Substring(0, markerIdx);
                int aIdx = aPart.IndexOf(" a/", StringComparison.Ordinal);
                if (aIdx >= 0) path = aPart.Substring(aIdx + 3);
            }
            if (String.IsNullOrEmpty(path))
            {
                return false;
            }

            bool dataExtension = false;
            foreach (string ext in _GeneratedDataExtensions)
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    dataExtension = true;
                    break;
                }
            }
            if (!dataExtension)
            {
                return false;
            }

            foreach (string marker in _GeneratedDataPathMarkers)
            {
                if (path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
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

        /// <summary>
        /// Builds the voyage board-notes section appended at the tail of a handoff brief. Notes are
        /// listed oldest first with author and time. Idempotent across repeated handoffs: any prior
        /// board-notes section is stripped before the new one is written. Returns an empty string
        /// when there is nothing to say.
        /// </summary>
        internal static string BuildVoyageBoardNotesBlock(List<CoordinationMessage>? notes)
        {
            const string headerMarker = "### Board notes on this voyage";
            if (notes == null || notes.Count == 0) return String.Empty;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(headerMarker).Append("\n");
            sb.Append("An operator or peer posted these on the coordination board while the previous stage ran. ")
              .Append("Treat them as instructions for this stage when they direct the work; otherwise context.\n");
            foreach (CoordinationMessage note in notes)
            {
                sb.Append("- [").Append(note.AuthorName).Append("] ").Append(note.Content.Replace("\r", " ")).Append('\n');
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Removes any previously injected voyage board-notes section so a repeated handoff
        /// replaces rather than duplicates it.
        /// </summary>
        internal static string RemoveVoyageBoardNotesSection(string description)
        {
            const string headerMarker = "### Board notes on this voyage";
            if (String.IsNullOrEmpty(description)) return description ?? "";

            int markerIndex = description.IndexOf(headerMarker, StringComparison.Ordinal);
            if (markerIndex < 0) return description;

            // The section runs until the next markdown section header or the end.
            int searchFrom = markerIndex + headerMarker.Length;
            int nextHeader = description.IndexOf("\n### ", searchFrom, StringComparison.Ordinal);
            if (nextHeader < 0) nextHeader = description.IndexOf("\n## ", searchFrom, StringComparison.Ordinal);
            if (nextHeader < 0) return description.Substring(0, markerIndex).TrimEnd();
            return (description.Substring(0, markerIndex).TrimEnd() + "\n" + description.Substring(nextHeader + 1)).TrimEnd();
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

        /// <summary>Outcome of evaluating the independent Checks behind a Judge PASS.</summary>
        internal enum JudgeCheckGate
        {
            /// <summary>All attached Checks are green.</summary>
            GreenChecks,
            /// <summary>At least one attached Check Failed -- PASS is overridden.</summary>
            HasFailed,
            /// <summary>At least one attached Check is Pending/Running -- PASS is held.</summary>
            HasPending,
            /// <summary>No Checks attached, but the Judge documented an environmental exclusion.</summary>
            NoChecksWithExclusion,
            /// <summary>No Checks attached and no documented exclusion -- PASS is rejected.</summary>
            NoChecksNoExclusion
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
                .Where(CheckRunGateRules.ParticipatesInRealSignalGate).ToList();
            if (active.Count == 0) return VoyageCheckGate.NoChecks;
            if (active.Any(c => c.Status == CheckRunStatusEnum.Failed)) return VoyageCheckGate.HasFailed;
            if (active.Any(CheckRunGateRules.IsUnresolved)) return VoyageCheckGate.HasPending;
            return VoyageCheckGate.AllGreen;
        }

        /// <summary>
        /// Evaluates the independent Checks behind a Judge PASS: the voyage's attached Checks plus
        /// any Checks attached to the Judge mission itself, resolved from real command output the
        /// Judge did not produce. A PASS with no Checks at all is only acceptable when the review
        /// documents an environmental exclusion (rule 31) with the
        /// <see cref="_JudgeCheckExclusionMarker"/> marker.
        /// </summary>
        internal async Task<JudgeCheckGate> EvaluateJudgeCheckGateAsync(Mission judgeMission, CancellationToken token)
        {
            Dictionary<string, CheckRun> checks = new Dictionary<string, CheckRun>();
            if (!String.IsNullOrEmpty(judgeMission.VoyageId))
            {
                EnumerationResult<CheckRun> byVoyage = await _Database.CheckRuns
                    .EnumerateAsync(new CheckRunQuery { VoyageId = judgeMission.VoyageId }, token).ConfigureAwait(false);
                foreach (CheckRun c in byVoyage.Objects) checks[c.Id] = c;
            }

            EnumerationResult<CheckRun> byMission = await _Database.CheckRuns
                .EnumerateAsync(new CheckRunQuery { MissionId = judgeMission.Id }, token).ConfigureAwait(false);
            foreach (CheckRun c in byMission.Objects) checks[c.Id] = c;

            List<CheckRun> collected = checks.Values.ToList();
            _LastJudgeGateChecks = collected;
            _LastJudgeReviewedCommit = judgeMission.CommitHash;
            return ClassifyJudgeCheckGate(collected, judgeMission.AgentOutput, judgeMission.CommitHash);
        }

        /// <summary>
        /// The commit the most recent Judge gate evaluation compared its Checks against: the tip the
        /// Judge reviewed. Retained beside <see cref="_LastJudgeGateChecks"/> so a hold message can
        /// say WHICH commit a stale green measured and which one the review is about.
        /// </summary>
        private string? _LastJudgeReviewedCommit = null;

        /// <summary>
        /// The Checks collected by the most recent <see cref="EvaluateJudgeCheckGateAsync"/> call,
        /// retained so a rejection can name the specific records that blocked the PASS. The gate
        /// message previously named only the rule, which left an operator with no way to tell WHICH
        /// Check to inspect - and, when several Checks failed for one environmental cause, no way to
        /// notice that one had been left unresolved.
        /// </summary>
        private List<CheckRun>? _LastJudgeGateChecks = null;

        /// <summary>
        /// Renders the Checks that block a Judge PASS as a compact, operator-actionable list.
        /// Canceled Checks are excluded because the gate ignores them. Returns an empty string when
        /// nothing matches, so callers can append it unconditionally.
        /// </summary>
        internal static string DescribeBlockingChecks(List<CheckRun>? checks, CheckRunStatusEnum status)
        {
            if (checks == null) return String.Empty;
            List<CheckRun> matching = checks
                .Where(c => c != null && c.Status == status)
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            if (matching.Count == 0) return String.Empty;

            List<string> parts = new List<string>();
            foreach (CheckRun c in matching)
            {
                string label = String.IsNullOrWhiteSpace(c.Label) ? c.Type.ToString() : c.Label!;
                parts.Add(c.Id + " (" + c.Type + ": " + label + ")");
            }
            return String.Join(", ", parts);
        }

        /// <summary>
        /// Renders the Checks that a Judge PASS is still waiting on as a compact, operator-
        /// actionable list. Only records that participate in the real-signal gate are named, so the
        /// message points at exactly the records the gate is blocked by. Returns an empty string
        /// when nothing is unresolved, so callers can append it unconditionally.
        /// </summary>
        /// <param name="checks">The Checks collected by the gate. Null renders as empty.</param>
        /// <param name="reviewedCommit">The commit under review; a Passed record for a different
        /// commit is stale and is named with both commits. Null names only unresolved records.</param>
        /// <returns>A comma-separated list of id, type and label, or an empty string.</returns>
        internal static string DescribeUnresolvedChecks(List<CheckRun>? checks, string? reviewedCommit = null)
        {
            if (checks == null) return String.Empty;
            List<CheckRun> matching = checks
                .Where(c => CheckRunGateRules.IsUnresolved(c) || CheckRunGateRules.IsStale(c, reviewedCommit))
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            if (matching.Count == 0) return String.Empty;

            List<string> parts = new List<string>();
            foreach (CheckRun c in matching)
            {
                string label = String.IsNullOrWhiteSpace(c.Label) ? c.Type.ToString() : c.Label!;
                string state = CheckRunGateRules.IsStale(c, reviewedCommit)
                    ? "stale: " + c.Status + " at " + (String.IsNullOrWhiteSpace(c.CommitHash) ? "(no commit)" : c.CommitHash) + " but the review is of " + reviewedCommit
                    : c.Status.ToString();
                parts.Add(c.Id + " (" + c.Type + ": " + label + ", " + state + ")");
            }
            return String.Join(", ", parts);
        }

        /// <summary>
        /// Pure classification of the Judge check gate from the collected Checks and the Judge's
        /// review output. Canceled Checks are ignored, and so are Checks that were armed but never
        /// executed: neither carries command output, so neither can decide the PASS. Of what
        /// remains, a single Failed Check overrides the PASS; a Pending or Running Check holds it;
        /// so does a Passed or Failed Check that measured a commit other than <paramref name="reviewedCommit"/>,
        /// because a verdict for older work says nothing about the tip under review and the executor
        /// re-arms it (<see cref="StaleCheckSupersessionService"/>) while the PASS is held;
        /// no deciding Checks at all requires the documented-exclusion marker in the review.
        /// </summary>
        internal static JudgeCheckGate ClassifyJudgeCheckGate(List<CheckRun> checks, string? agentOutput, string? reviewedCommit = null)
        {
            List<CheckRun> active = (checks ?? new List<CheckRun>())
                .Where(CheckRunGateRules.ParticipatesInRealSignalGate).ToList();
            if (active.Count == 0)
            {
                string output = agentOutput ?? String.Empty;
                return output.Contains(_JudgeCheckExclusionMarker, StringComparison.Ordinal)
                    ? JudgeCheckGate.NoChecksWithExclusion
                    : JudgeCheckGate.NoChecksNoExclusion;
            }
            // A failure for the reviewed commit rejects the PASS. A failure for an OLDER commit is
            // stale exactly as a green is: the reviewed commit may be the fix for it, so it holds
            // the PASS while the executor re-arms at the tip, and the new record decides.
            if (active.Any(c => c.Status == CheckRunStatusEnum.Failed && !CheckRunGateRules.IsStale(c, reviewedCommit))) return JudgeCheckGate.HasFailed;
            if (active.Any(c => CheckRunGateRules.IsUnresolved(c) || CheckRunGateRules.IsStale(c, reviewedCommit))) return JudgeCheckGate.HasPending;
            return JudgeCheckGate.GreenChecks;
        }

        /// <summary>
        /// Reset a mission for an in-place re-run (used for Judge re-runs: missing verdict, or a
        /// PASS held while independent Checks resolve). Clears the agent-produced state so the
        /// re-dispatch starts fresh on a new dock and captain. When
        /// <paramref name="countRecoveryBudget"/> is false, the mission is re-queued WITHOUT
        /// consuming the autonomous-rescue budget (<see cref="Mission.RecoveryAttempts"/>); the
        /// caller tracks its re-run in its own persisted field instead. Used by the missing-verdict
        /// path so an intermittent provider (a Judge that exits with empty output) never blocks the
        /// rescue that a genuinely failed mission still needs.
        /// </summary>
        private async Task ResetMissionForReRunAsync(Mission mission, CancellationToken token, bool countRecoveryBudget = true)
        {
            if (countRecoveryBudget)
            {
                mission.RecoveryAttempts++;
            }
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
        }

        private async Task CloneDependentChainAsync(
            List<Mission> voyageMissions,
            Mission templateMission,
            Mission newDependency,
            string parsedTitle,
            string parsedDescription,
            CancellationToken token)
        {
            await CloneDependentChainCoreAsync(
                voyageMissions,
                templateMission,
                newDependency,
                parsedTitle,
                parsedDescription,
                new HashSet<string>(StringComparer.Ordinal),
                token).ConfigureAwait(false);
        }

        private async Task CloneDependentChainCoreAsync(
            List<Mission> voyageMissions,
            Mission templateMission,
            Mission newDependency,
            string parsedTitle,
            string parsedDescription,
            HashSet<string> visited,
            CancellationToken token)
        {
            List<Mission> directDependents = voyageMissions
                .Where(m => m.DependsOnMissionId == templateMission.Id)
                .OrderBy(m => m.CreatedUtc)
                .ToList();

            foreach (Mission templateChild in directDependents)
            {
                // A dependent whose persona is the pipeline's first stage (Architect) is the root of
                // another dispatched mission's chain, not a stage of this one. Alias dependencies
                // link a completed chain's terminal stage to the next chain's first stage, and the
                // walk must stop there: following the edge would retitle, clone, or sequence the
                // sibling chains as if they belonged to this plan block.
                if (IsChainBoundaryDependent(templateChild)) continue;
                if (!visited.Add(templateChild.Id)) continue;

                Mission clonedStage = new Mission(
                    parsedTitle + " [" + templateChild.Persona + "]",
                    parsedDescription);
                clonedStage.TenantId = templateChild.TenantId;
                clonedStage.UserId = templateChild.UserId;
                clonedStage.VoyageId = templateChild.VoyageId;
                clonedStage.VesselId = templateChild.VesselId;
                clonedStage.Persona = templateChild.Persona;
                clonedStage.DependsOnMissionId = newDependency.Id;
                // Read-only mode propagates with the chain: cloned stages are the same line of
                // work as their template, so an audit template never spawns implementing stages.
                clonedStage.Mode = templateChild.Mode;
                // Deliberately NOT inherited. StageOrder identifies a parallel stage group, and every
                // cloned chain is an independent line of work that happens to share the template's
                // shape. Copying it would group the clones together and make each chain's downstream
                // wait on every other chain's stage of the same order -- a fan-out-wide deadlock.
                clonedStage.StageOrder = null;
                clonedStage.BranchName = null;
                clonedStage = await _Database.Missions.CreateAsync(clonedStage, token).ConfigureAwait(false);
                _Logging.Info(_Header + "architect created chained stage " + clonedStage.Id +
                    " (" + clonedStage.Persona + ") depending on " + newDependency.Id);
                await CloneDependentChainCoreAsync(
                    voyageMissions,
                    templateChild,
                    clonedStage,
                    parsedTitle,
                    parsedDescription,
                    visited,
                    token).ConfigureAwait(false);
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
                if (IsChainBoundaryDependent(dependent)) continue;

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
                    .Where(m => m.DependsOnMissionId == current.Id && !IsChainBoundaryDependent(m))
                    .OrderBy(m => m.CreatedUtc)
                    .FirstOrDefault();
                if (next == null) break;
                current = next;
            }

            return current;
        }

        private static bool IsChainBoundaryDependent(Mission dependent)
        {
            if (dependent == null) return false;
            return String.Equals(dependent.Persona, "Architect", StringComparison.OrdinalIgnoreCase);
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
            (normalizedDescription, string? yamlDependency) = ExtractArchitectFrontMatter(normalizedDescription);
            (normalizedDescription, string? proseDependency) = ExtractArchitectDependencyReference(normalizedDescription);
            string? dependencyReference = yamlDependency ?? proseDependency;
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

        /// <summary>
        /// Consumes the YAML-style front-matter that Architect blocks open with (`title:`, `preferredModel:`,
        /// `dependsOnMissionId:`, `description: |`) and returns the description body with the front-matter
        /// stripped and the block indent removed. Only known keys at the very top are treated as front-matter,
        /// so a body that happens to start with a `word: value` prose line is left untouched. The declared
        /// `dependsOnMissionId:` dependency is returned so the block's ordering intent reaches the dispatcher.
        /// </summary>
        /// <param name="description">Architect block description, including any front-matter.</param>
        /// <returns>The description body and the declared dependency reference, if any.</returns>
        internal static (string Description, string? DependsOnReference) ExtractArchitectFrontMatter(string? description)
        {
            if (String.IsNullOrWhiteSpace(description)) return ("", null);

            string[] lines = description.Replace("\r\n", "\n").Split('\n');
            string? dependencyReference = null;
            int index = 0;

            while (index < lines.Length)
            {
                string line = lines[index].TrimEnd('\r');
                if (!TryParseArchitectFrontMatterKey(line, out string key, out string value, out bool bodyFollows))
                {
                    break;
                }

                if (String.Equals(key, "dependsOnMissionId", StringComparison.OrdinalIgnoreCase) &&
                    String.IsNullOrWhiteSpace(dependencyReference))
                {
                    dependencyReference = NormalizeArchitectDependencyReference(value);
                }

                index++;
                if (!bodyFollows) continue;

                // `description: |` -- the remainder is the body with the YAML block indent removed.
                List<string> bodyLines = new List<string>();
                while (index < lines.Length)
                {
                    bodyLines.Add(StripArchitectBlockIndent(lines[index]));
                    index++;
                }

                return (String.Join("\n", bodyLines).Trim(), String.IsNullOrWhiteSpace(dependencyReference) ? null : dependencyReference);
            }

            if (index == 0) return (description.Trim(), null);

            // Known front-matter keys were consumed but no `description:` body marker was found; keep
            // whatever follows the front-matter as the body.
            List<string> remainder = new List<string>();
            for (; index < lines.Length; index++) remainder.Add(lines[index].TrimEnd('\r'));
            return (String.Join("\n", remainder).Trim(), String.IsNullOrWhiteSpace(dependencyReference) ? null : dependencyReference);
        }

        private static bool TryParseArchitectFrontMatterKey(string line, out string key, out string value, out bool bodyFollows)
        {
            key = "";
            value = "";
            bodyFollows = false;

            if (String.IsNullOrWhiteSpace(line)) return false;

            int colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) return false;

            string candidate = line.Substring(0, colonIndex).Trim();
            if (!IsArchitectFrontMatterKey(candidate)) return false;

            key = candidate;
            value = line.Substring(colonIndex + 1).Trim();

            // `description: |` opens the indented YAML body.
            if (String.Equals(key, "description", StringComparison.OrdinalIgnoreCase) &&
                (String.IsNullOrWhiteSpace(value) || String.Equals(value.TrimEnd(' ', '-', '|'), "", StringComparison.Ordinal)))
            {
                bodyFollows = true;
            }

            return true;
        }

        private static bool IsArchitectFrontMatterKey(string key)
        {
            if (String.IsNullOrWhiteSpace(key)) return false;

            return String.Equals(key, "title", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "preferredModel", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "dependsOnMissionId", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "description", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripArchitectBlockIndent(string line)
        {
            // The YAML body is indented two spaces relative to its keys; strip that common indent.
            if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                return line.Substring(2).TrimEnd('\r');
            }

            return line.TrimEnd('\r');
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

        internal static Mission? ResolveArchitectDependencyTerminalStage(
            IReadOnlyDictionary<int, Mission> terminalStagesByIndex,
            IReadOnlyDictionary<string, Mission> terminalStagesByTitle,
            int currentMissionIndex,
            string dependencyReference)
        {
            string normalizedReference = NormalizeArchitectDependencyReference(dependencyReference);
            if (String.IsNullOrWhiteSpace(normalizedReference)) return null;

            // Architect blocks declare their ordering as `dependsOnMissionId: M2` (the M-alias of an earlier
            // block). Map that alias to its block index so the numeric and by-title paths below can resolve it.
            System.Text.RegularExpressions.Match aliasMatch =
                System.Text.RegularExpressions.Regex.Match(
                    normalizedReference,
                    @"^[Mm]\d+$",
                    System.Text.RegularExpressions.RegexOptions.None);
            if (aliasMatch.Success)
            {
                normalizedReference = normalizedReference.Substring(1);
            }

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

        private async Task<bool> HasChangesSinceDockStartAsync(Dock? dock, int fallbackDiffLineCount, CancellationToken token)
        {
            if (dock == null || String.IsNullOrWhiteSpace(dock.WorktreePath))
            {
                return fallbackDiffLineCount > 0;
            }

            string? startCommit = TryReadDockStartCommit(dock.Id);
            if (String.IsNullOrWhiteSpace(startCommit))
            {
                _Logging.Warn(_Header + "no dock start commit metadata for " + dock.Id +
                    "; falling back to the captured branch diff for no-op validation");
                return fallbackDiffLineCount > 0;
            }

            try
            {
                IReadOnlyList<string> changedFiles = await _Git.GetChangedFilesSinceAsync(
                    dock.WorktreePath,
                    startCommit,
                    token).ConfigureAwait(false);
                return changedFiles.Count > 0;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not compare mission changes with dock start commit for " +
                    dock.Id + ": " + ex.Message + "; falling back to the captured branch diff");
                return fallbackDiffLineCount > 0;
            }
        }

        private static string NormalizeMissionPath(string path)
        {
            return (path ?? String.Empty).Trim().Replace('\\', '/');
        }

        /// <summary>
        /// Whether the root instruction file is tracked by the repository. A tracked file is repo
        /// content that must not be overwritten; an untracked file at the root was written by an
        /// earlier generation pass and is the canonical mission-brief location for runtimes that
        /// auto-load it. Falls back to false when the path is not a git worktree (unit tests), so
        /// the brief lands at the root and existing tests keep their behavior.
        /// </summary>
        /// <param name="worktreePath">Dock worktree path.</param>
        /// <param name="instructionsFileName">Runtime instruction filename, e.g. "CLAUDE.md".</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the root instruction file is tracked by git.</returns>
        private async Task<bool> IsRootInstructionsTrackedAsync(string worktreePath, string instructionsFileName, CancellationToken token)
        {
            try
            {
                return await _Git.IsPathTrackedAsync(worktreePath, instructionsFileName, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not resolve tracked status of root instructions at " +
                    worktreePath + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Ensure the generated mission instructions exist in the worktree, restoring them from the
        /// log snapshot when the dock lost them. Internal for the unit suite: the double-write
        /// regression (brief duplicated at root and under .armada/instructions/) is only provable by
        /// calling generation and restore back to back.
        /// </summary>
        internal async Task EnsureMissionInstructionsPresentAsync(
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
            bool rootInstructionsTracked = await IsRootInstructionsTrackedAsync(worktreePath, instructionsFileName, token).ConfigureAwait(false);
            // Restore-side rule: a TRACKED root file is repo content and the brief belongs under
            // .armada/instructions/. An untracked root file was either generated by an earlier pass
            // (the brief already lives at the root for an auto-load runtime) or is a local artifact;
            // either way the root is the canonical location. Deciding by tracked status instead of
            // mere existence keeps this path from duplicating a brief that generation wrote at the
            // root, which was the 2026-08-09 probe papercut (AGENTS.md byte-identical at both paths).
            string instructionsRelativePath = rootInstructionsTracked
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

        /// <summary>
        /// Count the captain ids already skipped from in-place re-runs for a mission. Each
        /// missing-verdict re-run appends the failing captain, so the count doubles as the
        /// missing-verdict retry budget. It is deliberately NOT
        /// <see cref="Mission.RecoveryAttempts"/>, which gates the autonomous-rescue pipeline:
        /// an intermittent Judge provider must not consume the recovery budget a genuinely failed
        /// mission still needs.
        /// </summary>
        private static int CountRetrySkipCaptains(string? retrySkipCaptainIds)
        {
            if (String.IsNullOrWhiteSpace(retrySkipCaptainIds)) return 0;
            int count = 0;
            foreach (string part in retrySkipCaptainIds.Split(','))
            {
                if (!String.IsNullOrWhiteSpace(part)) count++;
            }
            return count;
        }

        /// <summary>
        /// Append a captain id to the mission's in-place re-run skip list. Deduplicates, so a
        /// captain that produced empty output more than once is still counted once.
        /// </summary>
        private static void AppendRetrySkipCaptain(Mission mission, string? captainId)
        {
            if (mission == null || String.IsNullOrWhiteSpace(captainId)) return;
            string existing = mission.RetrySkipCaptainIds ?? String.Empty;
            bool alreadyPresent = false;
            foreach (string part in existing.Split(','))
            {
                if (String.Equals(part.Trim(), captainId, StringComparison.Ordinal))
                {
                    alreadyPresent = true;
                    break;
                }
            }
            if (alreadyPresent) return;
            mission.RetrySkipCaptainIds = String.IsNullOrEmpty(existing)
                ? captainId
                : existing + "," + captainId;
        }

        /// <summary>
        /// Whether a captain id sits on a mission's in-place re-run skip list.
        /// </summary>
        private static bool IsCaptainOnRetrySkipList(string? retrySkipCaptainIds, string? captainId)
        {
            if (String.IsNullOrWhiteSpace(captainId) || String.IsNullOrWhiteSpace(retrySkipCaptainIds)) return false;
            foreach (string part in retrySkipCaptainIds.Split(','))
            {
                if (String.Equals(part.Trim(), captainId, StringComparison.Ordinal)) return true;
            }
            return false;
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

            // A judge writes these headers by hand, so they arrive wrapped in whatever markdown the
            // model chose: `## Tests`, `**## Tests**`, `- __Tests__:`. Strip the decoration first and
            // match on the heading TEXT, because a review is rejected for what it fails to say and
            // never for how it was formatted. Emphasis can sit outside the heading markers as well as
            // inside them, which a pattern anchored on a leading `#` run cannot see. This is the same
            // normalization the verdict line already gets in ExtractJudgeNarrative.
            string pattern =
                @"^(?:\d+[\.\)]\s*)?"
                + System.Text.RegularExpressions.Regex.Escape(sectionName)
                + @"(?:$|[\s:\-\u2013\u2014(].*$)";

            foreach (string rawLine in agentOutput.Replace("\r\n", "\n").Split('\n'))
            {
                string normalized = rawLine.Trim().Trim('*', '_', '`', '#', '>', '-', ' ', ':').Trim();
                if (String.IsNullOrEmpty(normalized)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        normalized, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
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
                return CaptainAllowsPersona(captain, missionPersona);
            }

            return true;
        }

        /// <summary>
        /// Whether a captain may take a mission of the supplied persona. An empty allow-list means the
        /// captain accepts any persona.
        /// </summary>
        /// <remarks>
        /// Matching is normalized rather than literal. Persona names have two spellings in persisted
        /// data -- "Test Engineer" is canonical and "TestEngineer" is what older builds wrote -- and a
        /// substring test against the raw JSON treats those as different personas. A captain carrying the
        /// legacy spelling was then eligible for nothing: it stayed Idle while its mission queued for
        /// ever, which reads as a capacity problem and is really a spelling one.
        /// </remarks>
        /// <param name="captain">Captain being considered.</param>
        /// <param name="persona">Persona the mission runs as.</param>
        /// <returns>True when the captain may take the persona.</returns>
        internal static bool CaptainAllowsPersona(Captain captain, string? persona)
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
                        if (PersonaCatalog.Matches(allowedPersona, persona)) return true;
                    }

                    return false;
                }
            }
            catch (JsonException)
            {
                // Fall through to the substring form below: an allow-list that is not a JSON array is
                // malformed, and refusing every persona for it would bench the captain silently.
            }

            string normalized = PersonaCatalog.NormalizeName(persona);
            return captain.AllowedPersonas.Contains("\"" + persona + "\"", StringComparison.OrdinalIgnoreCase)
                || captain.AllowedPersonas.Contains("\"" + normalized + "\"", StringComparison.OrdinalIgnoreCase);
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

            // Prefer external-provider-served captains over native ones: a captain carrying
            // its own provider base URL on a non-OpenCode runtime consumes the alternate
            // (cheaper) subscription, so it wins the tie for an equal model and saves the
            // native provider's usage. OpenCode-runtime captains are treated as native.
            // Native captains remain the fallback when no external captain is idle. Applied
            // to the model-filtered set so both the no-persona shortcut and the persona
            // path honor it.
            {
                List<Captain> external = new List<Captain>();
                List<Captain> native = new List<Captain>();
                foreach (Captain captain in idleCaptains)
                {
                    if (captain.Runtime != AgentRuntimeEnum.OpenCode &&
                        !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                    {
                        external.Add(captain);
                    }
                    else
                    {
                        native.Add(captain);
                    }
                }
                idleCaptains = external;
                idleCaptains.AddRange(native);
            }

            // If no persona requirement, return any idle captain
            if (String.IsNullOrEmpty(persona))
                return idleCaptains[0];

            // Filter by AllowedPersonas (null = any persona is allowed)
            List<Captain> eligible = new List<Captain>();
            foreach (Captain captain in idleCaptains)
            {
                // Normalized match, so a captain whose allow-list carries the legacy spelling of a
                // persona is still eligible for that persona's missions.
                if (CaptainAllowsPersona(captain, persona))
                {
                    eligible.Add(captain);
                }
            }

            if (eligible.Count == 0)
            {
                return null;
            }

            // In-place re-run routing: a mission whose judges produced empty output records the
            // failing captain on RetrySkipCaptainIds. Exclude those captains from re-dispatch so
            // the re-run routes to a different (native fallback) captain instead of re-selecting
            // the same degraded provider. If the exclusion would empty the pool entirely (a
            // single-captain fleet), fall back to the full eligible set so work is never stranded.
            List<Captain> eligibleFiltersSkipped = new List<Captain>();
            foreach (Captain captain in eligible)
            {
                if (!IsCaptainOnRetrySkipList(mission?.RetrySkipCaptainIds, captain.Id))
                {
                    eligibleFiltersSkipped.Add(captain);
                }
            }
            if (eligibleFiltersSkipped.Count > 0)
            {
                eligible = eligibleFiltersSkipped;
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
        /// Run the pre-land dock-boundary scanner for the mission's vessel. On a finding, fail the mission
        /// with a redaction-safe reason (which surfaces it in the operator inbox) and return true so the
        /// caller does not land. Returns false when the vessel has no boundary rules or the dock is clean.
        /// </summary>
        private async Task<bool> TryFailMissionForBoundaryViolationAsync(Mission mission, Dock? dock, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.VesselId)) return false;
            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) return false;

            DockBoundarySettings settings = _Settings?.DockBoundary ?? new DockBoundarySettings();
            DockBoundaryScanResult scanResult = new DockBoundaryScanner().Scan(
                mission.DiffSnapshot,
                null,
                vessel.Id,
                vessel.Name,
                vessel.RepoUrl,
                vessel.ProtectedPaths,
                settings);

            if (scanResult.Passed) return false;

            if (dock != null)
            {
                try { await ReclaimMissionDockAsync(dock.Id, token).ConfigureAwait(false); }
                catch (Exception ex) { _Logging.Warn(_Header + "boundary reclaim error for mission " + mission.Id + ": " + ex.Message); }
            }

            mission.Status = MissionStatusEnum.Failed;
            mission.FailureReason = FormatDockBoundaryFailure(scanResult);
            mission.CaptainId = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + mission.Id + " blocked by dock-boundary scanner (" + scanResult.Findings.Count + " finding(s))");
            await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
            return true;
        }

        private static string FormatDockBoundaryFailure(DockBoundaryScanResult result)
        {
            if (result?.Findings == null || result.Findings.Count == 0)
                return "dock_boundary_violation: no details";
            List<string> parts = new List<string>();
            foreach (DockBoundaryFinding f in result.Findings)
            {
                string loc = !String.IsNullOrEmpty(f.Path) ? f.Path : "(unknown)";
                parts.Add(f.Kind.ToString() + " [" + (f.FindingLabel ?? "") + "] at " + loc);
            }
            return "dock_boundary_violation: " + String.Join("; ", parts);
        }

        private static List<string> ExtractChangedPathsFromDiff(string? diff)
        {
            List<string> paths = new List<string>();
            if (String.IsNullOrEmpty(diff)) return paths;
            foreach (string raw in diff!.Replace("\r\n", "\n").Split('\n'))
            {
                if (!raw.StartsWith("+++ ", StringComparison.Ordinal)) continue;
                string body = raw.Length > 4 ? raw.Substring(4).Trim() : String.Empty;
                if (body == "/dev/null") continue;
                if (body.StartsWith("b/", StringComparison.Ordinal) || body.StartsWith("a/", StringComparison.Ordinal))
                    body = body.Substring(2);
                body = body.Replace('\\', '/').TrimStart('/').Trim();
                if (!String.IsNullOrEmpty(body)) paths.Add(body);
            }
            return paths;
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
            if (String.IsNullOrEmpty(mission.Persona)) return;

            string? resolvedCaptainId = null;
            CaptainTierEnum? resolvedTier = null;

            List<CaptainAssignmentOverride> overrides = await ReadVoyageCaptainOverridesAsync(mission.VoyageId, token).ConfigureAwait(false);
            foreach (CaptainAssignmentOverride ov in overrides)
            {
                if (!PersonaCatalog.Matches(ov.Persona, mission.Persona)) continue;
                resolvedCaptainId = String.IsNullOrEmpty(ov.CaptainId) ? null : ov.CaptainId;
                resolvedTier = ov.FallbackTier;
                break;
            }

            if (String.IsNullOrEmpty(resolvedCaptainId))
            {
                Persona? persona = await ReadPersonaByNameAsync(mission.TenantId, mission.Persona, token).ConfigureAwait(false);
                if (persona != null && !String.IsNullOrEmpty(persona.DefaultCaptainId))
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

        private async Task<Persona?> ReadPersonaByNameAsync(string? tenantId, string personaName, CancellationToken token)
        {
            if (!String.IsNullOrEmpty(tenantId))
            {
                Persona? scoped = await _Database.Personas.ReadByNameAsync(tenantId, personaName, token).ConfigureAwait(false);
                if (scoped != null) return scoped;
            }

            return await _Database.Personas.ReadByNameAsync(personaName, token).ConfigureAwait(false);
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
        /// Serialize a list of captain overrides for voyage persistence. Returns null when the list is
        /// null or empty so the column stays null rather than storing "[]", which would otherwise read
        /// as "overrides were configured" to anyone inspecting the row.
        /// </summary>
        /// <param name="overrides">The overrides to serialize, or null.</param>
        /// <returns>The serialized JSON array, or null when there is nothing to store.</returns>
        public static string? SerializeCaptainOverrides(List<CaptainAssignmentOverride>? overrides)
        {
            if (overrides == null || overrides.Count == 0) return null;
            return JsonSerializer.Serialize(overrides);
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
