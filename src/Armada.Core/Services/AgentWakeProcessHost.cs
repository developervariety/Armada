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
    /// Production implementation of <see cref="IAgentWakeProcessHost"/>.
    /// Spawns the agent CLI process, writes stdin, drains stdout/stderr in the background,
    /// and enforces a timeout via kill. The background monitor calls <c>onExited</c> when done.
    /// </summary>
    public sealed class AgentWakeProcessHost : IAgentWakeProcessHost
    {
        #region Private-Members

        private readonly LoggingModule _Logging;
        private const string _Header = "[AgentWakeProcessHost] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>Constructs a new AgentWakeProcessHost.</summary>
        public AgentWakeProcessHost(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc/>
        public bool TryStart(AgentWakeProcessRequest request, Action onExited)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (onExited == null) throw new ArgumentNullException(nameof(onExited));

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = request.Command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!string.IsNullOrEmpty(request.WorkingDirectory))
                psi.WorkingDirectory = request.WorkingDirectory;

            foreach (string arg in request.ArgumentList)
                psi.ArgumentList.Add(arg);

            if (request.EnvironmentVariables != null)
            {
                foreach (KeyValuePair<string, string> kv in request.EnvironmentVariables)
                    psi.Environment[kv.Key] = kv.Value;
            }

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _Logging.Error(_Header + "spawn failed for command " + request.Command + ": " + ex.Message);
                return false;
            }

            if (process == null)
            {
                _Logging.Error(_Header + "spawn returned null for command " + request.Command);
                return false;
            }

            _Logging.Info(_Header + "spawned pid " + process.Id + " for command " + request.Command);
            _ = Task.Run(async () => await MonitorAsync(process, request, onExited).ConfigureAwait(false));
            return true;
        }

        #endregion

        #region Private-Methods

        private async Task MonitorAsync(Process process, AgentWakeProcessRequest request, Action onExited)
        {
            bool timedOut = false;
            try
            {
                if (!string.IsNullOrEmpty(request.StdinPayload))
                    await process.StandardInput.WriteAsync(request.StdinPayload).ConfigureAwait(false);
                process.StandardInput.Close();

                Task<string> stdoutDrain = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrDrain = process.StandardError.ReadToEndAsync();

                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    _Logging.Warn(_Header + "process timed out after " + request.TimeoutSeconds + "s; killing pid " + process.Id);
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                await Task.WhenAny(Task.WhenAll(stdoutDrain, stderrDrain), Task.Delay(5000)).ConfigureAwait(false);

                string stdoutSnippet = ExtractSnippet(stdoutDrain);
                string stderrSnippet = ExtractSnippet(stderrDrain);

                int? exitCode = null;
                try { if (process.HasExited) exitCode = process.ExitCode; } catch { }

                LogExit(process.Id, request.Command, exitCode, timedOut, stdoutSnippet, stderrSnippet);
            }
            catch (Exception ex)
            {
                _Logging.Error(_Header + "monitor error: " + ex.Message);
            }
            finally
            {
                try { process.Dispose(); } catch { }
                onExited();
            }
        }

        private static string ExtractSnippet(Task<string> drain)
        {
            if (drain == null) return string.Empty;
            if (!drain.IsCompletedSuccessfully) return string.Empty;
            string value = drain.Result ?? string.Empty;
            return TruncateForLog(value);
        }

        private static string TruncateForLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            const int maxChars = 1024;
            string trimmed = value.Trim();
            if (trimmed.Length <= maxChars) return trimmed;
            return "..." + trimmed.Substring(trimmed.Length - maxChars);
        }

        private void LogExit(int pid, string command, int? exitCode, bool timedOut, string stdoutSnippet, string stderrSnippet)
        {
            string baseInfo = "pid " + pid + " command " + command +
                " exitCode=" + (exitCode.HasValue ? exitCode.Value.ToString() : "unknown") +
                (timedOut ? " (timed out)" : string.Empty);

            bool failed = timedOut || (exitCode.HasValue && exitCode.Value != 0) || !exitCode.HasValue;
            if (failed)
            {
                string detail = baseInfo;
                if (!string.IsNullOrEmpty(stdoutSnippet)) detail += " :: stdout=" + stdoutSnippet;
                if (!string.IsNullOrEmpty(stderrSnippet)) detail += " :: stderr=" + stderrSnippet;
                _Logging.Warn(_Header + "exited abnormally " + detail);
            }
            else
            {
                string detail = baseInfo;
                if (!string.IsNullOrEmpty(stderrSnippet)) detail += " :: stderr=" + stderrSnippet;
                _Logging.Info(_Header + "exited " + detail);
            }
        }

        #endregion
    }
}
