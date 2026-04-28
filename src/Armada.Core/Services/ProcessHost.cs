namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Production IProcessHost implementation. Spawns a detached subprocess, writes the
    /// stdin payload, and returns immediately. A background task monitors the process and
    /// disposes it when it exits or the timeout elapses.
    /// </summary>
    public sealed class ProcessHost : IProcessHost
    {
        private readonly LoggingModule _Logging;
        private const string _Header = "[ProcessHost] ";

        /// <summary>Constructs the host with the supplied logging module.</summary>
        public ProcessHost(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc/>
        public Task<ProcessSpawnResult> SpawnDetachedAsync(ProcessSpawnRequest request, CancellationToken token)
        {
            ProcessStartInfo psi = BuildStartInfo(request);
            Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for command: " + request.Command);

            int pid = process.Id;
            _Logging.Info(_Header + "spawned pid=" + pid + " command=" + request.Command);

            // Fire-and-forget: write stdin then monitor until exit or timeout
            _ = RunProcessAsync(process, request.StdinPayload, request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 600);

            return Task.FromResult(new ProcessSpawnResult
            {
                ProcessId = pid,
                Exited = process.HasExited,
                StandardOutputTail = null,
            });
        }

        private static ProcessStartInfo BuildStartInfo(ProcessSpawnRequest request)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = request.Command,
                Arguments = request.Args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(request.WorkingDirectory))
                psi.WorkingDirectory = request.WorkingDirectory;

            if (request.EnvironmentVariables != null)
            {
                foreach (KeyValuePair<string, string> kv in request.EnvironmentVariables)
                    psi.Environment[kv.Key] = kv.Value;
            }

            return psi;
        }

        private async Task RunProcessAsync(Process process, string stdinPayload, int timeoutSeconds)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdinPayload).ConfigureAwait(false);
                process.StandardInput.Close();

                // Drain stdout so the child process does not block on a full pipe buffer
                _ = Task.Run(async () =>
                {
                    try { await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false); }
                    catch { }
                });

                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _Logging.Warn(_Header + "process timed out; killing pid=" + process.Id);
                try { process.Kill(); } catch { }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "process monitor error pid=" + process.Id + ": " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
