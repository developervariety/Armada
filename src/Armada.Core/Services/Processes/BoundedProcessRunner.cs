namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The one runner for short-lived child processes: git, gh, glab, docker, version probes and check commands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both output streams are read at the same time from the moment the process starts, so a child that fills one
    /// pipe while the other is still open never blocks on the pipe. Each stream is kept within its own byte budget;
    /// output past the budget is still read, counted, and named in the result and in the kept text.
    /// </para>
    /// <para>
    /// The timeout and the caller's cancellation both kill the process with its whole live tree, and with its
    /// process group when the request owns one (so a background child the process left behind dies too). On Linux a
    /// group-owning run also tags every process it starts with a run identifier, and the kill then sweeps the
    /// processes that carry it, so a descendant that started its own session and left the tree dies too (see
    /// <see cref="ContainmentMarker"/>). After a kill, and after a normal exit, the readers get a bounded drain
    /// window: a descendant that still holds a pipe cannot keep the call from returning. Such a descendant is
    /// reported, not waited for. A normal exit kills nothing.
    /// </para>
    /// <para>
    /// Standard input is always redirected and closed, after any input the request supplies, so a child that
    /// prompts reads end of file instead of waiting on a terminal nobody answers.
    /// </para>
    /// </remarks>
    public static class BoundedProcessRunner
    {
        #region Private-Members

        private const int SignalKill = 9;
        private static readonly TimeSpan _ReaderStopGrace = TimeSpan.FromSeconds(1);
        private static readonly Lazy<IReadOnlyList<string>?> _GroupLauncher = new Lazy<IReadOnlyList<string>?>(ResolveGroupLauncher);

        #endregion

        #region Public-Members

        /// <summary>
        /// Launcher prefix that makes a process the leader of its own session and process group, or null when the
        /// host has none (Windows, or a Unix host with neither setsid nor perl). Resolved once per process.
        /// </summary>
        public static IReadOnlyList<string>? GroupLauncher => _GroupLauncher.Value;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run the process to completion, a timeout, or a cancellation.
        /// </summary>
        /// <param name="request">What to run and its bounds.</param>
        /// <param name="token">Caller cancellation; kills the process and its tree.</param>
        /// <returns>The outcome. Never throws for a timeout, a cancellation or a non-zero exit.</returns>
        /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
        public static async Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            BoundedProcessResult result = new BoundedProcessResult();
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                return result;
            }

            ProcessStartInfo startInfo = request.StartInfo;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            bool ownsProcessGroup = request.OwnProcessGroup && ApplyGroupLauncher(startInfo);
            string? containmentId = ownsProcessGroup ? ContainmentMarker.Apply(startInfo) : null;

            BoundedOutput? combined = null;
            BoundedTextCapture? stdoutCapture = null;
            BoundedTextCapture? stderrCapture = null;
            if (request.CombineOutput)
            {
                combined = new BoundedOutput(request.OutputLimitBytes);
            }
            else
            {
                stdoutCapture = new BoundedTextCapture(request.OutputLimitBytes, request.OutputShape);
                stderrCapture = new BoundedTextCapture(request.OutputLimitBytes, request.OutputShape);
            }

            Stopwatch clock = Stopwatch.StartNew();
            using (Process process = new Process { StartInfo = startInfo })
            using (CancellationTokenSource readersStop = new CancellationTokenSource())
            using (CancellationTokenSource inputStop = new CancellationTokenSource())
            {
                process.Start();

                Task input = WriteStandardInputAsync(process, request, result, inputStop.Token);
                Task stdoutPump = combined != null
                    ? BoundedOutputPump.PumpAsync(process.StandardOutput, combined, request.OutputLimitBytes, readersStop.Token)
                    : BoundedOutputPump.PumpTextAsync(process.StandardOutput, stdoutCapture!, readersStop.Token);
                Task stderrPump = combined != null
                    ? BoundedOutputPump.PumpAsync(process.StandardError, combined, request.OutputLimitBytes, readersStop.Token)
                    : BoundedOutputPump.PumpTextAsync(process.StandardError, stderrCapture!, readersStop.Token);
                Task pumps = Task.WhenAll(stdoutPump, stderrPump);

                bool killed = false;
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(request.Timeout);
                    try
                    {
                        await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                        if (request.WaitForOutputClose) await pumps.WaitAsync(limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (limit.IsCancellationRequested)
                    {
                        killed = true;
                        result.Cancelled = token.IsCancellationRequested;
                        result.TimedOut = !result.Cancelled;
                        inputStop.Cancel();
                        result.KillError = Kill(process, ownsProcessGroup);
                        if (containmentId != null) Sweep(containmentId, result);
                        result.StillRunningAfterKill = !await WaitForExitAsync(process, request.KillDrainTimeout).ConfigureAwait(false);
                        if (!await DrainAsync(pumps, request.KillDrainTimeout, readersStop).ConfigureAwait(false))
                            result.OutputDrainTimedOut = true;
                    }
                }

                if (!killed)
                {
                    result.ExitCode = process.ExitCode;
                    if (!await DrainAsync(pumps, request.OutputDrainTimeout, readersStop).ConfigureAwait(false))
                        result.OutputDrainTimedOut = true;
                }

                await FinishStandardInputAsync(input, inputStop, result).ConfigureAwait(false);
                clock.Stop();
                result.Duration = clock.Elapsed;

                if (combined != null)
                {
                    result.StandardOutput = combined.Render();
                    result.StandardOutputTruncated = combined.Truncated;
                    result.StandardOutputOmittedBytes = combined.OmittedBytes;
                }
                else
                {
                    result.StandardOutput = stdoutCapture!.Render(request.TruncationMarker);
                    result.StandardOutputTruncated = stdoutCapture.Truncated;
                    result.StandardOutputOmittedBytes = stdoutCapture.OmittedBytes;
                    result.StandardError = stderrCapture!.Render(request.TruncationMarker);
                    result.StandardErrorTruncated = stderrCapture.Truncated;
                    result.StandardErrorOmittedBytes = stderrCapture.OmittedBytes;
                }

                return result;
            }
        }

        /// <summary>
        /// Describe a run that did not end with its own exit, for a log line or an error message: the timeout, the
        /// cancellation, a failed kill, a pipe held open, or a truncated stream. Empty when there is nothing to say.
        /// </summary>
        /// <param name="result">Run outcome.</param>
        /// <returns>A short space-separated description.</returns>
        public static string DescribeAnomalies(BoundedProcessResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            List<string> parts = new List<string>();
            if (result.TimedOut) parts.Add("timed_out");
            if (result.Cancelled) parts.Add("cancelled");
            if (result.KillError != null) parts.Add("kill_failed=" + result.KillError);
            if (result.StillRunningAfterKill) parts.Add("still_running_after_kill");
            if (result.EscapedProcessesKilled > 0) parts.Add("escaped_processes_killed=" + result.EscapedProcessesKilled);
            if (result.ContainmentUnreadableProcesses > 0) parts.Add("containment_unreadable=" + result.ContainmentUnreadableProcesses);
            if (result.ContainmentSweepError != null) parts.Add("containment_sweep_incomplete=" + result.ContainmentSweepError);
            if (result.OutputDrainTimedOut) parts.Add("output_pipe_held_open");
            if (result.StandardOutputTruncated) parts.Add("stdout_omitted_bytes=" + result.StandardOutputOmittedBytes);
            if (result.StandardErrorTruncated) parts.Add("stderr_omitted_bytes=" + result.StandardErrorOmittedBytes);
            if (result.StandardInputError != null) parts.Add("stdin_failed=" + result.StandardInputError);
            return String.Join(" ", parts);
        }

        #endregion

        #region Private-Methods

        private static bool ApplyGroupLauncher(ProcessStartInfo startInfo)
        {
            IReadOnlyList<string>? launcher = GroupLauncher;
            if (launcher == null) return false;
            if (!String.IsNullOrEmpty(startInfo.Arguments))
                throw new ArgumentException("A process-group launch needs ArgumentList, not an argument string.", nameof(startInfo));

            // The launcher execs the target itself, so a target it cannot exec would end as an exit code instead of a
            // start failure. The target is therefore resolved first, in the order Process.Start resolves it; one that
            // does not resolve to a file this process may execute is started without the launcher, and its start
            // fails exactly as it would without a group.
            string? target = ResolveExecutable(startInfo.FileName);
            if (target == null) return false;

            List<string> arguments = new List<string>(startInfo.ArgumentList);
            startInfo.FileName = launcher[0];
            startInfo.ArgumentList.Clear();
            for (int i = 1; i < launcher.Count; i++) startInfo.ArgumentList.Add(launcher[i]);
            startInfo.ArgumentList.Add(target);
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            return true;
        }

        private static async Task WriteStandardInputAsync(Process process, BoundedProcessRequest request, BoundedProcessResult result, CancellationToken token)
        {
            try
            {
                if (request.StandardInputWriter != null)
                {
                    await request.StandardInputWriter(process.StandardInput.BaseStream, token).ConfigureAwait(false);
                }
                else if (request.StandardInput != null)
                {
                    await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // A child that exits or is killed before reading all of its input closes the pipe under the writer.
                result.StandardInputError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    // Closing flushes; a child that is already gone cannot take the flush.
                    if (result.StandardInputError == null && (request.StandardInput != null || request.StandardInputWriter != null))
                        result.StandardInputError = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        private static async Task FinishStandardInputAsync(Task input, CancellationTokenSource inputStop, BoundedProcessResult result)
        {
            if (input.IsCompleted)
            {
                await input.ConfigureAwait(false);
                return;
            }

            // The process is gone and its output is read; a writer still blocked has nobody left to write to.
            inputStop.Cancel();
            try
            {
                await input.WaitAsync(_ReaderStopGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result.StandardInputError ??= "standard input writer did not stop";
                Observe(input);
            }
        }

        /// <summary>
        /// Kill the process, its live tree, and its process group when it owns one. Returns why the kill failed, or null.
        /// </summary>
        private static string? Kill(Process process, bool ownsProcessGroup)
        {
            if (ownsProcessGroup && !OperatingSystem.IsWindows())
            {
                int processGroup = 0;
                try
                {
                    processGroup = process.Id;
                }
                catch (InvalidOperationException)
                {
                    processGroup = 0;
                }

                // The launcher made the process a session and group leader, so its identifier names the group.
                if (processGroup > 0) SignalProcessGroup(processGroup, SignalKill);
            }

            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return null;
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill, which is the state the kill wants.
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>
        /// Kill the descendants that left both the tree and the group, found by the run identifier they carry.
        /// </summary>
        private static void Sweep(string containmentId, BoundedProcessResult result)
        {
            ContainmentSweepOutcome outcome = ContainmentMarker.Sweep(containmentId);
            result.EscapedProcessesKilled = outcome.Killed;
            result.ContainmentUnreadableProcesses = outcome.Unreadable;
            result.ContainmentSweepError = outcome.Error;
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan bound)
        {
            using (CancellationTokenSource wait = new CancellationTokenSource(bound))
            {
                try
                {
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Let the readers take what is left, then stop them: a descendant can hold a pipe open indefinitely and
        /// the call must still return. Returns false when the readers had to be stopped.
        /// </summary>
        private static async Task<bool> DrainAsync(Task pumps, TimeSpan bound, CancellationTokenSource readersStop)
        {
            try
            {
                await pumps.WaitAsync(bound).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                readersStop.Cancel();
            }

            try
            {
                await pumps.WaitAsync(_ReaderStopGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A read that ignores cancellation ends when the process object is disposed and its pipes close.
                Observe(pumps);
            }

            return false;
        }

        private static void Observe(Task task)
        {
            // Observe a late fault so it is not raised again as an unobserved task exception.
            task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private static void SignalProcessGroup(int processGroup, int signal)
        {
            try
            {
                // A negative identifier addresses the whole process group. ESRCH (the group is already gone) is the
                // expected outcome after a normal exit and needs no handling.
                kill(-processGroup, signal);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // No libc kill on this host: the tree kill that follows is the remaining containment.
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        [DllImport("libc", SetLastError = true)]
        private static extern int access(string path, int mode);

        private const int AccessExecute = 1;

        /// <summary>
        /// Resolve a file name the way Process.Start does on Unix: a rooted path as given, then the directory of the
        /// running executable, then the current directory, then each PATH entry that holds an executable file.
        /// Returns the full path only when the chosen file exists and this process may execute it; otherwise null.
        /// </summary>
        private static string? ResolveExecutable(string fileName)
        {
            if (String.IsNullOrEmpty(fileName)) return null;

            string? candidate = null;
            if (Path.IsPathRooted(fileName))
            {
                candidate = fileName;
            }
            else
            {
                string? processPath = Environment.ProcessPath;
                string? processDirectory = String.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
                if (processDirectory != null && File.Exists(Path.Combine(processDirectory, fileName)))
                    candidate = Path.Combine(processDirectory, fileName);
                else if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), fileName)))
                    candidate = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                else
                    candidate = FindExecutableOnPath(fileName);
            }

            if (candidate == null || !IsExecutableFile(candidate)) return null;
            return Path.GetFullPath(candidate);
        }

        private static string? FindExecutableOnPath(string fileName)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (String.IsNullOrEmpty(path)) return null;
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory, fileName);
                if (IsExecutableFile(candidate)) return candidate;
            }

            return null;
        }

        private static bool IsExecutableFile(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                return access(path, AccessExecute) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // No libc access on this host: the file is not proven executable, so it starts without the launcher.
                return false;
            }
        }

        /// <summary>
        /// Resolve the command that starts a process as the leader of a new session and process group: the
        /// util-linux setsid, or perl's POSIX::setsid where setsid is not installed.
        /// </summary>
        private static IReadOnlyList<string>? ResolveGroupLauncher()
        {
            if (OperatingSystem.IsWindows()) return null;

            string? setsid = FindOnPath("setsid");
            if (setsid != null) return new List<string> { setsid };

            string? perl = FindOnPath("perl");
            if (perl != null)
            {
                return new List<string>
                {
                    perl,
                    "-e",
                    "use POSIX (); POSIX::setsid() or die \"setsid: $!\\n\"; exec { $ARGV[0] } @ARGV or die \"exec: $!\\n\";"
                };
            }

            return null;
        }

        private static string? FindOnPath(string name)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (String.IsNullOrEmpty(path)) path = "/usr/local/bin:/usr/bin:/bin";
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        #endregion
    }
}
