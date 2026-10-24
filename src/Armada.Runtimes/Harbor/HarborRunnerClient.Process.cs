namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Waits for durable child identity and applies exact-owned stop requests.</summary>
    public sealed partial class HarborRunnerClient
    {
        private async Task<HarborLocalJobRecord> WaitForSupervisorStartAsync(string jobId, Process supervisor, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    HarborLocalJobRecord record = _Journal.Read(jobId);
                    if (record.ProcessId.HasValue && record.ProcessStartedUtc.HasValue) return record;
                    if (!String.IsNullOrWhiteSpace(record.FailureReason))
                        throw new HarborJobJournalException(record.FailureReason, "The supervisor failed before it started the child process.");
                }
                catch (HarborJobJournalException exception) when (exception.ReasonCode == "job_not_found" && !supervisor.HasExited)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                    continue;
                }
                catch (IOException) when (!supervisor.HasExited)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                    continue;
                }
                if (supervisor.HasExited) break;
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            }
            throw new HarborJobJournalException("supervisor_start_unconfirmed", "The supervisor did not persist child identity before the start deadline.");
        }

        private void HandleKill(HarborKillRequest request)
        {
            try
            {
                HarborLocalJobRecord record = _Journal.Read(request.JobId);
                RequestSafeStop(record, request.GracefulTimeoutMs);
            }
            catch (HarborJobJournalException exception)
            {
                _Log("Harbor kill refused: " + request.JobId + " " + exception.ReasonCode);
            }
        }
    }
}
