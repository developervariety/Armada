namespace Armada.Core.Services
{
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for captain lifecycle management.
    /// </summary>
    public class CaptainService : ICaptainService
    {
        #region Public-Members

        /// <inheritdoc />
        public Func<Captain, Task>? OnStopAgent { get; set; }

        /// <inheritdoc />
        public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }

        /// <summary>
        /// Where docks live. Recovery checks a Harbor-hosted dock on its Harbor through this; without it every dock is
        /// checked on this machine.
        /// </summary>
        public DockHostResolver? DockHosts { get; set; }

        #endregion

        #region Private-Members

        private string _Header = "[CaptainService] ";
        // Captains whose recovery relaunch was refused while their dock's Harbor was offline, keyed to that Harbor.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _AwaitingHarbor = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;
        private IDockService _Docks;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git service.</param>
        /// <param name="docks">Dock service.</param>
        public CaptainService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            IDockService docks)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Docks = docks ?? throw new ArgumentNullException(nameof(docks));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task RecallAsync(string captainId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));

            Captain? captain = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Captains.ReadAsync(tenantId, captainId, token).ConfigureAwait(false)
                : await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null) throw new KeyNotFoundException("Captain not found: " + captainId);

            _Logging.Info(_Header + "recalling captain " + captainId);

            // Stop agent process
            if (OnStopAgent != null && captain.ProcessId.HasValue)
            {
                try
                {
                    await OnStopAgent.Invoke(captain).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error stopping agent for captain " + captainId + ": " + ex.ToString());
                    const string reason = "Recall deferred: the previous job has not confirmed its exit. Captain, mission and dock ownership were retained. Remediation: reconnect the owning Harbor and confirm the job has stopped before retrying.";
                    if (!String.IsNullOrEmpty(captain.CurrentMissionId))
                    {
                        Mission? held = await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                        if (held != null)
                        {
                            held.FailureReason = reason;
                            held.LastUpdateUtc = DateTime.UtcNow;
                            await _Database.Missions.UpdateAsync(held, token).ConfigureAwait(false);
                        }
                    }
                    throw new InvalidOperationException(reason, ex);
                }
            }

            // Update state
            await _Database.Captains.UpdateStateAsync(captainId, CaptainStateEnum.Stopping, token).ConfigureAwait(false);

            // Mark the current mission as failed
            if (!String.IsNullOrEmpty(captain.CurrentMissionId))
            {
                Mission? currentMission = !String.IsNullOrEmpty(tenantId)
                    ? await _Database.Missions.ReadAsync(tenantId, captain.CurrentMissionId, token).ConfigureAwait(false)
                    : await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                if (currentMission != null &&
                    (currentMission.Status == MissionStatusEnum.InProgress ||
                     currentMission.Status == MissionStatusEnum.Assigned))
                {
                    currentMission.Status = MissionStatusEnum.Failed;
                    currentMission.FailureKind = MissionFailureKindEnum.OperatorAction;
                    currentMission.ProcessId = null;
                    currentMission.CompletedUtc = DateTime.UtcNow;
                    currentMission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(currentMission, token).ConfigureAwait(false);
                }
            }

            // Reclaim the dock worktree
            if (!String.IsNullOrEmpty(captain.CurrentDockId))
            {
                try
                {
                    await _Docks.ReclaimAsync(captain.CurrentDockId, tenantId, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error reclaiming dock " + captain.CurrentDockId + " for captain " + captainId + ": " + ex.ToString());
                }
            }

            // Clear captain assignment
            captain.State = CaptainStateEnum.Idle;
            captain.CurrentMissionId = null;
            captain.CurrentDockId = null;
            captain.ProcessId = null;
            captain.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> RetryRelaunchesAwaitingHarborAsync(CancellationToken token = default)
        {
            if (_AwaitingHarbor.IsEmpty || DockHosts?.Harbors == null) return 0;
            int relaunched = 0;
            foreach (KeyValuePair<string, string> waiting in _AwaitingHarbor.ToArray())
            {
                if (!DockHosts.Harbors.IsConnected(waiting.Value)) continue;
                if (!_AwaitingHarbor.TryRemove(waiting.Key, out _)) continue;
                Captain? captain = await _Database.Captains.ReadAsync(waiting.Key, token).ConfigureAwait(false);
                // Only a captain still holding its mission with no process is waiting; anything else has moved on.
                if (captain == null || captain.State != CaptainStateEnum.Working || captain.ProcessId.HasValue || String.IsNullOrEmpty(captain.CurrentMissionId)) continue;
                if (captain.RecoveryAttempts >= _Settings.MaxRecoveryAttempts) continue;
                _Logging.Info(_Header + "Harbor " + waiting.Value + " is back; relaunching captain " + captain.Id + " now rather than at the next stall check");
                await TryRecoverAsync(captain, token).ConfigureAwait(false);
                relaunched++;
            }
            return relaunched;
        }

        /// <inheritdoc />
        public async Task TryRecoverAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            _AwaitingHarbor.TryRemove(captain.Id, out _);

            try
            {
                captain.RecoveryAttempts++;
                _Logging.Info(_Header + "auto-recovery attempt " + captain.RecoveryAttempts + "/" + _Settings.MaxRecoveryAttempts + " for captain " + captain.Id);

                // Reload mission and dock info
                Mission? mission = !String.IsNullOrEmpty(captain.CurrentMissionId)
                    ? await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false)
                    : null;

                Dock? dock = !String.IsNullOrEmpty(captain.CurrentDockId)
                    ? await _Database.Docks.ReadAsync(captain.CurrentDockId, token).ConfigureAwait(false)
                    : null;

                if (mission == null || dock == null)
                {
                    string missingReason = "Auto-recovery failed because the mission or dock could not be reloaded.";
                    _Logging.Warn(_Header + "cannot recover captain " + captain.Id + ": mission or dock not found -- failing mission and releasing to idle");
                    await FinalizeRecoveryFailureAsync(captain, mission, missingReason, MissionFailureKindEnum.Infra, token).ConfigureAwait(false);
                    return;
                }

                Voyage? voyage = !String.IsNullOrEmpty(mission.VoyageId)
                    ? await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false)
                    : null;

                if (mission.Status == MissionStatusEnum.Verifying) return; // A queued/running check owns this captain.

                bool voyageCancelled = voyage != null && voyage.Status == VoyageStatusEnum.Cancelled;
                bool missionRecoverable =
                    mission.Status == MissionStatusEnum.Assigned ||
                    mission.Status == MissionStatusEnum.InProgress ||
                    mission.Status == MissionStatusEnum.Testing ||
                    mission.Status == MissionStatusEnum.Review;

                if (voyageCancelled || !missionRecoverable)
                {
                    if (voyageCancelled && mission.Status != MissionStatusEnum.Cancelled)
                    {
                        mission.Status = MissionStatusEnum.Cancelled;
                        mission.CompletedUtc = DateTime.UtcNow;
                    }

                    mission.ProcessId = null;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                    try
                    {
                        await _Docks.ReclaimAsync(dock.Id, token: token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "error reclaiming dock " + dock.Id + " while skipping recovery for captain " + captain.Id + ": " + ex.ToString());
                    }

                    await ReleaseAsync(captain, token: token).ConfigureAwait(false);
                    _Logging.Debug(_Header + "skipping auto-recovery for captain " + captain.Id +
                        " because mission " + mission.Id + " is " + mission.Status +
                        (voyageCancelled ? " and voyage " + mission.VoyageId + " is Cancelled" : String.Empty));
                    return;
                }

                if (String.IsNullOrEmpty(dock.WorktreePath))
                {
                    string worktreeReason = "Auto-recovery failed because dock " + dock.Id + " has no worktree path.";
                    _Logging.Warn(_Header + "cannot recover captain " + captain.Id + ": dock " + dock.Id + " has no worktree path -- failing mission and releasing to idle");
                    await FinalizeRecoveryFailureAsync(captain, mission, worktreeReason, MissionFailureKindEnum.Infra, token).ConfigureAwait(false);
                    return;
                }

                // A Harbor-hosted dock is checked (and repaired) on its Harbor. While that Harbor is offline the dock
                // cannot be checked; the relaunch below decides (it waits for the Harbor rather than moving the dock).
                bool harborDock = DockHosts != null && DockHostResolver.IsHarborDock(dock);
                bool harborOffline = harborDock && (DockHosts!.Harbors == null || !DockHosts.Harbors.IsConnected(dock.HarborId!));
                IGitService dockGit = harborDock && !harborOffline ? DockHosts!.ForDock(dock).Git : _Git;
                bool worktreeAccessible = harborOffline;
                try
                {
                    if (!harborOffline) worktreeAccessible = await dockGit.IsRepositoryAsync(dock.WorktreePath, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "worktree accessibility check failed for captain " + captain.Id + ": " + ex.ToString());
                }

                // Only attempt destructive repair when the worktree is no longer usable.
                // Normal agent recovery must preserve the mission's uncommitted changes.
                if (!worktreeAccessible)
                {
                    try
                    {
                        await dockGit.RepairWorktreeAsync(dock.WorktreePath, token).ConfigureAwait(false);
                        worktreeAccessible = await dockGit.IsRepositoryAsync(dock.WorktreePath, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "worktree repair failed for captain " + captain.Id + ": " + ex.ToString());
                    }
                }

                if (!worktreeAccessible)
                {
                    string inaccessibleReason = "Auto-recovery failed because dock " + dock.Id + " is not a usable git worktree.";
                    _Logging.Warn(_Header + "cannot recover captain " + captain.Id + ": dock " + dock.Id + " is not a usable git worktree -- failing mission and releasing to idle");
                    await FinalizeRecoveryFailureAsync(captain, mission, inaccessibleReason, MissionFailureKindEnum.Infra, token).ConfigureAwait(false);
                    return;
                }

                // Get vessel for context regeneration
                Vessel? vessel = !String.IsNullOrEmpty(mission.VesselId)
                    ? await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false)
                    : null;

                // Update captain state for re-launch
                captain.State = CaptainStateEnum.Working;
                captain.LastHeartbeatUtc = DateTime.UtcNow;
                captain.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

                // Count every attempted recovery launch durably, including retries after a Harbor reconnect.
                // Reattaching an existing Harbor process never enters this path and must not increment it.
                if (OnLaunchAgent != null)
                {
                    mission.RedispatchAttempts++;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    try
                    {
                        int processId = await OnLaunchAgent.Invoke(captain, mission, dock).ConfigureAwait(false);

                        // A fast agent can exit (and its handler move the mission on and release the captain) before
                        // this returns: record the relaunch only while the captain still holds the mission and the
                        // mission is still in the state it was relaunched from.
                        if (await _Database.Captains.TrySetProcessAsync(captain.Id, mission.Id, processId, token).ConfigureAwait(false))
                            captain.ProcessId = processId;

                        MissionStatusEnum relaunchedFrom = mission.Status;
                        mission.ProcessId = processId;
                        mission.Status = MissionStatusEnum.InProgress;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        if (!await _Database.Missions.TryUpdateIfStatusAsync(mission, new MissionStatusEnum[] { relaunchedFrom, MissionStatusEnum.Assigned, MissionStatusEnum.InProgress }, token).ConfigureAwait(false))
                            _Logging.Info(_Header + "mission " + mission.Id + " moved on before relaunch process " + processId + " was recorded; keeping its newer state");

                        Signal signal = new Signal(SignalTypeEnum.Assignment, "Auto-recovery attempt " + captain.RecoveryAttempts + " for mission: " + mission.Title);
                        signal.ToCaptainId = captain.Id;
                        await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

                        _Logging.Info(_Header + "recovered captain " + captain.Id + " with process " + processId);
                    }
                    catch (HarborLaunchUnavailableException refused) when (captain.RecoveryAttempts < _Settings.MaxRecoveryAttempts)
                    {
                        // requireHarborForLaunch refused the relaunch because the user's Harbor is unavailable. Spend
                        // this attempt and wait: the captain stays Working with no process, and the next stall check
                        // retries recovery, so a Harbor that reconnects within the remaining attempts gets the mission back.
                        _Logging.Warn(_Header + "recovery relaunch for captain " + captain.Id + " refused (attempt " + captain.RecoveryAttempts + "/" + _Settings.MaxRecoveryAttempts + "); retrying when the Harbor reconnects, or at the next stall check: " + refused.Message);
                        if (!String.IsNullOrEmpty(dock.HarborId)) _AwaitingHarbor[captain.Id] = dock.HarborId!;
                        captain.ProcessId = null;
                        captain.LastHeartbeatUtc = DateTime.UtcNow;
                        captain.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

                        mission.ProcessId = null;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "recovery launch failed for captain " + captain.Id + ": " + ex.ToString());
                        string launchReason = "Auto-recovery failed while relaunching the agent: " + ex.Message;
                        MissionFailureKindEnum launchKind = ex is HarborDockNotFoundException
                            ? MissionFailureKindEnum.Infra
                            : MissionFailureKindEnum.StallRecoveryExhausted;
                        await FinalizeRecoveryFailureAsync(captain, mission, launchReason, launchKind, token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "unhandled error in TryRecoverAsync for captain " + captain.Id + ": " + ex.ToString());
                try
                {
                    Mission? mission = !String.IsNullOrEmpty(captain.CurrentMissionId)
                        ? await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false)
                        : null;
                    string unexpectedReason = "Auto-recovery failed unexpectedly: " + ex.Message;
                    await FinalizeRecoveryFailureAsync(captain, mission, unexpectedReason, MissionFailureKindEnum.StallRecoveryExhausted, token).ConfigureAwait(false);
                }
                catch { }
            }
        }

        /// <inheritdoc />
        public async Task ReleaseAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            captain.State = CaptainStateEnum.Idle;
            captain.CurrentMissionId = null;
            captain.CurrentDockId = null;
            captain.ProcessId = null;
            captain.RecoveryAttempts = 0;
            captain.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

            _Logging.Info(_Header + "released captain " + captain.Id);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Mark the mission as failed and return the captain to Idle when auto-recovery cannot continue.
        /// </summary>
        private async Task FinalizeRecoveryFailureAsync(Captain captain, Mission? mission, string reason, MissionFailureKindEnum failureKind, CancellationToken token)
        {
            if (mission != null &&
                !MissionStateMachine.IsTerminalOrPostWork(mission.Status))
            {
                mission.Status = MissionStatusEnum.Failed;
                mission.FailureReason = reason;
                mission.FailureKind = failureKind;
                mission.ProcessId = null;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }

            // Failed stall recovery must retain edits, including when the replacement launch throws.
            if (failureKind != MissionFailureKindEnum.StallRecoveryExhausted && !String.IsNullOrEmpty(captain.CurrentDockId))
            {
                try
                {
                    await _Docks.ReclaimAsync(captain.CurrentDockId, captain.TenantId, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error reclaiming dock " + captain.CurrentDockId + " during recovery failure cleanup: " + ex.ToString());
                }
            }

            captain.CurrentMissionId = null;
            captain.CurrentDockId = null;
            captain.ProcessId = null;
            captain.State = CaptainStateEnum.Idle;
            captain.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

            if (mission != null)
            {
                Signal signal = new Signal(SignalTypeEnum.Error, "Mission " + mission.Id + " failed during auto-recovery: " + reason);
                signal.FromCaptainId = captain.Id;
                await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);
            }
        }

        #endregion
    }
}
