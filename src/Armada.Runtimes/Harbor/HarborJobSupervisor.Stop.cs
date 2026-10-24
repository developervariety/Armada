namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;

    /// <summary>Observes exact stop requests and confirms child termination before recording exit.</summary>
    public sealed partial class HarborJobSupervisor
    {
        private static async Task WaitForExitOrStopAsync(HarborJobJournal journal, HarborLocalJobRecord record, Process process, Task stdout, Task stderr, Task prompt)
        {
            while (!process.HasExited)
            {
                if (record.OutputGap)
                {
                    await KillAndWaitForExitAsync(journal, record, process, "output_capture_incomplete").ConfigureAwait(false);
                    return;
                }
                if (stdout.IsFaulted || stderr.IsFaulted || prompt.IsFaulted || stdout.IsCanceled || stderr.IsCanceled || prompt.IsCanceled)
                {
                    await KillAndWaitForExitAsync(journal, record, process, "supervisor_task_failed").ConfigureAwait(false);
                    return;
                }
                if (journal.TryReadStopRequest(record.JobId, record.LaunchKey, out int gracePeriodMs))
                {
                    try { process.CloseMainWindow(); }
                    catch (InvalidOperationException)
                    {
                        if (!process.HasExited) throw;
                    }
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(gracePeriodMs);
                    while (!process.HasExited && DateTime.UtcNow < deadline)
                        await Task.Delay(100).ConfigureAwait(false);
                    await KillAndWaitForExitAsync(journal, record, process, "stop_exit_unconfirmed").ConfigureAwait(false);
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }
}
