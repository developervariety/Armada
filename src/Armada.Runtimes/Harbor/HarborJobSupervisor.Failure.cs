namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;

    /// <summary>Records uncertain termination and retains process ownership until actual exit.</summary>
    public sealed partial class HarborJobSupervisor
    {
        private static Task ConfirmOrRecordUncertainExitAsync(HarborJobJournal journal, Process process, HarborLocalJobRecord record)
        {
            return KillAndWaitForExitAsync(journal, record, process, "supervisor_failure_exit_unconfirmed");
        }

        private static async Task KillAndWaitForExitAsync(HarborJobJournal journal, HarborLocalJobRecord record, Process process, string reason)
        {
            string? killFailure = null;
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception || exception is NotSupportedException)
            {
                killFailure = "child_kill_unconfirmed";
            }
            if (!process.HasExited)
            {
                bool exitedWithinGrace = false;
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    exitedWithinGrace = true;
                }
                catch (TimeoutException)
                {
                    exitedWithinGrace = false;
                }
                if (!exitedWithinGrace && !process.HasExited)
                {
                    record.IsRunning = true;
                    record.StopConfirmed = false;
                    journal.MarkOutputGap(record, killFailure ?? reason);
                    // Keep the supervisor and stream readers alive; an unconfirmed writer can still append to its pipes.
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            record.ExitCode = process.ExitCode;
            record.IsRunning = false;
            record.StopConfirmed = true;
        }
    }
}
