namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Waits for a bounded child process and records its result.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private static async Task<HarborWorkspaceProcessResult> RunProcessAsync(
            ProcessStartInfo start,
            TimeSpan timeout,
            int outputLimit,
            CancellationToken token)
        {
            DateTime startedUtc = DateTime.UtcNow;
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (Process process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw Failure("workspace_process_start_failed", "The configured local process did not start.");
                Task<HarborWorkspaceStreamResult> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, outputLimit);
                Task<HarborWorkspaceStreamResult> stderr = ReadBoundedAsync(process.StandardError.BaseStream, outputLimit);
                bool timedOut = false;
                bool cancelled = false;
                using (CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timer.CancelAfter(timeout);
                    try
                    {
                        await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested || timer.IsCancellationRequested)
                    {
                        cancelled = token.IsCancellationRequested;
                        timedOut = !cancelled;
                        KillProcessTree(process);
                        await ConfirmProcessExitAsync(process).ConfigureAwait(false);
                    }
                }
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                stopwatch.Stop();
                return new HarborWorkspaceProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = (await stdout.ConfigureAwait(false)).Text,
                    StandardError = (await stderr.ConfigureAwait(false)).Text,
                    StandardOutputBytes = (await stdout.ConfigureAwait(false)).Bytes,
                    StandardErrorBytes = (await stderr.ConfigureAwait(false)).Bytes,
                    StandardOutputTotalBytes = stdout.Result.TotalBytesRead,
                    StandardErrorTotalBytes = stderr.Result.TotalBytesRead,
                    StandardOutputOmittedBytes = stdout.Result.OmittedBytes,
                    StandardErrorOmittedBytes = stderr.Result.OmittedBytes,
                    Truncated = stdout.Result.Truncated || stderr.Result.Truncated,
                    TimedOut = timedOut,
                    Cancelled = cancelled,
                    StopConfirmed = true,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                    StartedUtc = startedUtc
                };
            }
        }

    }
}
