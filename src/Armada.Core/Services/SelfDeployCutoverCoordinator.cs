namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Supervisor side of a self-deploy cutover, and recovery of an interrupted one.
    /// Invariants: a process is launched only after every recorded admiral process is confirmed exited by
    /// verified identity; a launch is recorded before and after it happens; an artifact is re-verified before
    /// it is launched; the previous binary is never started against a schema the candidate advanced; and
    /// recovery never promotes a candidate that was not already running.
    /// </summary>
    public sealed class SelfDeployCutoverCoordinator
    {
        private static readonly SelfDeployRestartStateEnum[] NonTerminalStates =
        {
            SelfDeployRestartStateEnum.Prepared,
            SelfDeployRestartStateEnum.Armed,
            SelfDeployRestartStateEnum.ExitRequested,
            SelfDeployRestartStateEnum.OldStopped,
            SelfDeployRestartStateEnum.CandidateStarting,
            SelfDeployRestartStateEnum.RollingBack,
            SelfDeployRestartStateEnum.RollbackStarting
        };

        private readonly ISelfDeployProcessHost _Host;
        private readonly ISelfDeployHealthProbe _Probe;
        private readonly ISelfDeployArtifactStore _Artifacts;
        private readonly ISelfDeploySchemaVersionReader _Schema;
        private readonly ISelfDeployLaunchPlanner _Planner;
        private readonly SelfDeployRestartRecordStore _Records;
        private readonly SelfDeployCutoverOptions _Options;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="host">Verified process host.</param>
        /// <param name="probe">Health probe.</param>
        /// <param name="artifacts">Artifact store.</param>
        /// <param name="schema">Schema version reader.</param>
        /// <param name="planner">Launch planner.</param>
        /// <param name="records">Restart record store.</param>
        /// <param name="options">Cutover bounds.</param>
        public SelfDeployCutoverCoordinator(
            ISelfDeployProcessHost host,
            ISelfDeployHealthProbe probe,
            ISelfDeployArtifactStore artifacts,
            ISelfDeploySchemaVersionReader schema,
            ISelfDeployLaunchPlanner planner,
            SelfDeployRestartRecordStore records,
            SelfDeployCutoverOptions options)
        {
            _Host = host ?? throw new ArgumentNullException(nameof(host));
            _Probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _Artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
            _Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            _Planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _Records = records ?? throw new ArgumentNullException(nameof(records));
            _Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Run the supervisor for a prepared operation until a terminal state or an interruption.
        /// </summary>
        /// <param name="operationId">Operation id passed by the running admiral.</param>
        /// <param name="supervisor">Identity of this supervisor process.</param>
        /// <param name="token">Cancellation token. Cancellation leaves the record at its last durable state.</param>
        /// <returns>Outcome.</returns>
        public async Task<SelfDeployCutoverResult> SuperviseAsync(
            string operationId,
            SelfDeployProcessIdentity supervisor,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(operationId)) throw new ArgumentNullException(nameof(operationId));
            if (supervisor == null) throw new ArgumentNullException(nameof(supervisor));
            IDisposable? supervisorLock = _Records.TryAcquireSupervisorLock();
            if (supervisorLock == null) return Outcome(null, "supervisor_lock_held");
            using (supervisorLock)
            {
                SelfDeployRestartRecordReadResult read = await _Records.ReadAsync(token).ConfigureAwait(false);
                if (!read.IsReadable) return Outcome(null, read.Exists ? read.FailureReason : "restart_record_missing");
                SelfDeployRestartRecord record = read.Record!;
                if (!String.Equals(record.OperationId, operationId, StringComparison.Ordinal))
                    return Outcome(record.State, "operation_mismatch");
                if (record.State != SelfDeployRestartStateEnum.Prepared)
                    return Outcome(record.State, "unexpected_state_" + record.State);

                string? rollbackFailure = await _Artifacts.VerifyAsync(record.Rollback, token).ConfigureAwait(false);
                string? candidateFailure = await _Artifacts.VerifyAsync(record.Candidate, token).ConfigureAwait(false);
                string? armFailure = rollbackFailure != null
                    ? "rollback_" + rollbackFailure
                    : candidateFailure != null ? "candidate_" + candidateFailure : null;
                if (armFailure == null && (record.OldProcess == null
                    || _Host.GetState(record.OldProcess) != SelfDeployProcessStateEnum.Running))
                    armFailure = "old_process_not_verified_running";
                if (armFailure != null)
                    return await FinishAsync(operationId, new[] { SelfDeployRestartStateEnum.Prepared },
                        SelfDeployRestartStateEnum.Aborted, armFailure, token).ConfigureAwait(false);

                SelfDeployRestartTransitionResult armed = await _Records.TryTransitionAsync(operationId,
                    new[] { SelfDeployRestartStateEnum.Prepared },
                    r =>
                    {
                        r.SupervisorProcess = supervisor;
                        r.MoveTo(SelfDeployRestartStateEnum.Armed, "supervisor_armed");
                    }, token).ConfigureAwait(false);
                if (!armed.Applied) return Outcome(armed.Record?.State, armed.FailureReason);

                SelfDeployRestartRecord? exitRequested = await WaitForExitRequestAsync(operationId, token).ConfigureAwait(false);
                if (exitRequested == null)
                {
                    SelfDeployCutoverResult aborted = await FinishAsync(operationId, new[] { SelfDeployRestartStateEnum.Armed },
                        SelfDeployRestartStateEnum.Aborted, "exit_request_timeout", token).ConfigureAwait(false);
                    if (aborted.State != SelfDeployRestartStateEnum.ExitRequested) return aborted;
                    exitRequested = (await _Records.ReadAsync(token).ConfigureAwait(false)).Record;
                    if (exitRequested == null) return Outcome(null, "restart_record_unreadable");
                }
                else if (exitRequested.State != SelfDeployRestartStateEnum.ExitRequested)
                {
                    return Outcome(exitRequested.State, exitRequested.Reason);
                }

                return await StopOldThenLaunchCandidateAsync(exitRequested, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Drive an interrupted restart to a terminal state. Recovery restores the rollback artifact unless a
        /// launched candidate is still running and proves health; it never launches the candidate.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Outcome.</returns>
        public async Task<SelfDeployCutoverResult> RecoverAsync(CancellationToken token = default)
        {
            IDisposable? supervisorLock = _Records.TryAcquireSupervisorLock();
            if (supervisorLock == null) return Outcome(null, "supervisor_lock_held");
            using (supervisorLock)
            {
                SelfDeployRestartRecordReadResult read = await _Records.ReadAsync(token).ConfigureAwait(false);
                if (!read.Exists) return Outcome(null, "no_restart_record");
                if (!read.IsReadable) return Outcome(null, read.FailureReason);
                SelfDeployRestartRecord record = read.Record!;
                if (SelfDeployRestartRecord.IsTerminal(record.State)) return Outcome(record.State, "restart_record_terminal");

                SelfDeployProcessStateEnum oldState = StateOf(record.OldProcess);
                SelfDeployProcessStateEnum candidateState = StateOf(record.CandidateProcess);
                SelfDeployProcessStateEnum rollbackState = StateOf(record.RollbackProcess);
                if (oldState == SelfDeployProcessStateEnum.Unverified
                    || candidateState == SelfDeployProcessStateEnum.Unverified
                    || rollbackState == SelfDeployProcessStateEnum.Unverified)
                    return await FailAsync(record, "recovery_process_state_unverified", token).ConfigureAwait(false);

                int running = (oldState == SelfDeployProcessStateEnum.Running ? 1 : 0)
                    + (candidateState == SelfDeployProcessStateEnum.Running ? 1 : 0)
                    + (rollbackState == SelfDeployProcessStateEnum.Running ? 1 : 0);
                if (running > 1) return await FailAsync(record, "recovery_ownership_overlap_detected", token).ConfigureAwait(false);

                switch (record.State)
                {
                    case SelfDeployRestartStateEnum.Prepared:
                    case SelfDeployRestartStateEnum.Armed:
                    case SelfDeployRestartStateEnum.ExitRequested:
                    case SelfDeployRestartStateEnum.OldStopped:
                        if (oldState == SelfDeployProcessStateEnum.Running)
                        {
                            if (record.State == SelfDeployRestartStateEnum.OldStopped)
                                return await FailAsync(record, "recovery_old_process_running_after_stop", token).ConfigureAwait(false);
                            return await FinishAsync(record.OperationId, new[] { record.State },
                                SelfDeployRestartStateEnum.Aborted, "recovery_old_process_still_owner", token).ConfigureAwait(false);
                        }
                        return await RollBackAsync(record, "recovery_interrupted_before_candidate_launch", token).ConfigureAwait(false);

                    case SelfDeployRestartStateEnum.CandidateStarting:
                        if (oldState == SelfDeployProcessStateEnum.Running)
                            return await FailAsync(record, "recovery_old_process_running_after_stop", token).ConfigureAwait(false);
                        if (record.CandidateProcess == null)
                            return await FailAsync(record, "recovery_candidate_launch_identity_unrecorded", token).ConfigureAwait(false);
                        if (candidateState == SelfDeployProcessStateEnum.Running)
                        {
                            SelfDeployHealthResult candidateHealth = await WaitForHealthAsync(record.HealthUrl, record.CandidateProcess, token).ConfigureAwait(false);
                            if (candidateHealth.Healthy)
                                return await FinishAsync(record.OperationId, new[] { SelfDeployRestartStateEnum.CandidateStarting },
                                    SelfDeployRestartStateEnum.Committed, "recovery_candidate_healthy", token).ConfigureAwait(false);
                            return await RollBackAsync(record, "recovery_candidate_" + candidateHealth.FailureReason, token).ConfigureAwait(false);
                        }
                        return await RollBackAsync(record, "recovery_candidate_exited", token).ConfigureAwait(false);

                    case SelfDeployRestartStateEnum.RollingBack:
                        if (oldState == SelfDeployProcessStateEnum.Running)
                            return await FailAsync(record, "recovery_old_process_running_after_stop", token).ConfigureAwait(false);
                        return await RollBackAsync(record, "recovery_resumed_rollback", token).ConfigureAwait(false);

                    case SelfDeployRestartStateEnum.RollbackStarting:
                        if (oldState == SelfDeployProcessStateEnum.Running || candidateState == SelfDeployProcessStateEnum.Running)
                            return await FailAsync(record, "recovery_unexpected_process_running_during_rollback", token).ConfigureAwait(false);
                        if (record.RollbackProcess == null)
                            return await FailAsync(record, "recovery_rollback_launch_identity_unrecorded", token).ConfigureAwait(false);
                        if (rollbackState == SelfDeployProcessStateEnum.Running)
                        {
                            SelfDeployHealthResult rollbackHealth = await WaitForHealthAsync(record.HealthUrl, record.RollbackProcess, token).ConfigureAwait(false);
                            if (rollbackHealth.Healthy)
                                return await FinishAsync(record.OperationId, new[] { SelfDeployRestartStateEnum.RollbackStarting },
                                    SelfDeployRestartStateEnum.RolledBack, "recovery_rollback_healthy", token).ConfigureAwait(false);
                            return await StopFailedRollbackAsync(record, "recovery_rollback_" + rollbackHealth.FailureReason, token).ConfigureAwait(false);
                        }
                        return await LaunchRollbackAsync(record, SelfDeployRestartStateEnum.RollbackStarting, "recovery_rollback_exited_relaunched", token).ConfigureAwait(false);

                    default:
                        return Outcome(record.State, "restart_record_terminal");
                }
            }
        }

        private async Task<SelfDeployRestartRecord?> WaitForExitRequestAsync(string operationId, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + _Options.HandshakeTimeout;
            while (DateTime.UtcNow < deadline)
            {
                SelfDeployRestartRecordReadResult read = await _Records.ReadAsync(token).ConfigureAwait(false);
                if (read.IsReadable && String.Equals(read.Record!.OperationId, operationId, StringComparison.Ordinal)
                    && read.Record.State != SelfDeployRestartStateEnum.Armed)
                    return read.Record;
                await Task.Delay(_Options.PollInterval, token).ConfigureAwait(false);
            }
            return null;
        }

        private async Task<SelfDeployCutoverResult> StopOldThenLaunchCandidateAsync(SelfDeployRestartRecord record, CancellationToken token)
        {
            SelfDeployProcessStateEnum oldState = await _Host.WaitForExitAsync(
                record.OldProcess!, _Options.OldProcessExitTimeout, _Options.PollInterval, token).ConfigureAwait(false);
            if (oldState == SelfDeployProcessStateEnum.Unverified)
                return await FailAsync(record, "old_process_state_unverified", token).ConfigureAwait(false);
            if (oldState == SelfDeployProcessStateEnum.Running)
            {
                // The admiral committed to exit but did not. Stop only that exact process, not its descendants.
                bool stopped = await _Host.TerminateAsync(record.OldProcess!, false, _Options.TerminationTimeout, token).ConfigureAwait(false);
                if (!stopped) return await FailAsync(record, "old_process_exit_unconfirmed", token).ConfigureAwait(false);
            }

            SelfDeployRestartTransitionResult oldStopped = await _Records.TryTransitionAsync(record.OperationId,
                new[] { SelfDeployRestartStateEnum.ExitRequested },
                r =>
                {
                    r.OldExitConfirmedUtc = DateTime.UtcNow;
                    r.MoveTo(SelfDeployRestartStateEnum.OldStopped, oldState == SelfDeployProcessStateEnum.Running
                        ? "old_process_terminated_by_identity"
                        : "old_process_exited");
                }, token).ConfigureAwait(false);
            if (!oldStopped.Applied) return Outcome(oldStopped.Record?.State, oldStopped.FailureReason);
            record = oldStopped.Record!;

            string? candidateFailure = await _Artifacts.VerifyAsync(record.Candidate, token).ConfigureAwait(false);
            if (candidateFailure != null) return await RollBackAsync(record, "candidate_" + candidateFailure, token).ConfigureAwait(false);

            SelfDeployRestartTransitionResult launching = await _Records.TryTransitionAsync(record.OperationId,
                new[] { SelfDeployRestartStateEnum.OldStopped },
                r => r.MoveTo(SelfDeployRestartStateEnum.CandidateStarting, "candidate_launching"), token).ConfigureAwait(false);
            if (!launching.Applied) return Outcome(launching.Record?.State, launching.FailureReason);
            record = launching.Record!;

            SelfDeployProcessIdentity candidate;
            try
            {
                candidate = _Host.Start(_Planner.ForServer(record.Candidate, record.OperationId));
            }
            catch (SelfDeployCutoverException ex)
            {
                return await RollBackAsync(record, "candidate_" + ex.FailureReason, token).ConfigureAwait(false);
            }

            SelfDeployRestartTransitionResult launched = await _Records.TryTransitionAsync(record.OperationId,
                new[] { SelfDeployRestartStateEnum.CandidateStarting },
                r =>
                {
                    r.CandidateProcess = candidate;
                    r.MoveTo(SelfDeployRestartStateEnum.CandidateStarting, "candidate_launched");
                }, token).ConfigureAwait(false);
            if (!launched.Applied) return Outcome(launched.Record?.State, launched.FailureReason);
            record = launched.Record!;

            SelfDeployHealthResult health = await WaitForHealthAsync(record.HealthUrl, candidate, token).ConfigureAwait(false);
            if (health.Healthy)
                return await FinishAsync(record.OperationId, new[] { SelfDeployRestartStateEnum.CandidateStarting },
                    SelfDeployRestartStateEnum.Committed, "candidate_healthy", token).ConfigureAwait(false);
            return await RollBackAsync(record, "candidate_" + health.FailureReason, token).ConfigureAwait(false);
        }

        private async Task<SelfDeployCutoverResult> RollBackAsync(SelfDeployRestartRecord record, string reason, CancellationToken token)
        {
            SelfDeployRestartTransitionResult rollingBack = await _Records.TryTransitionAsync(record.OperationId,
                NonTerminalStates, r => r.MoveTo(SelfDeployRestartStateEnum.RollingBack, reason), token).ConfigureAwait(false);
            if (!rollingBack.Applied) return Outcome(rollingBack.Record?.State, rollingBack.FailureReason);
            record = rollingBack.Record!;

            if (record.CandidateProcess != null)
            {
                SelfDeployProcessStateEnum candidateState = _Host.GetState(record.CandidateProcess);
                if (candidateState == SelfDeployProcessStateEnum.Unverified)
                    return await FailAsync(record, "candidate_state_unverified", token).ConfigureAwait(false);
                if (candidateState == SelfDeployProcessStateEnum.Running)
                {
                    bool stopped = await _Host.TerminateAsync(record.CandidateProcess, true, _Options.TerminationTimeout, token).ConfigureAwait(false);
                    if (!stopped) return await FailAsync(record, "candidate_exit_unconfirmed", token).ConfigureAwait(false);
                }
            }

            int schemaAfter;
            try
            {
                schemaAfter = await _Schema.ReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return await FinishAsync(record.OperationId, new[] { SelfDeployRestartStateEnum.RollingBack },
                    SelfDeployRestartStateEnum.RollbackBlocked, "schema_version_unreadable", token).ConfigureAwait(false);
            }
            if (schemaAfter > record.SchemaVersionBefore)
                return await FinishAsync(record.OperationId, new[] { SelfDeployRestartStateEnum.RollingBack },
                    SelfDeployRestartStateEnum.RollbackBlocked, "schema_advanced_restore_required", token).ConfigureAwait(false);

            return await LaunchRollbackAsync(record, SelfDeployRestartStateEnum.RollingBack, "rollback_launching", token).ConfigureAwait(false);
        }

        private async Task<SelfDeployCutoverResult> LaunchRollbackAsync(
            SelfDeployRestartRecord record,
            SelfDeployRestartStateEnum expectedState,
            string reason,
            CancellationToken token)
        {
            string? rollbackFailure = await _Artifacts.VerifyAsync(record.Rollback, token).ConfigureAwait(false);
            if (rollbackFailure != null) return await FailAsync(record, "rollback_" + rollbackFailure, token).ConfigureAwait(false);

            SelfDeployRestartTransitionResult launching = await _Records.TryTransitionAsync(record.OperationId,
                new[] { expectedState },
                r =>
                {
                    r.RollbackProcess = null;
                    r.MoveTo(SelfDeployRestartStateEnum.RollbackStarting, reason);
                }, token).ConfigureAwait(false);
            if (!launching.Applied) return Outcome(launching.Record?.State, launching.FailureReason);
            record = launching.Record!;

            SelfDeployProcessIdentity rollback;
            try
            {
                rollback = _Host.Start(_Planner.ForServer(record.Rollback, record.OperationId));
            }
            catch (SelfDeployCutoverException ex)
            {
                return await FailAsync(record, "rollback_" + ex.FailureReason, token).ConfigureAwait(false);
            }

            SelfDeployRestartTransitionResult launched = await _Records.TryTransitionAsync(record.OperationId,
                new[] { SelfDeployRestartStateEnum.RollbackStarting },
                r =>
                {
                    r.RollbackProcess = rollback;
                    r.MoveTo(SelfDeployRestartStateEnum.RollbackStarting, "rollback_launched");
                }, token).ConfigureAwait(false);
            if (!launched.Applied) return Outcome(launched.Record?.State, launched.FailureReason);
            record = launched.Record!;

            SelfDeployHealthResult health = await WaitForHealthAsync(record.HealthUrl, rollback, token).ConfigureAwait(false);
            if (health.Healthy)
                return await FinishAsync(record.OperationId, new[] { SelfDeployRestartStateEnum.RollbackStarting },
                    SelfDeployRestartStateEnum.RolledBack, "rollback_healthy", token).ConfigureAwait(false);
            return await StopFailedRollbackAsync(record, "rollback_" + health.FailureReason, token).ConfigureAwait(false);
        }

        private async Task<SelfDeployCutoverResult> StopFailedRollbackAsync(SelfDeployRestartRecord record, string reason, CancellationToken token)
        {
            if (record.RollbackProcess != null
                && _Host.GetState(record.RollbackProcess) == SelfDeployProcessStateEnum.Running)
            {
                bool stopped = await _Host.TerminateAsync(record.RollbackProcess, true, _Options.TerminationTimeout, token).ConfigureAwait(false);
                if (!stopped) reason += "_exit_unconfirmed";
            }
            return await FailAsync(record, reason, token).ConfigureAwait(false);
        }

        private async Task<SelfDeployHealthResult> WaitForHealthAsync(
            string healthUrl,
            SelfDeployProcessIdentity process,
            CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + _Options.HealthTimeout;
            string lastFailure = "health_not_checked";
            while (DateTime.UtcNow < deadline)
            {
                SelfDeployProcessStateEnum state = _Host.GetState(process);
                if (state == SelfDeployProcessStateEnum.Exited)
                    return new SelfDeployHealthResult { FailureReason = "process_exited_before_healthy" };
                if (state == SelfDeployProcessStateEnum.Unverified)
                    return new SelfDeployHealthResult { FailureReason = "process_state_unverified" };
                SelfDeployHealthResult attempt = await _Probe.CheckAsync(healthUrl, process, token).ConfigureAwait(false);
                if (attempt.Healthy)
                {
                    if (_Host.GetState(process) == SelfDeployProcessStateEnum.Running) return attempt;
                    return new SelfDeployHealthResult { FailureReason = "process_exited_before_healthy" };
                }
                lastFailure = String.IsNullOrWhiteSpace(attempt.FailureReason) ? "health_failed" : attempt.FailureReason;
                await Task.Delay(_Options.PollInterval, token).ConfigureAwait(false);
            }
            return new SelfDeployHealthResult { FailureReason = "health_timeout_" + lastFailure };
        }

        private async Task<SelfDeployCutoverResult> FailAsync(SelfDeployRestartRecord record, string reason, CancellationToken token)
        {
            return await FinishAsync(record.OperationId, NonTerminalStates, SelfDeployRestartStateEnum.Failed, reason, token).ConfigureAwait(false);
        }

        private async Task<SelfDeployCutoverResult> FinishAsync(
            string operationId,
            IReadOnlyCollection<SelfDeployRestartStateEnum> expected,
            SelfDeployRestartStateEnum state,
            string reason,
            CancellationToken token)
        {
            SelfDeployRestartTransitionResult result = await _Records.TryTransitionAsync(operationId, expected,
                r => r.MoveTo(state, reason), token).ConfigureAwait(false);
            if (result.Applied) return Outcome(state, reason);
            return Outcome(result.Record?.State, result.FailureReason);
        }

        private SelfDeployProcessStateEnum StateOf(SelfDeployProcessIdentity? identity)
        {
            return identity == null ? SelfDeployProcessStateEnum.Exited : _Host.GetState(identity);
        }

        private static SelfDeployCutoverResult Outcome(SelfDeployRestartStateEnum? state, string reason)
        {
            return new SelfDeployCutoverResult { State = state, Reason = reason };
        }
    }
}
