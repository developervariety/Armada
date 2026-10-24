namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Admits server launches without duplicating retained jobs.</summary>
    public sealed partial class HarborRunnerClient
    {
        private async Task HandleLaunchAsync(
            HarborLaunchRequest request,
            Func<HarborMessage, Task> send,
            HashSet<string> exitSent,
            List<Task> background,
            CancellationToken token)
        {
            string jobId = request.JobId ?? String.Empty;
            bool safeJobId = String.Equals(SafeJobId(jobId), jobId, StringComparison.Ordinal);
            try
            {
                if (!safeJobId || String.IsNullOrWhiteSpace(request.LaunchKey) || request.LaunchKey.Length > 200 || request.LaunchKey.Any(Char.IsControl))
                    throw new HarborJobJournalException("launch_identity_invalid", "The launch identity is incomplete.");
                if (!_Settings.Supervisor.Commands.ContainsKey(request.Runtime))
                    throw new HarborJobJournalException("runtime_not_configured", "The requested runtime is not configured.");
                List<HarborLocalJobRecord> retained = _Journal.Enumerate();
                if (retained.Any(record => String.Equals(record.JobId, jobId, StringComparison.Ordinal)))
                    throw new HarborJobJournalException("job_already_exists", "The job is already retained and cannot be launched again.");
                if (retained.Count >= _Settings.Supervisor.MaxRetainedJobs)
                    throw new HarborJobJournalException("job_limit_exceeded", "The retained job limit has been reached.");
                if (retained.Count(record => record.IsRunning || !record.StopConfirmed) >= _Settings.MaxConcurrentJobs)
                    throw new HarborJobJournalException("job_capacity_exhausted", "The concurrent job limit has been reached.");

                using (System.Diagnostics.Process supervisor = await StartSupervisorAsync(request, token).ConfigureAwait(false))
                {
                    HarborLocalJobRecord started = await WaitForSupervisorStartAsync(jobId, supervisor, token).ConfigureAwait(false);
                    await send(new HarborStarted
                    {
                        JobId = jobId,
                        ProcessId = started.ProcessId!.Value,
                        ProcessStartedUtc = NormalizeUtc(started.ProcessStartedUtc!.Value)
                    }).ConfigureAwait(false);
                    StartJobPump(jobId, started.FirstOutputSequence, send, exitSent, background, token);
                }
            }
            catch (HarborJobJournalException exception)
            {
                _Log("Harbor launch refused: " + SafeJobId(jobId) + " " + exception.ReasonCode);
                await send(new HarborError { JobId = safeJobId ? jobId : null, Message = exception.ReasonCode }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception || exception is UnauthorizedAccessException || exception is ArgumentException)
            {
                _Log("Harbor launch failed: " + SafeJobId(jobId) + " harbor_supervisor_start_failed");
                await send(new HarborError { JobId = safeJobId ? jobId : null, Message = "harbor_supervisor_start_failed" }).ConfigureAwait(false);
            }
        }
    }
}
