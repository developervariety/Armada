namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Runs host commands in-process on the Admiral's own machine. This is the Local-mode (standalone)
    /// implementation of the host-command seam; the Split-mode implementation delegates the same request to a
    /// Harbor over its link. Standard output and standard error are captured as raw character streams, so the
    /// returned text is exactly what the command wrote: NUL-separated git output keeps its exact terminators, and
    /// no line ending is added or normalized.
    /// </summary>
    public class LocalHostCommandExecutor : IHostCommandExecutor
    {
        #region Private-Members

        private const int _DrainAfterKillMs = 2000;
        private const int _ReadBufferChars = 4096;

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<HostCommandResult> RunAsync(HostCommandRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = request.Executable,
                WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? Environment.CurrentDirectory : request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = request.StandardInput != null,
                CreateNoWindow = true
            };

            foreach (string argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);

            // git and gh run under the C locale so callers that read their text output see stable wording.
            if (GitProcessEnvironment.IsGitTool(request.Executable))
                GitProcessEnvironment.Apply(startInfo);

            StringBuilder standardOutput = new StringBuilder();
            StringBuilder standardError = new StringBuilder();

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;

                if (request.Environment != null)
                {
                    foreach (KeyValuePair<string, string> variable in request.Environment)
                    {
                        if (!String.IsNullOrEmpty(variable.Key)) startInfo.Environment[variable.Key] = variable.Value;
                    }
                }

                process.Start();
                Task outputPump = PumpAsync(process.StandardOutput, standardOutput);
                Task errorPump = PumpAsync(process.StandardError, standardError);

                if (request.StandardInput != null)
                {
                    await process.StandardInput.WriteAsync(request.StandardInput).ConfigureAwait(false);
                    process.StandardInput.Close();
                }

                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (request.TimeoutMs > 0) linked.CancelAfter(request.TimeoutMs);

                    try
                    {
                        await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

                        // The process has exited; its pipes reach end of stream once every writer has closed them.
                        await Task.WhenAll(outputPump, errorPump).WaitAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        TryKill(process);
                        await DrainAsync(outputPump, errorPump).ConfigureAwait(false);
                        if (token.IsCancellationRequested) throw;

                        return new HostCommandResult
                        {
                            ExitCode = -1,
                            StandardOutput = Snapshot(standardOutput),
                            StandardError = Snapshot(standardError),
                            TimedOut = true
                        };
                    }

                    return new HostCommandResult
                    {
                        ExitCode = process.ExitCode,
                        StandardOutput = Snapshot(standardOutput),
                        StandardError = Snapshot(standardError),
                        TimedOut = false
                    };
                }
            }
        }

        #endregion

        #region Private-Methods

        private static async Task PumpAsync(StreamReader reader, StringBuilder sink)
        {
            char[] buffer = new char[_ReadBufferChars];
            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0) break;
                    lock (sink)
                    {
                        sink.Append(buffer, 0, read);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static async Task DrainAsync(Task outputPump, Task errorPump)
        {
            try
            {
                await Task.WhenAll(outputPump, errorPump).WaitAsync(TimeSpan.FromMilliseconds(_DrainAfterKillMs)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        private static string Snapshot(StringBuilder sink)
        {
            lock (sink)
            {
                return sink.ToString();
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
            }
        }

        #endregion
    }
}
