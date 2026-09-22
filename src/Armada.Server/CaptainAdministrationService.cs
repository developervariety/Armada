namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// The one definition of the captain administration rules that REST, MCP, WebSocket and the dashboard share:
    /// emergency stop of every captain and session, whether a captain may be deleted or restarted, the dependent
    /// cleanup that follows a deletion, and an in-place restart that keeps the captain record.
    /// </summary>
    public class CaptainAdministrationService
    {
        #region Public-Members

        /// <summary>
        /// Stops a captain's agent process. Restart calls it for a captain that still records a process.
        /// When null, a restart of a captain that records a process fails and leaves the captain unchanged.
        /// </summary>
        public Func<Captain, Task>? StopProcess { get; set; } = null;

        /// <summary>
        /// Stops one planning session. When null, every active planning session is reported as a failed stop.
        /// </summary>
        public Func<PlanningSession, CancellationToken, Task>? StopPlanningSession { get; set; } = null;

        /// <summary>
        /// Stops one objective refinement session. When null, every active refinement session is reported as a failed stop.
        /// </summary>
        public Func<ObjectiveRefinementSession, CancellationToken, Task>? StopRefinementSession { get; set; } = null;

        #endregion

        #region Private-Members

        private readonly string _Header = "[CaptainAdministrationService] ";
        private readonly DatabaseDriver _Database;
        private readonly Func<string, CancellationToken, Task> _RecallCaptain;
        private readonly LoggingModule? _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="recallCaptain">Recalls one working captain: stops its process, fails its mission and returns it to Idle.</param>
        /// <param name="logging">Optional logging module.</param>
        public CaptainAdministrationService(DatabaseDriver database, Func<string, CancellationToken, Task> recallCaptain, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _RecallCaptain = recallCaptain ?? throw new ArgumentNullException(nameof(recallCaptain));
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Route planning and refinement session stops through the supplied coordinators.
        /// A null coordinator leaves the matching stop unset.
        /// </summary>
        /// <param name="planningSessions">Planning session coordinator.</param>
        /// <param name="refinementSessions">Objective refinement session coordinator.</param>
        public void AttachSessionCoordinators(PlanningSessionCoordinator? planningSessions, ObjectiveRefinementCoordinator? refinementSessions)
        {
            if (planningSessions != null)
                StopPlanningSession = (session, token) => planningSessions.StopAsync(session, token);
            if (refinementSessions != null)
                StopRefinementSession = (session, token) => refinementSessions.StopAsync(session, token);
        }

        /// <summary>
        /// Stop every working captain, active planning session and active objective refinement session.
        /// Each stop is attempted independently; a failure is counted and named, never hidden.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Stopped and failed counts per kind, with one entry per failure.</returns>
        public async Task<CaptainStopAllResult> StopAllAsync(CancellationToken token = default)
        {
            CaptainStopAllResult result = new CaptainStopAllResult();
            _Logging?.Warn(_Header + "emergency stop of all captains and sessions requested");

            List<Captain> working = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);
            foreach (Captain captain in working)
            {
                try
                {
                    await _RecallCaptain(captain.Id, token).ConfigureAwait(false);
                    result.CaptainsStopped++;
                }
                catch (Exception ex)
                {
                    result.CaptainsFailed++;
                    result.Failures.Add(new CaptainStopFailure("Captain", captain.Id, ex.Message));
                }
            }

            List<PlanningSession> planning = (await _Database.PlanningSessions.EnumerateAsync(token).ConfigureAwait(false))
                .Where(IsActive)
                .ToList();
            foreach (PlanningSession session in planning)
            {
                if (StopPlanningSession == null)
                {
                    result.PlanningSessionsFailed++;
                    result.Failures.Add(new CaptainStopFailure("PlanningSession", session.Id, "No planning session coordinator is available to stop this session."));
                    continue;
                }

                try
                {
                    await StopPlanningSession(session, token).ConfigureAwait(false);
                    result.PlanningSessionsStopped++;
                }
                catch (Exception ex)
                {
                    result.PlanningSessionsFailed++;
                    result.Failures.Add(new CaptainStopFailure("PlanningSession", session.Id, ex.Message));
                }
            }

            List<ObjectiveRefinementSession> refinement = (await _Database.ObjectiveRefinementSessions.EnumerateAsync(token).ConfigureAwait(false))
                .Where(IsActive)
                .ToList();
            foreach (ObjectiveRefinementSession session in refinement)
            {
                if (StopRefinementSession == null)
                {
                    result.RefinementSessionsFailed++;
                    result.Failures.Add(new CaptainStopFailure("RefinementSession", session.Id, "No objective refinement coordinator is available to stop this session."));
                    continue;
                }

                try
                {
                    await StopRefinementSession(session, token).ConfigureAwait(false);
                    result.RefinementSessionsStopped++;
                }
                catch (Exception ex)
                {
                    result.RefinementSessionsFailed++;
                    result.Failures.Add(new CaptainStopFailure("RefinementSession", session.Id, ex.Message));
                }
            }

            if (result.Failed > 0)
            {
                _Logging?.Warn(_Header + "emergency stop left " + result.Failed + " captain(s) or session(s) running: "
                    + String.Join("; ", result.Failures.Select(f => f.Kind + " " + f.Id + ": " + f.Message)));
            }

            return result;
        }

        /// <summary>
        /// Read a captain in the caller's scope. A null scope, or an administrator, reads any captain.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="scope">Caller scope, or null for an unscoped operator call.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The captain, or null when it is not visible to the caller.</returns>
        public async Task<Captain?> ReadAsync(string captainId, AuthContext? scope, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(captainId)) return null;
            if (scope == null || scope.IsAdmin)
                return await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (scope.IsTenantAdmin)
                return await _Database.Captains.ReadAsync(scope.TenantId!, captainId, token).ConfigureAwait(false);
            return await _Database.Captains.ReadAsync(scope.TenantId!, scope.UserId!, captainId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Name why a captain may not be deleted or restarted: it is Working, Planning or Refining, or it owns an
        /// Assigned or InProgress mission. Returns null when the captain may be deleted or restarted.
        /// </summary>
        /// <param name="captain">Captain to check.</param>
        /// <param name="operation">Operation named in the refusal, for example "delete" or "restart".</param>
        /// <param name="scope">Caller scope, or null for an unscoped operator call.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The refusal message, or null.</returns>
        public async Task<string?> FindBusyReasonAsync(Captain captain, string operation, AuthContext? scope, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            if (captain.State == CaptainStateEnum.Working || captain.State == CaptainStateEnum.Planning || captain.State == CaptainStateEnum.Refining)
                return "Cannot " + operation + " captain while state is Working, Planning, or Refining. Stop the captain first.";

            List<Mission> missions = scope == null || scope.IsAdmin
                ? await _Database.Missions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false)
                : await _Database.Missions.EnumerateByCaptainAsync(scope.TenantId!, captain.Id, token).ConfigureAwait(false);
            int active = missions.Count(m => m.Status == MissionStatusEnum.Assigned || m.Status == MissionStatusEnum.InProgress);
            if (active > 0)
                return "Cannot " + operation + " captain with " + active + " active mission(s) in Assigned or InProgress status. Cancel or complete them first.";

            return null;
        }

        /// <summary>
        /// Delete one captain when the shared busy rule allows it, then remove the events, planning sessions and
        /// objective refinement sessions that reference it.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="scope">Caller scope, or null for an unscoped operator call.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The deletion outcome.</returns>
        public async Task<CaptainDeletionResult> DeleteAsync(string captainId, AuthContext? scope, CancellationToken token = default)
        {
            Captain? captain = await ReadAsync(captainId, scope, token).ConfigureAwait(false);
            if (captain == null)
                return new CaptainDeletionResult(CaptainAdministrationOutcomeEnum.NotFound, captainId ?? "", "Captain not found");

            string? busy = await FindBusyReasonAsync(captain, "delete", scope, token).ConfigureAwait(false);
            if (busy != null)
                return new CaptainDeletionResult(CaptainAdministrationOutcomeEnum.Busy, captain.Id, busy);

            if (scope == null || scope.IsAdmin)
                await _Database.Captains.DeleteAsync(captain.Id, token).ConfigureAwait(false);
            else if (scope.IsTenantAdmin)
                await _Database.Captains.DeleteAsync(scope.TenantId!, captain.Id, token).ConfigureAwait(false);
            else
                await _Database.Captains.DeleteAsync(scope.TenantId!, scope.UserId!, captain.Id, token).ConfigureAwait(false);

            CaptainDeletionResult result = new CaptainDeletionResult(CaptainAdministrationOutcomeEnum.Completed, captain.Id, "Captain deleted");
            result.DependentsRemoved = await CascadeCleanup.RemoveDependentsForCaptainAsync(_Database, captain.Id, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Delete several captains with the same rule and cleanup as <see cref="DeleteAsync"/>.
        /// </summary>
        /// <param name="captainIds">Captain identifiers.</param>
        /// <param name="scope">Caller scope, or null for an unscoped operator call.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Deleted count and one skipped entry per captain that was not deleted.</returns>
        public async Task<DeleteMultipleResult> DeleteManyAsync(IEnumerable<string> captainIds, AuthContext? scope, CancellationToken token = default)
        {
            if (captainIds == null) throw new ArgumentNullException(nameof(captainIds));

            DeleteMultipleResult result = new DeleteMultipleResult();
            foreach (string id in captainIds)
            {
                if (String.IsNullOrEmpty(id))
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                    continue;
                }

                CaptainDeletionResult deletion = await DeleteAsync(id, scope, token).ConfigureAwait(false);
                if (deletion.Outcome == CaptainAdministrationOutcomeEnum.Completed)
                    result.Deleted++;
                else if (deletion.Outcome == CaptainAdministrationOutcomeEnum.NotFound)
                    result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                else
                    result.Skipped.Add(new DeleteMultipleSkipped(id, deletion.Message));
            }

            result.ResolveStatus();
            return result;
        }

        /// <summary>
        /// Restart a captain in place. The record keeps its identifier, configuration, credentials, endpoint,
        /// playbooks, ownership and any quarantine or bench hold; only the runtime state is reset (any leftover
        /// process is stopped, the mission, dock and process references and the recovery count are cleared, and a
        /// captain without a hold returns to Idle). A refused or failed restart leaves the record unchanged.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="scope">Caller scope, or null for an unscoped operator call.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The restart outcome and the captain as stored afterwards.</returns>
        public async Task<CaptainRestartResult> RestartAsync(string captainId, AuthContext? scope, CancellationToken token = default)
        {
            Captain? captain = await ReadAsync(captainId, scope, token).ConfigureAwait(false);
            if (captain == null)
                return new CaptainRestartResult(CaptainAdministrationOutcomeEnum.NotFound, null, "Captain not found");

            string? busy = await FindBusyReasonAsync(captain, "restart", scope, token).ConfigureAwait(false);
            if (busy != null)
                return new CaptainRestartResult(CaptainAdministrationOutcomeEnum.Busy, captain, busy);

            if (captain.ProcessId.HasValue)
            {
                if (StopProcess == null)
                    return new CaptainRestartResult(CaptainAdministrationOutcomeEnum.Failed, captain,
                        "Captain records process " + captain.ProcessId.Value + " and no process stop is available; the captain is unchanged.");

                try
                {
                    await StopProcess(captain).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new CaptainRestartResult(CaptainAdministrationOutcomeEnum.Failed, captain,
                        "Could not stop process " + captain.ProcessId.Value + ": " + ex.Message + ". The captain is unchanged.");
                }
            }

            bool held = captain.State == CaptainStateEnum.Quarantined || captain.State == CaptainStateEnum.Benched;
            if (!held) captain.State = CaptainStateEnum.Idle;
            captain.CurrentMissionId = null;
            captain.CurrentDockId = null;
            captain.ProcessId = null;
            captain.RecoveryAttempts = 0;
            captain.LastHeartbeatUtc = null;
            captain.LastUpdateUtc = DateTime.UtcNow;
            Captain restarted = await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
            _Logging?.Info(_Header + "restarted captain " + restarted.Id + " in place (state " + restarted.State + ")");
            return new CaptainRestartResult(CaptainAdministrationOutcomeEnum.Completed, restarted, "Captain restarted");
        }

        #endregion

        #region Private-Methods

        private static bool IsActive(PlanningSession session)
        {
            return session.Status == PlanningSessionStatusEnum.Active
                || session.Status == PlanningSessionStatusEnum.Responding
                || session.Status == PlanningSessionStatusEnum.Stopping;
        }

        private static bool IsActive(ObjectiveRefinementSession session)
        {
            return session.Status == ObjectiveRefinementSessionStatusEnum.Active
                || session.Status == ObjectiveRefinementSessionStatusEnum.Responding
                || session.Status == ObjectiveRefinementSessionStatusEnum.Stopping;
        }

        #endregion
    }
}
