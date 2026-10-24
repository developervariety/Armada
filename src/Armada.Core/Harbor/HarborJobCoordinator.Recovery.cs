namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;

    /// <summary>Durable restart recovery and authenticated runner adoption for Harbor jobs.</summary>
    public sealed partial class HarborJobCoordinator
    {
        #region Public-Methods

        /// <summary>Read snapshots for all records hydrated by this coordinator.</summary>
        /// <returns>Immutable current job snapshots.</returns>
        public IReadOnlyList<HarborJobSnapshot> GetSnapshots()
        {
            lock (_Gate)
            {
                List<HarborJobSnapshot> snapshots = new List<HarborJobSnapshot>(_Jobs.Count);
                foreach (JobRecord job in _Jobs.Values) snapshots.Add(job.Snapshot());
                return snapshots;
            }
        }

        /// <summary>Persist the completion-delivered acknowledgment after the lifecycle callback returns.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="expectedRevision">Revision delivered to the callback.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the exact terminal revision was acknowledged.</returns>
        public async Task<bool> AcknowledgeCompletionDeliveredAsync(string jobId, long expectedRevision, CancellationToken token = default)
        {
            if (_Store == null || String.IsNullOrWhiteSpace(jobId)) return false;
            bool acknowledged = await _Store.TryAcknowledgeCompletionDeliveredAsync(jobId, expectedRevision, token).ConfigureAwait(false);
            if (acknowledged)
            {
                lock (_Gate)
                {
                    if (_Jobs.TryGetValue(jobId, out JobRecord? job) && job.Revision == expectedRevision)
                    {
                        job.CompletionDelivered = true;
                        job.Revision++;
                        job.LastUpdateUtc = DateTime.UtcNow;
                        job.Completion.TrySetResult(job.Snapshot());
                    }
                }
            }
            return acknowledged;
        }

        #endregion

        #region Private-Methods

        private async Task DeliverCompletionAsync(JobRecord job, IHarborJobRecoveryHost recoveryHost, CancellationToken token)
        {
            HarborJobSnapshot terminal = job.Snapshot();
            if (!await recoveryHost.DeliverCompletionAsync(terminal, token).ConfigureAwait(false)) return;
            await AcknowledgeCompletionDeliveredAsync(job.JobId, terminal.Revision, token).ConfigureAwait(false);
        }

        private async Task MarkUncertainAndPersistAsync(JobRecord job, string reason)
        {
            List<HarborJobRecord> writes = new List<HarborJobRecord>();
            lock (_Gate) MarkUncertain(job, reason, writes);
            await PersistAsync(writes).ConfigureAwait(false);
        }

        private async Task<bool> PersistJobAsync(JobRecord job, CancellationToken token)
        {
            if (_Store == null) return true;
            HarborJobRecord record;
            lock (_Gate) record = job.ToDurable();
            try
            {
                return await _Store.TryUpdateAtRevisionAsync(record, record.Revision - 1, token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordPersistFailure(job.JobId, "resume update", exception);
                return false;
            }
        }

        private static bool SameUtcInstant(DateTime? stored, DateTime? reported)
        {
            if (!stored.HasValue || !reported.HasValue || stored.Value.Kind != DateTimeKind.Utc || reported.Value.Kind != DateTimeKind.Utc) return false;
            long storedTicks = stored.Value.Ticks / 10;
            long reportedTicks = reported.Value.Ticks / 10;
            return storedTicks == reportedTicks;
        }

        private static bool SameNullableUtcInstant(DateTime? stored, DateTime? reported)
        {
            if (!stored.HasValue || !reported.HasValue) return stored.HasValue == reported.HasValue;
            return SameUtcInstant(stored, reported);
        }

        private static bool SameRunnerProcess(JobRecord job, HarborResumeJobInfo proof)
        {
            if (!job.ProcessId.HasValue || !job.RunnerProcessStartedUtc.HasValue
                || !proof.ProcessId.HasValue || proof.ProcessId.Value <= 0 || !proof.ProcessStartedUtc.HasValue)
                return false;
            return proof.ProcessId == job.ProcessId && SameUtcInstant(job.RunnerProcessStartedUtc, proof.ProcessStartedUtc);
        }

        #endregion
    }
}
