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
        /// Event recorded when stall recovery abandons a relaunch because the mission changed under it.
        /// </summary>
        public const string RecoveryAbandonedEvent = "captain.recovery_abandoned";

        #endregion

        #region Private-Members

        private string _Header = "[CaptainService] ";
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
            if (captain == null) throw new InvalidOperationException("Captain not found: " + captainId);

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
                    _Logging.Warn(_Header + "error stopping agent for captain " + captainId + ": " + ex.Message);
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
                    _Logging.Warn(_Header + "error reclaiming dock " + captain.CurrentDockId + " for captain " + captainId + ": " + ex.Message);
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
        public async Task TryRecoverAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

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
                    await FinalizeRecoveryFailureAsync(captain, mission, missingReason, token).ConfigureAwait(false);
                    return;
                }

                Voyage? voyage = !String.IsNullOrEmpty(mission.VoyageId)
                    ? await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false)
                    : null;

                bool voyageCancelled = voyage != null && voyage.Status == VoyageStatusEnum.Cancelled;
                bool missionRecoverable =
                    mission.Status == MissionStatusEnum.Assigned ||
                    mission.Status == MissionStatusEnum.InProgress ||
                    mission.Status == MissionStatusEnum.Testing ||
                    mission.Status == MissionStatusEnum.Review;

                if (voyageCancelled || !missionRecoverable)
                {
                    // A cancelled voyage cancels only work it may still stop; finished and produced
                    // missions keep their status.
                    if (voyageCancelled && MissionStateMachine.IsCancelledWithVoyage(mission.Status))
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
                        _Logging.Warn(_Header + "error reclaiming dock " + dock.Id + " while skipping recovery for captain " + captain.Id + ": " + ex.Message);
                    }

                    await ReleaseAsync(captain, token: token).ConfigureAwait(false);
                    _Logging.Info(_Header + "skipping auto-recovery for captain " + captain.Id +
                        " because mission " + mission.Id + " is " + mission.Status +
                        (voyageCancelled ? " and voyage " + mission.VoyageId + " is Cancelled" : String.Empty));
                    return;
                }

                if (String.IsNullOrEmpty(dock.WorktreePath))
                {
                    string worktreeReason = "Auto-recovery failed because dock " + dock.Id + " has no worktree path.";
                    _Logging.Warn(_Header + "cannot recover captain " + captain.Id + ": dock " + dock.Id + " has no worktree path -- failing mission and releasing to idle");
                    await FinalizeRecoveryFailureAsync(captain, mission, worktreeReason, token).ConfigureAwait(false);
                    return;
                }

                bool worktreeAccessible = false;
                try
                {
                    worktreeAccessible = await _Git.IsRepositoryAsync(dock.WorktreePath, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "worktree accessibility check failed for captain " + captain.Id + ": " + ex.Message);
                }

                // Only attempt destructive repair when the worktree is no longer usable.
                // Normal agent recovery must preserve the mission's uncommitted changes.
                if (!worktreeAccessible)
                {
                    try
                    {
                        await _Git.RepairWorktreeAsync(dock.WorktreePath, token).ConfigureAwait(false);
                        worktreeAccessible = await _Git.IsRepositoryAsync(dock.WorktreePath, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "worktree repair failed for captain " + captain.Id + ": " + ex.Message);
                    }
                }

                if (!worktreeAccessible)
                {
                    string inaccessibleReason = "Auto-recovery failed because dock " + dock.Id + " is not a usable git worktree.";
                    _Logging.Warn(_Header + "cannot recover captain " + captain.Id + ": dock " + dock.Id + " is not a usable git worktree -- failing mission and releasing to idle");
                    await FinalizeRecoveryFailureAsync(captain, mission, inaccessibleReason, token).ConfigureAwait(false);
                    return;
                }

                // Get vessel for context regeneration
                Vessel? vessel = !String.IsNullOrEmpty(mission.VesselId)
                    ? await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false)
                    : null;

                // The relaunch is decided on the mission as read above. It is written back only while the mission
                // still has that status and captain: a writer that moved it meanwhile (a cancel, a failure, a
                // requeue) wins, and the relaunch is abandoned rather than overwriting that decision.
                MissionStatusEnum decidedStatus = mission.Status;
                string? decidedCaptainId = mission.CaptainId;
                string? conflict = await ReadRecoveryConflictAsync(mission.Id, decidedStatus, decidedCaptainId, token).ConfigureAwait(false);
                if (conflict != null)
                {
                    await AbandonRecoveryAsync(captain, mission, null, conflict, token).ConfigureAwait(false);
                    return;
                }

                // Clear the stopped process from the captain and the mission before the relaunch, so the new
                // process id is the only one either records and the stopped process's exit is never read as
                // the new run's.
                mission.ProcessId = null;
                mission.LastUpdateUtc = DateTime.UtcNow;
                if (!await _Database.Missions.TryUpdateIfStatusAsync(mission, decidedStatus, token).ConfigureAwait(false))
                {
                    await AbandonRecoveryAsync(captain, mission, null, "its status changed from " + decidedStatus, token).ConfigureAwait(false);
                    return;
                }

                // Update captain state for re-launch
                captain.State = CaptainStateEnum.Working;
                captain.ProcessId = null;
                captain.LastHeartbeatUtc = DateTime.UtcNow;
                captain.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

                // Re-launch agent process
                if (OnLaunchAgent != null)
                {
                    int processId;
                    try
                    {
                        processId = await OnLaunchAgent.Invoke(captain, mission, dock).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "recovery launch failed for captain " + captain.Id + ": " + ex.Message);
                        string launchReason = "Auto-recovery failed while relaunching the agent: " + ex.Message;
                        await FinalizeRecoveryFailureAsync(captain, mission, launchReason, token).ConfigureAwait(false);
                        return;
                    }

                    Mission? current = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                    conflict = DescribeRecoveryConflict(current, decidedStatus, decidedCaptainId);
                    bool written = false;
                    if (conflict == null && current != null)
                    {
                        current.ProcessId = processId;
                        current.ProcessStartedUtc = ProcessSupervisor.GetRecordedLaunchStartUtc(processId);
                        current.Status = MissionStatusEnum.InProgress;
                        current.LastUpdateUtc = DateTime.UtcNow;
                        written = await _Database.Missions.TryUpdateIfStatusAsync(current, decidedStatus, token).ConfigureAwait(false);
                        if (!written) conflict = "its status changed from " + decidedStatus;
                    }

                    if (!written)
                    {
                        await AbandonRecoveryAsync(captain, current ?? mission, processId, conflict ?? "it could not be re-read", token).ConfigureAwait(false);
                        return;
                    }

                    captain.ProcessId = processId;

                    captain.ProcessStartedUtc = ProcessSupervisor.GetRecordedLaunchStartUtc(processId);
                    await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

                    await MissionAttemptFactRecorder.RecordAsync(_Database, current!, MissionAttemptFactTypeEnum.Retried, "captain_recovery_relaunch", _Logging, token).ConfigureAwait(false);

                    Signal signal = new Signal(SignalTypeEnum.Assignment, "Auto-recovery attempt " + captain.RecoveryAttempts + " for mission: " + current!.Title);
                    signal.ToCaptainId = captain.Id;
                    await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

                    _Logging.Info(_Header + "recovered captain " + captain.Id + " with process " + processId);
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "unhandled error in TryRecoverAsync for captain " + captain.Id + ": " + ex.Message);
                try
                {
                    Mission? mission = !String.IsNullOrEmpty(captain.CurrentMissionId)
                        ? await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false)
                        : null;
                    string unexpectedReason = "Auto-recovery failed unexpectedly: " + ex.Message;
                    await FinalizeRecoveryFailureAsync(captain, mission, unexpectedReason, token).ConfigureAwait(false);
                }
                catch (Exception finalizeEx)
                {
                    _Logging.Warn(_Header + "could not finalize the recovery failure for captain " + captain.Id
                        + "; its captain and mission state may be stale: " + finalizeEx.Message);
                }
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
        /// Re-read the mission and describe why the relaunch decided on it no longer applies, or null when it
        /// still has the status and captain the relaunch was decided on.
        /// </summary>
        private async Task<string?> ReadRecoveryConflictAsync(string missionId, MissionStatusEnum decidedStatus, string? decidedCaptainId, CancellationToken token)
        {
            Mission? current = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            return DescribeRecoveryConflict(current, decidedStatus, decidedCaptainId);
        }

        private static string? DescribeRecoveryConflict(Mission? current, MissionStatusEnum decidedStatus, string? decidedCaptainId)
        {
            if (current == null) return "it no longer exists";
            if (current.Status != decidedStatus) return "its status changed from " + decidedStatus + " to " + current.Status;
            if (!String.Equals(current.CaptainId, decidedCaptainId, StringComparison.Ordinal))
                return "its captain changed from " + (decidedCaptainId ?? "none") + " to " + (current.CaptainId ?? "none");
            return null;
        }

        /// <summary>
        /// Abandon a relaunch whose mission changed under it. The captain is released if it still names the
        /// mission, and a process the relaunch already started is stopped after that, so its exit finds no
        /// captain working the mission. The mission itself is left as the other writer set it.
        /// </summary>
        private async Task AbandonRecoveryAsync(Captain captain, Mission mission, int? startedProcessId, string conflict, CancellationToken token)
        {
            string detail = "stall recovery for captain " + captain.Id + " abandoned: mission " + mission.Id + " " + conflict
                + (startedProcessId.HasValue ? "; stopping relaunched process " + startedProcessId.Value : "; nothing was relaunched");
            _Logging.Warn(_Header + detail);

            Captain? current = await _Database.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            if (current != null && String.Equals(current.CurrentMissionId, mission.Id, StringComparison.Ordinal))
            {
                await ReleaseAsync(current, token).ConfigureAwait(false);
            }

            if (startedProcessId.HasValue && OnStopAgent != null)
            {
                Captain stopTarget = current ?? captain;
                stopTarget.ProcessId = startedProcessId.Value;
                stopTarget.ProcessStartedUtc = ProcessSupervisor.GetRecordedLaunchStartUtc(startedProcessId.Value);
                try
                {
                    await OnStopAgent.Invoke(stopTarget).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not stop relaunched process " + startedProcessId.Value + " for captain " + captain.Id + ": " + ex.Message);
                }
                finally
                {
                    stopTarget.ProcessId = null;
                }
            }

            ArmadaEvent evt = new ArmadaEvent(RecoveryAbandonedEvent, "Captain " + captain.Name + " " + detail);
            evt.EntityType = "captain";
            evt.EntityId = captain.Id;
            evt.CaptainId = captain.Id;
            evt.MissionId = mission.Id;
            evt.VesselId = mission.VesselId;
            evt.VoyageId = mission.VoyageId;
            EventOwnerScopeResult scope = await EventOwnerScope.ApplyAsync(_Database, evt, token).ConfigureAwait(false);
            if (scope.Outcome == EventOwnerScopeOutcomeEnum.LookupFailed)
                _Logging.Warn(_Header + "event " + RecoveryAbandonedEvent + " written without owner scope: " + scope.Detail);
            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Mark the mission as failed and return the captain to Idle when auto-recovery cannot continue.
        /// </summary>
        private async Task FinalizeRecoveryFailureAsync(Captain captain, Mission? mission, string reason, CancellationToken token)
        {
            if (mission != null &&
                mission.Status != MissionStatusEnum.Complete &&
                mission.Status != MissionStatusEnum.Failed &&
                mission.Status != MissionStatusEnum.Cancelled &&
                mission.Status != MissionStatusEnum.LandingFailed &&
                mission.Status != MissionStatusEnum.PullRequestOpen)
            {
                mission.Status = MissionStatusEnum.Failed;
                mission.FailureReason = reason;
                mission.ProcessId = null;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }

            if (!String.IsNullOrEmpty(captain.CurrentDockId))
            {
                try
                {
                    await _Docks.ReclaimAsync(captain.CurrentDockId, captain.TenantId, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "error reclaiming dock " + captain.CurrentDockId + " during recovery failure cleanup: " + ex.Message);
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
