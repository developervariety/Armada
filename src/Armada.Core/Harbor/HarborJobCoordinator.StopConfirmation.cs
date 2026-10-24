namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;

    public sealed partial class HarborJobCoordinator
    {
        #region Public-Methods

        /// <summary>Confirm that an uncertain runner process stopped, using exact identity and named evidence.</summary>
        /// <param name="jobId">Durable job identifier.</param>
        /// <param name="expectedRevision">Revision inspected by the operator.</param>
        /// <param name="enrollmentGeneration">Expected runner enrollment generation.</param>
        /// <param name="runnerProcessId">Expected runner operating-system process identifier.</param>
        /// <param name="runnerProcessStartedUtc">Expected runner process start time.</param>
        /// <param name="evidence">Named evidence that confirms the process stopped.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the exact uncertain record was confirmed.</returns>
        public async Task<bool> ConfirmStoppedByOperatorAsync(
            string jobId,
            long expectedRevision,
            long enrollmentGeneration,
            int? runnerProcessId,
            DateTime? runnerProcessStartedUtc,
            string evidence,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(jobId) || String.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length > 1000)
                return false;
            JobRecord job;
            lock (_Gate)
            {
                if (!_Jobs.TryGetValue(jobId, out JobRecord? found) || found == null) return false;
                job = found;
                if (job.Revision != expectedRevision || job.State != HarborJobStateEnum.Uncertain
                    || job.EnrollmentGeneration != enrollmentGeneration || job.ProcessId != runnerProcessId
                    || (job.ProcessId.HasValue
                        ? !SameNullableUtcInstant(job.RunnerProcessStartedUtc, runnerProcessStartedUtc)
                        : job.RunnerProcessStartedUtc.HasValue || runnerProcessStartedUtc.HasValue)
                    || _Links.ContainsKey(job.RunnerId)) return false;
                job.State = HarborJobStateEnum.Failed;
                job.StopConfirmed = true;
                job.ExitCode = null;
                job.CompletedUtc = DateTime.UtcNow;
                job.FailureReason = "operator_confirmed_stopped: " + evidence.Trim();
                Touch(job, null);
            }
            if (!await PersistJobAsync(job, token).ConfigureAwait(false)) return false;
            IHarborJobRecoveryHost? recoveryHost;
            lock (_Gate) recoveryHost = _RecoveryHost;
            if (!String.IsNullOrWhiteSpace(job.MissionId) && recoveryHost != null)
            {
                await DeliverCompletionAsync(job, recoveryHost, token).ConfigureAwait(false);
            }
            else
            {
                List<JobRecord> terminal = new List<JobRecord> { job };
                HashSet<string> persisted = new HashSet<string>(StringComparer.Ordinal) { job.JobId };
                await DeliverFinishedAsync(terminal, persisted).ConfigureAwait(false);
            }
            return true;
        }

        #endregion
    }
}
