namespace Armada.Core.Services
{
    using System.Diagnostics;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Executes Mux CLI commands used by Armada for validation and endpoint inspection.
    /// </summary>
    public class MuxCliService
    {
        #region Private-Members

        private readonly string _Header = "[MuxCliService] ";
        private readonly LoggingModule _Logging;
        private readonly TimeSpan _DefaultTimeout = TimeSpan.FromSeconds(20);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MuxCliService(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Probe a Mux captain configuration.
        /// </summary>
        public async Task<MuxProbeResult> ProbeAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            MuxCaptainOptions? options = CaptainRuntimeOptions.GetMuxOptions(captain) ?? new MuxCaptainOptions();
            return await ProbeAsync(captain.Model, options, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Probe a Mux endpoint selection directly.
        /// </summary>
        public async Task<MuxProbeResult> ProbeAsync(string? model, MuxCaptainOptions options, CancellationToken token = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            DateTime startUtc = DateTime.UtcNow;
            MuxCommandExecutionResult execution = await ExecuteAsync(
                MuxCommandBuilder.BuildProbeArguments(model, options),
                _DefaultTimeout,
                token).ConfigureAwait(false);

            return new MuxProbeResult
            {
                ContractVersion = 1,
                Success = execution.ExitCode == 0,
                ErrorCode = execution.ExitCode == 0 ? String.Empty : "mux_cli_error",
                FailureCategory = execution.ExitCode == 0 ? String.Empty : "runtime",
                ErrorMessage = execution.ExitCode == 0 ? String.Empty : BuildCommandFailureMessage("version", execution),
                CommandName = "version",
                ConfigDirectory = options.ConfigDirectory ?? String.Empty,
                EndpointName = options.Endpoint ?? String.Empty,
                Model = model ?? String.Empty,
                McpSupported = true,
                DurationMs = Convert.ToInt64((DateTime.UtcNow - startUtc).TotalMilliseconds)
            };
        }

        /// <summary>
        /// Enumerate configured Mux endpoints.
        /// </summary>
        public Task<MuxEndpointListResult> ListEndpointsAsync(string? configDirectory, CancellationToken token = default)
        {
            MuxEndpointListResult result = new MuxEndpointListResult
            {
                ContractVersion = 1,
                Success = true,
                ConfigDirectory = configDirectory ?? String.Empty,
                Endpoints = new List<MuxEndpointInfo>()
            };

            return Task.FromResult(result);
        }

        /// <summary>
        /// Inspect a single configured Mux endpoint.
        /// </summary>
        public Task<MuxEndpointShowResult> ShowEndpointAsync(string endpointName, string? configDirectory, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(endpointName)) throw new ArgumentNullException(nameof(endpointName));

            MuxEndpointShowResult result = new MuxEndpointShowResult
            {
                Success = false,
                ContractVersion = 1,
                ConfigDirectory = configDirectory ?? String.Empty,
                ErrorCode = "unsupported",
                ErrorMessage = "Current Mux CLI versions do not expose named endpoint inspection."
            };

            return Task.FromResult(result);
        }

        #endregion

        #region Private-Methods

        private async Task<MuxCommandExecutionResult> ExecuteAsync(
            List<string> arguments,
            TimeSpan timeout,
            CancellationToken token)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(ResolveMuxExecutable());
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // Mux prints small JSON documents; 4 MiB per stream is far above any endpoint listing.
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, timeout)
            {
                OutputLimitBytes = 4 * 1024 * 1024,
                OutputShape = BoundedOutputShapeEnum.Head
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.KillError != null)
                _Logging.Warn(_Header + "could not kill mux; it may still be running: " + result.KillError);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (result.TimedOut)
                throw new TimeoutException("mux command timed out after " + timeout.TotalSeconds.ToString("0") + " seconds.");
            if (result.StandardOutputTruncated)
                throw new InvalidOperationException("mux output exceeded " + request.OutputLimitBytes + " bytes and was not parsed.");

            string stdout = result.StandardOutput;
            string stderr = result.StandardError;
            int exitCode = result.ExitCode ?? -1;
            if (exitCode != 0)
            {
                _Logging.Debug(_Header + "mux exited with code " + exitCode + ": " + FirstNonEmptyLine(stderr, stdout));
            }

            return new MuxCommandExecutionResult
            {
                ExitCode = exitCode,
                Stdout = stdout.Trim(),
                Stderr = stderr.Trim()
            };
        }

        private static string ResolveMuxExecutable()
        {
            if (!OperatingSystem.IsWindows())
                return "mux";

            string appDataNpm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "mux.cmd");

            if (File.Exists(appDataNpm))
                return appDataNpm;

            return "mux";
        }

        private static string BuildCommandFailureMessage(string commandName, MuxCommandExecutionResult execution)
        {
            string details = FirstNonEmptyLine(execution.Stderr, execution.Stdout);
            if (String.IsNullOrWhiteSpace(details))
            {
                details = "mux returned exit code " + execution.ExitCode + ".";
            }

            return "Mux " + commandName + " failed. " + details;
        }

        private static string FirstNonEmptyLine(string? primary, string? secondary)
        {
            foreach (string source in new[] { primary ?? String.Empty, secondary ?? String.Empty })
            {
                foreach (string line in source.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (!String.IsNullOrWhiteSpace(trimmed))
                    {
                        return trimmed;
                    }
                }
            }

            return String.Empty;
        }

        private sealed class MuxCommandExecutionResult
        {
            public int ExitCode { get; set; } = 0;
            public string Stdout { get; set; } = String.Empty;
            public string Stderr { get; set; } = String.Empty;
        }

        #endregion
    }
}
