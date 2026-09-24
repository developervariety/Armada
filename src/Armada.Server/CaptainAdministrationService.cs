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
    /// creating and updating a captain (the name rule, runtime-option normalization and model validation), stopping
    /// one captain, emergency stop of every captain and session, whether a captain may be deleted or restarted, the
    /// dependent cleanup that follows a deletion, and an in-place restart that keeps the captain record.
    /// </summary>
    public class CaptainAdministrationService
    {
        #region Public-Members

        /// <summary>
        /// Stops a captain's agent process. Restart calls it for a captain that still records a process, and a
        /// single-captain stop calls it before the recall. When null, a restart of a captain that records a process
        /// fails and leaves the captain unchanged, and a single-captain stop leaves the process stop to the recall.
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

        /// <summary>
        /// Validates a captain's runtime and model before a create, and before an update that changes them. It returns
        /// null when the captain can serve, otherwise the reason. A reason that names a provider credit,
        /// authentication or quota failure cannot be verified now, so the write is kept with a warning; any other
        /// reason refuses the write. When null, no model validation runs.
        /// </summary>
        public Func<Captain, CancellationToken, Task<string?>>? ValidateModel { get; set; } = null;

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
        /// Stop one captain. A Planning captain is stopped through its active planning session and a Refining captain
        /// through its active objective refinement session, so the session ends and releases the captain; a captain
        /// reserved by a session that cannot be resolved is refused with <see cref="CaptainAdministrationOutcomeEnum.Conflict"/>.
        /// Any other captain has its agent process stopped and is recalled, which fails its active mission and
        /// returns it to Idle.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="scope">Caller scope, or null for an unscoped operator call.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stop outcome.</returns>
        public async Task<CaptainStopResult> StopAsync(string captainId, AuthContext? scope, CancellationToken token = default)
        {
            Captain? captain = await ReadAsync(captainId, scope, token).ConfigureAwait(false);
            if (captain == null)
                return new CaptainStopResult(CaptainAdministrationOutcomeEnum.NotFound, captainId ?? "", "Captain not found");

            if (captain.State == CaptainStateEnum.Planning)
            {
                PlanningSession? session = (await _Database.PlanningSessions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false))
                    .Where(IsActive)
                    .OrderByDescending(s => s.LastUpdateUtc)
                    .FirstOrDefault();
                if (session == null || StopPlanningSession == null)
                    return new CaptainStopResult(CaptainAdministrationOutcomeEnum.Conflict, captain.Id,
                        "Captain is currently reserved by a planning session, but Armada could not resolve that session for coordinated stop.");

                await StopPlanningSession(session, token).ConfigureAwait(false);
                _Logging?.Info(_Header + "stopped captain " + captain.Id + " through planning session " + session.Id);
                CaptainStopResult planningStopped = new CaptainStopResult(CaptainAdministrationOutcomeEnum.Completed, captain.Id, "Planning session stopped");
                planningStopped.PlanningSessionId = session.Id;
                return planningStopped;
            }

            if (captain.State == CaptainStateEnum.Refining)
            {
                ObjectiveRefinementSession? session = (await _Database.ObjectiveRefinementSessions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false))
                    .Where(IsActive)
                    .OrderByDescending(s => s.LastUpdateUtc)
                    .FirstOrDefault();
                if (session == null || StopRefinementSession == null)
                    return new CaptainStopResult(CaptainAdministrationOutcomeEnum.Conflict, captain.Id,
                        "Captain is currently reserved by an objective refinement session, but Armada could not resolve that session for coordinated stop.");

                await StopRefinementSession(session, token).ConfigureAwait(false);
                _Logging?.Info(_Header + "stopped captain " + captain.Id + " through objective refinement session " + session.Id);
                CaptainStopResult refinementStopped = new CaptainStopResult(CaptainAdministrationOutcomeEnum.Completed, captain.Id, "Objective refinement session stopped");
                refinementStopped.ObjectiveRefinementSessionId = session.Id;
                return refinementStopped;
            }

            if (captain.ProcessId.HasValue && StopProcess != null)
            {
                try
                {
                    await StopProcess(captain).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging?.Warn(_Header + "could not stop process " + captain.ProcessId.Value + " of captain " + captain.Id + ": " + ex.Message);
                    return new CaptainStopResult(CaptainAdministrationOutcomeEnum.Failed, captain.Id,
                        "Could not stop process " + captain.ProcessId.Value + ": " + ex.Message + ". The captain was not recalled.");
                }
            }

            await _RecallCaptain(captain.Id, token).ConfigureAwait(false);
            return new CaptainStopResult(CaptainAdministrationOutcomeEnum.Completed, captain.Id, "Captain stopped");
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

            List<PlanningSession> planning = await ReadActiveSessionsAsync(
                result,
                CaptainStopAllResult.PlanningSessionsSource,
                "PlanningSession",
                () => _Database.PlanningSessions.EnumerateAsync(token),
                s => IsActive(s),
                failed => result.PlanningSessionsFailed += failed).ConfigureAwait(false);
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

            List<ObjectiveRefinementSession> refinement = await ReadActiveSessionsAsync(
                result,
                CaptainStopAllResult.RefinementSessionsSource,
                "RefinementSession",
                () => _Database.ObjectiveRefinementSessions.EnumerateAsync(token),
                s => IsActive(s),
                failed => result.RefinementSessionsFailed += failed).ConfigureAwait(false);
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
            CascadeCleanupResult cleanup = await CascadeCleanup.RemoveDependentsForCaptainAsync(_Database, captain.Id, token, _Logging).ConfigureAwait(false);
            result.DependentsRemoved = cleanup.Removed;
            result.DependentsSkipped = cleanup.Skips;
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


        /// <summary>
        /// Create a captain from its configuration fields. A name another captain already has is refused; the
        /// server-owned fields are reset; the owner is the one supplied; runtime options are normalized when
        /// requested; and the runtime and model are validated. Nothing is stored when the create is refused.
        /// </summary>
        /// <param name="configuration">Configuration fields, with server-owned fields already checked by the surface.</param>
        /// <param name="tenantId">Owning tenant.</param>
        /// <param name="userId">Owning user.</param>
        /// <param name="normalizeRuntimeOptions">Normalize runtime options by <see cref="NormalizeRuntimeOptions"/>; a
        /// surface that builds the options itself from typed arguments passes false.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored captain, or the refusal.</returns>
        public async Task<CaptainWriteResult> CreateAsync(
            Captain configuration,
            string? tenantId,
            string? userId,
            bool normalizeRuntimeOptions = true,
            CancellationToken token = default)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            string? nameConflict = await CaptainNameRule.FindCreateConflictAsync(_Database.Captains, configuration.Name, token).ConfigureAwait(false);
            if (nameConflict != null)
                return CaptainWriteResult.Refused(CaptainAdministrationOutcomeEnum.Busy, CaptainWriteResult.NameConflictCode, nameConflict);

            Captain captain = CaptainInputMapping.ForCreate(configuration);
            captain.TenantId = tenantId;
            captain.UserId = userId;
            if (normalizeRuntimeOptions) NormalizeRuntimeOptions(captain, null);

            ModelCheck check = await CheckModelAsync(captain, null, token).ConfigureAwait(false);
            if (check.Error != null)
                return CaptainWriteResult.Refused(CaptainAdministrationOutcomeEnum.Failed, CaptainWriteResult.InvalidModelCode, check.Error);

            captain = await _Database.Captains.CreateAsync(captain, token).ConfigureAwait(false);
            return CaptainWriteResult.Written(captain, check.CannotVerifyReason);
        }

        /// <summary>
        /// Update a captain's configuration fields. Server-owned fields keep their stored values; runtime options are
        /// normalized when requested; and when the runtime, model, model endpoint or credentials change, the result is
        /// validated. Nothing is stored when the update is refused.
        /// </summary>
        /// <param name="existing">Stored captain, already read under the caller's scope.</param>
        /// <param name="configuration">The configuration the captain should have.</param>
        /// <param name="normalizeRuntimeOptions">Normalize runtime options by <see cref="NormalizeRuntimeOptions"/>; a
        /// surface that builds the options itself from typed arguments passes false.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored captain, or the refusal.</returns>
        public async Task<CaptainWriteResult> UpdateAsync(
            Captain existing,
            Captain configuration,
            bool normalizeRuntimeOptions = true,
            CancellationToken token = default)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            Captain updated = CaptainInputMapping.ForUpdate(existing, configuration);
            if (normalizeRuntimeOptions) NormalizeRuntimeOptions(updated, existing);

            ModelCheck check = await CheckModelAsync(updated, existing, token).ConfigureAwait(false);
            if (check.Error != null)
                return CaptainWriteResult.Refused(CaptainAdministrationOutcomeEnum.Failed, CaptainWriteResult.InvalidModelCode, check.Error);

            updated = await _Database.Captains.UpdateAsync(updated, token).ConfigureAwait(false);
            return CaptainWriteResult.Written(updated, check.CannotVerifyReason);
        }

        /// <summary>
        /// The runtime-option rule for a captain written from a whole body: only a Mux captain keeps runtime options,
        /// and a Mux update that sends none keeps the options the captain already had.
        /// </summary>
        /// <param name="captain">Captain being written.</param>
        /// <param name="existing">Stored captain on an update; null on a create.</param>
        public static void NormalizeRuntimeOptions(Captain captain, Captain? existing)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            if (captain.Runtime != AgentRuntimeEnum.Mux)
            {
                captain.RuntimeOptionsJson = null;
                return;
            }

            if (String.IsNullOrWhiteSpace(captain.RuntimeOptionsJson)
                && existing != null
                && existing.Runtime == AgentRuntimeEnum.Mux
                && !String.IsNullOrWhiteSpace(existing.RuntimeOptionsJson))
            {
                captain.RuntimeOptionsJson = existing.RuntimeOptionsJson;
                return;
            }

            if (String.IsNullOrWhiteSpace(captain.RuntimeOptionsJson))
                captain.RuntimeOptionsJson = null;
        }

        /// <summary>
        /// Whether an update changes what model validation checks: the runtime, the model, the model endpoint, or the
        /// captain's own credentials.
        /// </summary>
        /// <param name="updated">Captain as it will be stored.</param>
        /// <param name="existing">Captain as stored.</param>
        /// <returns>True when the update must be validated.</returns>
        public static bool ChangesValidatedFields(Captain updated, Captain existing)
        {
            if (updated == null) throw new ArgumentNullException(nameof(updated));
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            return !String.Equals(updated.Model, existing.Model, StringComparison.OrdinalIgnoreCase)
                || updated.Runtime != existing.Runtime
                || !String.Equals(updated.ModelEndpointId, existing.ModelEndpointId, StringComparison.Ordinal)
                || !String.Equals(updated.ApiKey, existing.ApiKey, StringComparison.Ordinal)
                || !String.Equals(updated.ApiBaseUrl, existing.ApiBaseUrl, StringComparison.Ordinal);
        }
        #endregion

        #region Private-Methods

        private async Task<ModelCheck> CheckModelAsync(Captain captain, Captain? existing, CancellationToken token)
        {
            ModelCheck check = new ModelCheck();
            if (ValidateModel == null) return check;
            if (existing != null && !ChangesValidatedFields(captain, existing)) return check;

            string? error = await ValidateModel(captain, token).ConfigureAwait(false);
            if (error == null) return check;
            bool cannotVerifyNow = ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(error) || ProviderQuotaLimitDetector.IsQuotaLimitSignal(error);
            if (cannotVerifyNow)
            {
                _Logging?.Warn(_Header + "model validation cannot be verified for captain " + (existing?.Id ?? captain.Name) + "; the write is kept. Error: " + error);
                check.CannotVerifyReason = error;
                return check;
            }
            check.Error = error;
            return check;
        }

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

        /// <summary>
        /// Read the active sessions of one kind for an emergency stop. A provider that does not store
        /// that kind has none running: the source is named in the result and the stop continues. Any
        /// other read error means sessions of that kind may still be running, so it is counted and
        /// named as a failed stop, never skipped.
        /// </summary>
        private async Task<List<T>> ReadActiveSessionsAsync<T>(
            CaptainStopAllResult result,
            string source,
            string kind,
            Func<Task<List<T>>> read,
            Func<T, bool> isActive,
            Action<int> countFailed)
        {
            try
            {
                return (await read().ConfigureAwait(false)).Where(isActive).ToList();
            }
            catch (NotSupportedException ex)
            {
                result.UnavailableSources.Add(source);
                _Logging?.Info(_Header + "emergency stop skipped " + source + ": the database provider does not store them (" + ex.Message + ")");
                return new List<T>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                countFailed(1);
                result.Failures.Add(new CaptainStopFailure(kind, "*", "Could not read active sessions: " + ex.Message));
                return new List<T>();
            }
        }

        #endregion

        #region Private-Types

        private sealed class ModelCheck
        {
            public string? Error { get; set; }
            public string? CannotVerifyReason { get; set; }
        }

        #endregion
    }
}
