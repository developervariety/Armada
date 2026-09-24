namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Production implementation of <see cref="IAgentWakeProcessHost"/>.
    /// Runs the agent CLI through <see cref="BoundedProcessRunner"/>: standard input is written while both output
    /// streams are read, each stream is kept within a byte budget, and the timeout kills the process tree. The
    /// background monitor calls <c>onExited</c> when done.
    /// </summary>
    public sealed class AgentWakeProcessHost : IAgentWakeProcessHost
    {
        #region Public-Members

        /// <summary>Most UTF-8 bytes kept from each output stream of a wake process; only a snippet is logged.</summary>
        public const int OutputLimitBytes = 64 * 1024;

        #endregion

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

            Task<BoundedProcessResult> run;
            try
            {
                // A timeout of zero or less still kills at once, as it always has, rather than running unbounded.
                BoundedProcessRequest bounded = new BoundedProcessRequest(psi, TimeSpan.FromSeconds(Math.Max(0.001, request.TimeoutSeconds)))
                {
                    StandardInput = string.IsNullOrEmpty(request.StdinPayload) ? null : request.StdinPayload,
                    OutputLimitBytes = OutputLimitBytes
                };
                run = BoundedProcessRunner.RunAsync(bounded);
            }
            catch (Exception ex)
            {
                _Logging.Error(_Header + "spawn failed for command " + request.Command + ": " + ex.Message);
                return false;
            }

            // The runner starts the process before its first await, so a start failure has already faulted the task.
            if (run.IsFaulted)
            {
                Exception failure = run.Exception!.GetBaseException();
                _Logging.Error(_Header + "spawn failed for command " + request.Command + ": " + failure.Message);
                return false;
            }

            _Logging.Info(_Header + "spawned command " + request.Command);
            _ = Task.Run(async () => await MonitorAsync(run, request, onExited).ConfigureAwait(false));
            return true;
        }

        #endregion

        #region Private-Methods

        private async Task MonitorAsync(Task<BoundedProcessResult> run, AgentWakeProcessRequest request, Action onExited)
        {
            try
            {
                BoundedProcessResult result = await run.ConfigureAwait(false);
                if (result.TimedOut)
                    _Logging.Warn(_Header + "process timed out after " + request.TimeoutSeconds + "s; killed command " + request.Command);

                LogExit(request.Command, result);
            }
            catch (Exception ex)
            {
                _Logging.Error(_Header + "monitor error: " + ex.Message);
            }
            finally
            {
                onExited();
            }
        }

        private static string TruncateForLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            const int maxChars = 1024;
            string trimmed = value.Trim();
            if (trimmed.Length <= maxChars) return trimmed;
            return "..." + trimmed.Substring(trimmed.Length - maxChars);
        }

        private void LogExit(string command, BoundedProcessResult result)
        {
            int? exitCode = result.ExitCode;
            string anomalies = BoundedProcessRunner.DescribeAnomalies(result);
            string baseInfo = "command " + command +
                " exitCode=" + (exitCode.HasValue ? exitCode.Value.ToString() : "unknown") +
                (result.TimedOut ? " (timed out)" : string.Empty) +
                (string.IsNullOrEmpty(anomalies) ? string.Empty : " [" + anomalies + "]");

            string stdoutSnippet = TruncateForLog(result.StandardOutput);
            string stderrSnippet = TruncateForLog(result.StandardError);
            bool failed = result.TimedOut || (exitCode.HasValue && exitCode.Value != 0) || !exitCode.HasValue;
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
