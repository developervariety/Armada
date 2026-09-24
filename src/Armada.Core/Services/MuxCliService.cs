namespace Armada.Core.Services
{
    using System.Diagnostics;
    using System.Text.Json;
    using System.Text.Json.Serialization;
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

        // Mux reads endpoints.json case-insensitively and tolerates comments and trailing commas.
        private static readonly JsonSerializerOptions _EndpointsJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

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

            MuxProbeResult result = new MuxProbeResult
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

            ApplyEndpointConfiguration(result, model, options);
            return result;
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

        /// <summary>
        /// Fill the probe result with the endpoint a `mux print` launch of these options selects, read from
        /// endpoints.json in the config directory that launch uses. `mux --version` reports none of this, and
        /// `mux probe` sends a completion request to the provider, so reading the file is the only source that
        /// makes no provider call. Selection and defaults follow Mux: the named endpoint, else the default one,
        /// else the first; tool calling is on unless the endpoint's quirks set supportsTools to false. The
        /// built-in tool count is left unset because no provider-free Mux command reports it. Reading never
        /// changes <see cref="MuxProbeResult.Success"/>; a failure is named in
        /// <see cref="MuxProbeResult.EndpointConfigurationError"/>.
        /// </summary>
        private static void ApplyEndpointConfiguration(MuxProbeResult result, string? model, MuxCaptainOptions options)
        {
            string configDirectory = ResolveConfigDirectory(options);
            result.ConfigDirectory = configDirectory;
            result.SettingsFilePresent = File.Exists(Path.Combine(configDirectory, "settings.json"));
            result.McpServersFilePresent = File.Exists(Path.Combine(configDirectory, "mcp-servers.json"));

            string endpointsPath = Path.Combine(configDirectory, "endpoints.json");
            result.EndpointsFilePresent = File.Exists(endpointsPath);
            if (!result.EndpointsFilePresent)
            {
                result.EndpointConfigurationError = "endpoints.json was not found in the Mux config directory " + configDirectory + ".";
                return;
            }

            try
            {
                string json = File.ReadAllText(endpointsPath);
                MuxEndpointsFile? file = String.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<MuxEndpointsFile>(json, _EndpointsJsonOptions);
                List<MuxEndpointEntry> endpoints = file?.Endpoints?.Where(e => e != null).ToList() ?? new List<MuxEndpointEntry>();

                MuxEndpointEntry? selected;
                string selectionSource;
                if (!String.IsNullOrWhiteSpace(options.Endpoint))
                {
                    string endpointName = options.Endpoint!.Trim();
                    selected = endpoints.FirstOrDefault(e => String.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase));
                    selectionSource = "endpoint-option";
                    if (selected == null)
                    {
                        result.EndpointConfigurationError = "endpoints.json names no endpoint '" + endpointName + "'.";
                        return;
                    }
                }
                else
                {
                    selected = endpoints.FirstOrDefault(e => e.IsDefault);
                    selectionSource = "default";
                    if (selected == null && endpoints.Count > 0)
                    {
                        selected = endpoints[0];
                        selectionSource = "first";
                    }
                    if (selected == null)
                    {
                        result.EndpointConfigurationError = "endpoints.json lists no endpoint.";
                        return;
                    }
                }

                result.EndpointName = selected.Name ?? result.EndpointName;
                result.AdapterType = selected.AdapterType ?? String.Empty;
                result.BaseUrl = selected.BaseUrl ?? String.Empty;
                if (String.IsNullOrWhiteSpace(model))
                {
                    result.Model = selected.Model ?? String.Empty;
                }

                result.ToolsEnabled = selected.Quirks?.SupportsTools ?? true;
                result.EndpointSelectionSource = selectionSource;
                result.EndpointConfigurationRead = true;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                result.EndpointConfigurationError = "endpoints.json could not be read: " + ex.Message;
            }
        }

        /// <summary>
        /// The config directory a `mux print` launch of these options uses: --config-dir when the captain sets
        /// one, else the MUX_CONFIG_DIR variable, else ~/.mux.
        /// </summary>
        private static string ResolveConfigDirectory(MuxCaptainOptions options)
        {
            if (!String.IsNullOrWhiteSpace(options.ConfigDirectory))
                return Path.GetFullPath(options.ConfigDirectory!.Trim());

            string? environmentDirectory = Environment.GetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable);
            if (!String.IsNullOrWhiteSpace(environmentDirectory))
                return Path.GetFullPath(environmentDirectory.Trim());

            return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mux"));
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

        private sealed class MuxEndpointsFile
        {
            [JsonPropertyName("endpoints")]
            public List<MuxEndpointEntry>? Endpoints { get; set; } = null;
        }

        private sealed class MuxEndpointEntry
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; } = null;

            [JsonPropertyName("adapterType")]
            public string? AdapterType { get; set; } = null;

            [JsonPropertyName("baseUrl")]
            public string? BaseUrl { get; set; } = null;

            [JsonPropertyName("model")]
            public string? Model { get; set; } = null;

            [JsonPropertyName("isDefault")]
            public bool IsDefault { get; set; } = false;

            [JsonPropertyName("quirks")]
            public MuxEndpointQuirks? Quirks { get; set; } = null;
        }

        private sealed class MuxEndpointQuirks
        {
            [JsonPropertyName("supportsTools")]
            public bool? SupportsTools { get; set; } = null;
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
