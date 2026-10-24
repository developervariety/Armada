namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;

    public sealed partial class HarborJobCoordinator
    {
        /// <summary>Hydrate durable jobs and restore exact mission attempts before runner links are accepted.</summary>
        /// <param name="recoveryHost">Lifecycle bridge that validates and restores exact attempts.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when recovery completed.</returns>
        public async Task<bool> InitializeRecoveryAsync(IHarborJobRecoveryHost recoveryHost, CancellationToken token = default)
        {
            if (recoveryHost == null) throw new ArgumentNullException(nameof(recoveryHost));
            if (_Store == null) return false;
            lock (_Gate)
            {
                if (_RecoveryInitialized) return true;
                _RecoveryHost = recoveryHost;
            }
            string? after = null;
            while (true)
            {
                List<HarborJobRecord> page = await _Store.EnumerateAsync(new HarborJobQuery
                {
                    RecoveryOnly = true,
                    JobIdAfter = after,
                    Limit = 1000
                }, token).ConfigureAwait(false);
                foreach (HarborJobRecord stored in page)
                {
                    token.ThrowIfCancellationRequested();
                    JobRecord job = new JobRecord(stored);
                    if ((!HarborJobRecord.IsTerminal(job.State) || !job.StopConfirmed)
                        && (job.State != HarborJobStateEnum.Uncertain || job.StopConfirmed || job.CompletedUtc.HasValue
                            || !String.Equals(job.FailureReason, ReasonResumeUnconfirmed, StringComparison.Ordinal)))
                    {
                        job.State = HarborJobStateEnum.Uncertain;
                        job.StopConfirmed = false;
                        job.CompletedUtc = null;
                        job.FailureReason = ReasonResumeUnconfirmed;
                        job.Revision++;
                    }
                    lock (_Gate) _Jobs[job.JobId] = job;
                    await RestoreHydratedMissionAsync(job, recoveryHost, token).ConfigureAwait(false);
                    if (job.State != stored.State || job.Revision != stored.Revision)
                    {
                        if (!await _Store.TryUpdateAsync(job.ToDurable(), token).ConfigureAwait(false))
                            return false;
                        job.Durable = true;
                    }
                    else job.Durable = true;
                    if (HarborJobRecord.IsTerminal(job.State) && job.StopConfirmed && !job.CompletionDelivered)
                        await DeliverCompletionAsync(job, recoveryHost, token).ConfigureAwait(false);
                }
                if (page.Count < 1000) break;
                after = page[page.Count - 1].JobId;
            }
            lock (_Gate) _RecoveryInitialized = true;
            return true;
        }

        private async Task RestoreHydratedMissionAsync(JobRecord job, IHarborJobRecoveryHost recoveryHost, CancellationToken token)
        {
            if (HarborJobRecord.IsTerminal(job.State) || String.IsNullOrWhiteSpace(job.MissionId)) return;
            HarborJobRecoveryResult restored = await recoveryHost.RestoreAsync(job.Snapshot(), token).ConfigureAwait(false);
            job.Observer = restored.Observer;
            if (restored.Disposition == HarborJobRecoveryDispositionEnum.Adopted) return;
            job.State = HarborJobStateEnum.Uncertain;
            job.StopConfirmed = false;
            job.FailureReason = ReasonResumeUnconfirmed;
            job.Revision++;
        }

    }
}
