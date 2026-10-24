namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>Local job journal stop operations.</summary>
    public sealed partial class HarborJobJournal
    {
        /// <summary>Write a stop request for the matching job. The supervisor validates child identity before acting.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="launchKey">Exact launch key.</param>
        /// <param name="gracePeriodMs">Grace period before forced process-tree termination.</param>
        public void WriteStopRequest(string jobId, string launchKey, int gracePeriodMs)
        {
            lock (_Sync)
            {
                HarborLocalJobRecord record = ReadRecord(jobId);
                if (!String.Equals(record.LaunchKey, launchKey, StringComparison.Ordinal)) throw Failure("stop_identity_mismatch", "The stop request does not match the retained launch.");
                if (gracePeriodMs < 0 || gracePeriodMs > 300000) throw Failure("stop_grace_invalid", "The stop grace period is outside its allowed range.");
                StopRequest request = new StopRequest
                {
                    JobId = jobId,
                    LaunchKey = launchKey,
                    GracePeriodMs = gracePeriodMs,
                    ProcessId = record.ProcessId,
                    ProcessStartedUtc = record.ProcessStartedUtc,
                    ProcessLinuxStartTicks = record.ProcessLinuxStartTicks,
                    ProcessLinuxBootId = record.ProcessLinuxBootId
                };
                AtomicWrite(StopPath(jobId), JsonSerializer.Serialize(request, _JsonOptions));
            }
        }
    }
}
