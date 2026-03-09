namespace Armada.Core.Services
{
    using SyslogLogging;
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
        public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }

        #endregion

        #region Private-Members

        private string _Header = "[MissionService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IDockService _Docks;
        private ICaptainService _Captains;

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
        public MissionService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IDockService docks,
            ICaptainService captains)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Docks = docks ?? throw new ArgumentNullException(nameof(docks));
            _Captains = captains ?? throw new ArgumentNullException(nameof(captains));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task TryAssignAsync(Mission mission, Vessel vessel, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            // Check for vessel-level lock (broad-scope missions block new assignments)
            List<Mission> activeMissions = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            List<Mission> broadMissions = activeMissions.Where(m =>
                (m.Status == MissionStatusEnum.InProgress || m.Status == MissionStatusEnum.Assigned) &&
                IsBroadScope(m)).ToList();

            if (broadMissions.Count > 0)
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has a broad-scope mission in progress — deferring assignment of " + mission.Id);
                return;
            }

            // Check if this mission is broad-scope and vessel already has active work
            int concurrentCount = activeMissions.Count(m =>
                m.Status == MissionStatusEnum.InProgress ||
                m.Status == MissionStatusEnum.Assigned);

            if (IsBroadScope(mission) && concurrentCount > 0)
            {
                _Logging.Warn(_Header + "broad-scope mission " + mission.Id + " deferred — vessel " + vessel.Id + " has " + concurrentCount + " active mission(s)");
                return;
            }

            // Warn about concurrent missions on same vessel
            if (concurrentCount > 0)
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " already has " + concurrentCount + " active mission(s) — potential for conflicts");
            }

            // Find an idle captain
            List<Captain> idleCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle, token).ConfigureAwait(false);
            if (idleCaptains.Count == 0)
            {
                _Logging.Info(_Header + "no idle captains available for mission " + mission.Id);
                return;
            }

            Captain captain = idleCaptains[0];

            // Generate branch name
            string branchName = Constants.BranchPrefix + captain.Name.ToLowerInvariant() + "/" + mission.Id;
            mission.BranchName = branchName;
            mission.CaptainId = captain.Id;
            mission.Status = MissionStatusEnum.Assigned;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            // Provision dock (worktree)
            Dock? dock = await _Docks.ProvisionAsync(vessel, captain, branchName, token).ConfigureAwait(false);
            if (dock == null)
            {
                // Provisioning failed — revert mission assignment
                mission.Status = MissionStatusEnum.Pending;
                mission.CaptainId = null;
                mission.BranchName = null;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                return;
            }

            // Update captain
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            captain.LastHeartbeatUtc = DateTime.UtcNow;
            captain.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

            // Create assignment signal
            Signal signal = new Signal(SignalTypeEnum.Assignment, mission.Title);
            signal.ToCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            // Generate mission CLAUDE.md into worktree
            await GenerateClaudeMdAsync(dock.WorktreePath!, mission, vessel, token).ConfigureAwait(false);

            // Launch agent process via captain service
            if (_Captains.OnLaunchAgent != null)
            {
                try
                {
                    int processId = await _Captains.OnLaunchAgent.Invoke(captain, mission, dock).ConfigureAwait(false);
                    captain.ProcessId = processId;
                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

                    mission.Status = MissionStatusEnum.InProgress;
                    mission.StartedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                    _Logging.Info(_Header + "launched agent process " + processId + " for captain " + captain.Id);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "failed to launch agent for captain " + captain.Id + ": " + ex.Message);

                    Signal errorSignal = new Signal(SignalTypeEnum.Error, "Failed to launch agent: " + ex.Message);
                    errorSignal.FromCaptainId = captain.Id;
                    await _Database.Signals.CreateAsync(errorSignal, token).ConfigureAwait(false);
                }
            }

            _Logging.Info(_Header + "assigned mission " + mission.Id + " to captain " + captain.Id + " at " + dock.WorktreePath);
        }

        /// <inheritdoc />
        public async Task HandleCompletionAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (String.IsNullOrEmpty(captain.CurrentMissionId)) return;

            Mission? mission = await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
            if (mission == null) return;

            // Mark mission complete
            mission.Status = MissionStatusEnum.Complete;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            _Logging.Info(_Header + "mission " + mission.Id + " completed by captain " + captain.Id);

            // Get dock for push/PR
            Dock? dock = null;
            if (!String.IsNullOrEmpty(captain.CurrentDockId))
            {
                dock = await _Database.Docks.ReadAsync(captain.CurrentDockId, token).ConfigureAwait(false);
            }

            // Invoke OnMissionComplete for push/PR handling
            if (dock != null && OnMissionComplete != null)
            {
                try
                {
                    await OnMissionComplete.Invoke(mission, dock).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error in mission complete handler for " + mission.Id + ": " + ex.Message);
                }
            }

            // Log completion signal
            Signal signal = new Signal(SignalTypeEnum.Completion, "Mission completed: " + mission.Title);
            signal.FromCaptainId = captain.Id;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            // Release the captain
            await _Captains.ReleaseAsync(captain, token).ConfigureAwait(false);

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
        public async Task GenerateClaudeMdAsync(string worktreePath, Mission mission, Vessel vessel, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            string claudeMdPath = Path.Combine(worktreePath, "CLAUDE.md");

            string content =
                "# Mission Instructions\n" +
                "\n" +
                "You are an Armada captain executing a mission. Follow these instructions carefully.\n" +
                "\n" +
                "## Mission\n" +
                "- **Title:** " + mission.Title + "\n" +
                "- **ID:** " + mission.Id + "\n" +
                (mission.VoyageId != null ? "- **Voyage:** " + mission.VoyageId + "\n" : "") +
                "\n" +
                "## Description\n" +
                (mission.Description ?? "No additional description provided.") + "\n" +
                "\n" +
                "## Repository\n" +
                "- **Name:** " + vessel.Name + "\n" +
                "- **Branch:** " + (mission.BranchName ?? "unknown") + "\n" +
                "- **Default Branch:** " + vessel.DefaultBranch + "\n" +
                "\n" +
                "## Rules\n" +
                "- Work only within this worktree directory\n" +
                "- Commit all changes to the current branch\n" +
                "- Commit and push your changes — the Admiral will also push if needed\n" +
                "- If you encounter a blocking issue, commit what you have and exit\n" +
                "- Exit with code 0 on success\n" +
                "\n" +
                "## Progress Signals (Optional)\n" +
                "You can report progress to the Admiral by printing these lines to stdout:\n" +
                "- `[ARMADA:PROGRESS] 50` — report completion percentage (0-100)\n" +
                "- `[ARMADA:STATUS] Testing` — transition mission to Testing status\n" +
                "- `[ARMADA:STATUS] Review` — transition mission to Review status\n" +
                "- `[ARMADA:MESSAGE] your message here` — send a progress message\n";

            // If there's an existing CLAUDE.md, preserve it and prepend our instructions
            if (File.Exists(claudeMdPath))
            {
                string existing = await File.ReadAllTextAsync(claudeMdPath).ConfigureAwait(false);
                content += "\n## Existing Project Instructions\n\n" + existing;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(claudeMdPath)!);
            await File.WriteAllTextAsync(claudeMdPath, content).ConfigureAwait(false);

            _Logging.Info(_Header + "generated mission CLAUDE.md at " + claudeMdPath);
        }

        #endregion
    }
}
