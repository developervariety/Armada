namespace Armada.Core.Services
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Runs a provider utility with argument-list execution and environment-only credentials.
    /// </summary>
    public sealed class SelfDeployNativeCommandRunner : ISelfDeployNativeCommandRunner
    {
        /// <summary>Maximum captured characters per native output stream.</summary>
        public const int MaximumOutputCharacters = 256 * 1024;

        /// <summary>Marker appended when a native output stream exceeds its capture limit.</summary>
        public const string OutputTruncationMarker = "[ARMADA: native command output truncated]";

        /// <summary>Default time to wait for redirected pipes to drain after the utility exits.</summary>
        public static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(5);

        private readonly TimeSpan _OutputDrainTimeout;

        /// <summary>Create a runner with the default pipe drain timeout.</summary>
        public SelfDeployNativeCommandRunner()
            : this(DefaultOutputDrainTimeout)
        {
        }

        /// <summary>
        /// Create a runner with an explicit pipe drain timeout: how long to wait for redirected pipes to close after
        /// the utility exits before failing with <c>native_command_io_drain_timeout</c>.
        /// </summary>
        public SelfDeployNativeCommandRunner(TimeSpan outputDrainTimeout)
        {
            if (outputDrainTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(outputDrainTimeout), "Must be positive.");
            _OutputDrainTimeout = outputDrainTimeout;
        }

        /// <inheritdoc />
        public async Task<SelfDeployNativeCommandResult> RunAsync(
            SelfDeployNativeCommandRequest request,
            CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.FileName)) throw new ArgumentException("Command executable is required.", nameof(request));

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = !String.IsNullOrWhiteSpace(request.StandardInputFilePath),
                CreateNoWindow = true
            };
            if (!String.IsNullOrWhiteSpace(request.WorkingDirectory))
                startInfo.WorkingDirectory = request.WorkingDirectory;

            foreach (string argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (System.Collections.Generic.KeyValuePair<string, string> variable in request.EnvironmentVariables)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("Native database utility did not start.");
                }
                catch (Win32Exception ex)
                {
                    // ENOENT / ERROR_FILE_NOT_FOUND (2) and ERROR_PATH_NOT_FOUND (3) mean the tool is not installed;
                    // any other start failure (for example EACCES) means it exists but cannot be executed.
                    bool missing = ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3;
                    throw new SelfDeployNativeClientMissingException(request.FileName, !missing, ex);
                }

                Task inputTask = Task.CompletedTask;
                if (!String.IsNullOrWhiteSpace(request.StandardInputFilePath))
                {
                    inputTask = CopyFileToStandardInputAsync(process, request.StandardInputFilePath!, token);
                }

                // Read without the caller token after launch. This guarantees both redirected
                // pipes are drained during cancellation, while ReadBoundedAsync prevents a noisy
                // provider from pinning server memory.
                Task<string> stdoutTask = ReadBoundedAsync(process.StandardOutput);
                Task<string> stderrTask = ReadBoundedAsync(process.StandardError);
                try
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    bool terminationFailed = false;
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        terminationFailed = true;
                    }
                    bool exited = false;
                    using (CancellationTokenSource killWait = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        try
                        {
                            await process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                            exited = true;
                        }
                        catch (OperationCanceledException)
                        {
                            exited = false;
                        }
                    }
                    if (terminationFailed) throw new InvalidOperationException("native_command_termination_failed");
                    if (!exited) throw new InvalidOperationException("native_command_termination_timeout");
                    if (!await AwaitIoAsync(inputTask, stdoutTask, stderrTask).ConfigureAwait(false))
                    {
                        bool streamsClosed = CloseRedirectedStreams(
                            process,
                            !String.IsNullOrWhiteSpace(request.StandardInputFilePath));
                        Exception? observationFailure = await ObserveIoAfterCloseAsync(
                            inputTask, stdoutTask, stderrTask).ConfigureAwait(false);
                        if (!streamsClosed)
                        {
                            throw new InvalidOperationException("native_command_io_close_failed", observationFailure);
                        }
                        throw new InvalidOperationException("native_command_io_drain_timeout", observationFailure);
                    }
                    throw;
                }
                if (!await AwaitIoAsync(inputTask, stdoutTask, stderrTask).ConfigureAwait(false))
                {
                    bool streamsClosed = CloseRedirectedStreams(
                        process,
                        !String.IsNullOrWhiteSpace(request.StandardInputFilePath));
                    Exception? observationFailure = await ObserveIoAfterCloseAsync(
                        inputTask, stdoutTask, stderrTask).ConfigureAwait(false);
                    if (!streamsClosed)
                    {
                        throw new InvalidOperationException("native_command_io_close_failed", observationFailure);
                    }
                    throw new InvalidOperationException("native_command_io_drain_timeout", observationFailure);
                }

                return new SelfDeployNativeCommandResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = await stdoutTask.ConfigureAwait(false),
                    StandardError = await stderrTask.ConfigureAwait(false)
                };
            }
        }

        private static async Task<string> ReadBoundedAsync(TextReader reader)
        {
            char[] buffer = new char[8192];
            StringBuilder output = new StringBuilder(Math.Min(MaximumOutputCharacters, 8192));
            bool truncated = false;
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count == 0) break;

                if (output.Length < MaximumOutputCharacters)
                {
                    int keep = Math.Min(count, MaximumOutputCharacters - output.Length);
                    output.Append(buffer, 0, keep);
                    if (keep < count) truncated = true;
                }
                else
                {
                    truncated = true;
                }
            }

            if (truncated)
            {
                output.Append('\n');
                output.Append(OutputTruncationMarker);
            }
            return output.ToString();
        }

        private async Task<bool> AwaitIoAsync(Task inputTask, Task stdoutTask, Task stderrTask)
        {
            try
            {
                await Task.WhenAll(inputTask, stdoutTask, stderrTask)
                    .WaitAsync(_OutputDrainTimeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                // The input copy can be canceled after a process kill. The output readers are
                // deliberately uncanceled and remain bounded, so only a pipe-drain timeout is
                // treated as a termination failure.
                return true;
            }
        }

        private async Task<Exception?> ObserveIoAfterCloseAsync(
            Task inputTask,
            Task stdoutTask,
            Task stderrTask)
        {
            try
            {
                await Task.WhenAll(inputTask, stdoutTask, stderrTask)
                    .WaitAsync(_OutputDrainTimeout).ConfigureAwait(false);
                return null;
            }
            catch (TimeoutException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                // The task is observed here so a faulted pipe cannot become an unobserved
                // exception after the command has already failed closed.
                return ex;
            }
        }

        private static bool CloseRedirectedStreams(Process process, bool inputRedirected)
        {
            bool closed = true;
            try
            {
                process.StandardOutput.Dispose();
            }
            catch (Exception)
            {
                closed = false;
            }
            try
            {
                process.StandardError.Dispose();
            }
            catch (Exception)
            {
                closed = false;
            }
            if (inputRedirected)
            {
                try
                {
                    process.StandardInput.Dispose();
                }
                catch (Exception)
                {
                    closed = false;
                }
            }
            return closed;
        }

        private static async Task CopyFileToStandardInputAsync(Process process, string path, CancellationToken token)
        {
            using (FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await source.CopyToAsync(process.StandardInput.BaseStream, 81920, token).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
    }
}
