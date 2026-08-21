namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for the Mux CLI.
    /// </summary>
    public class MuxRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Mux";

        /// <summary>
        /// Mux does not support session resume in Armada's current integration.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the mux CLI executable.
        /// </summary>
        public string ExecutablePath
        {
            get => _ExecutablePath;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(ExecutablePath));
                _ExecutablePath = value;
            }
        }

        #endregion

        #region Private-Members

        private string _ExecutablePath = "mux";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public MuxRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Get the mux CLI command.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build Mux CLI arguments.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            MuxCaptainOptions? options = CaptainRuntimeOptions.GetMuxOptions(captain);
            return MuxCommandBuilder.BuildPrintArguments(workingDirectory, prompt, model, finalMessageFilePath, options, ShowThinking);
        }

        /// <summary>
        /// Current Mux accepts piped instructions. Using stdin keeps long Armada
        /// prompts out of the Windows command line.
        /// </summary>
        protected override bool UsePromptStdin => true;

        /// <summary>
        /// Capture exact provider usage when a Mux JSONL event supplies it.
        /// Estimated context counters such as finalEstimatedTokens are intentionally ignored.
        /// </summary>
        protected override void HandleRawOutputLine(int processId, string line)
        {
            MuxEvent? evt = Deserialize(line);
            if (evt?.Usage == null || !evt.Usage.HasReportedValue())
                return;

            PublishTokenUsage(processId, new RuntimeTokenUsage
            {
                Source = "mux.provider_usage",
                InputTokens = NonNegative(evt.Usage.InputTokens ?? evt.Usage.InputTokensSnake),
                OutputTokens = NonNegative(evt.Usage.OutputTokens ?? evt.Usage.OutputTokensSnake),
                ReasoningTokens = NonNegative(evt.Usage.ReasoningTokens ?? evt.Usage.ReasoningTokensSnake),
                CacheReadTokens = NonNegative(evt.Usage.CacheReadTokens ?? evt.Usage.CacheReadTokensSnake),
                CacheWriteTokens = NonNegative(evt.Usage.CacheWriteTokens ?? evt.Usage.CacheWriteTokensSnake),
                ProviderTotalTokens = (evt.Usage.TotalTokens ?? evt.Usage.TotalTokensSnake).HasValue
                    ? NonNegative(evt.Usage.TotalTokens ?? evt.Usage.TotalTokensSnake)
                    : null
            });
        }

        /// <summary>
        /// Render Mux JSONL as readable activity and assistant output.
        /// </summary>
        protected override string TransformOutputLine(string line)
        {
            MuxEvent? evt = Deserialize(line);
            if (evt == null)
                return line;

            if (!String.IsNullOrEmpty(evt.Text))
                return evt.Text;
            if (!String.IsNullOrEmpty(evt.Content))
                return evt.Content;

            if (StructuredRuntimeLogFormatter.TryBuildToolActivity(line, WorkingDirectory, out string activity))
                return activity;

            return String.Empty;
        }

        private static MuxEvent? Deserialize(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<MuxEvent>(line);
            }
            catch
            {
                return null;
            }
        }

        private static long NonNegative(long? value)
        {
            return Math.Max(0, value ?? 0);
        }

        /// <summary>
        /// Apply Mux-specific environment overrides.
        /// </summary>
        protected override void ApplyEnvironment(System.Diagnostics.ProcessStartInfo startInfo, Captain? captain, string? model = null)
        {
            MuxCaptainOptions? options = CaptainRuntimeOptions.GetMuxOptions(captain);
            if (!String.IsNullOrWhiteSpace(options?.ConfigDirectory))
            {
                startInfo.Environment["MUX_CONFIG_ROOT"] = options.ConfigDirectory!;
            }

            if (!String.IsNullOrWhiteSpace(options?.BaseUrl))
            {
                startInfo.Environment["OPENAI_BASE_URL"] = options.BaseUrl!;
            }
        }

        /// <summary>
        /// Determine whether an output line is a Mux structured protocol event (for example
        /// run_started / run_completed) rather than assistant text. Mux emits these as single-line
        /// JSON objects carrying an eventType field; a consumer rendering the captain's reply skips
        /// them so raw protocol JSON does not leak into the chat.
        /// </summary>
        /// <param name="line">A single output line from the Mux CLI.</param>
        /// <returns>True if the line is a Mux protocol event; otherwise false.</returns>
        public static bool IsProtocolEventLine(string? line)
        {
            if (String.IsNullOrWhiteSpace(line)) return false;

            string trimmed = line.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}') return false;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(trimmed))
                {
                    return document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("eventType", out JsonElement eventType)
                        && eventType.ValueKind == JsonValueKind.String;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        #endregion

        #region Private-Types

        private sealed class MuxEvent
        {
            [JsonPropertyName("eventType")]
            public string? EventType { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("content")]
            public string? Content { get; set; }

            [JsonPropertyName("usage")]
            public MuxUsage? Usage { get; set; }
        }

        private sealed class MuxUsage
        {
            [JsonPropertyName("inputTokens")]
            public long? InputTokens { get; set; }

            [JsonPropertyName("input_tokens")]
            public long? InputTokensSnake { get; set; }

            [JsonPropertyName("outputTokens")]
            public long? OutputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public long? OutputTokensSnake { get; set; }

            [JsonPropertyName("reasoningTokens")]
            public long? ReasoningTokens { get; set; }

            [JsonPropertyName("reasoning_tokens")]
            public long? ReasoningTokensSnake { get; set; }

            [JsonPropertyName("cacheReadTokens")]
            public long? CacheReadTokens { get; set; }

            [JsonPropertyName("cache_read_tokens")]
            public long? CacheReadTokensSnake { get; set; }

            [JsonPropertyName("cacheWriteTokens")]
            public long? CacheWriteTokens { get; set; }

            [JsonPropertyName("cache_write_tokens")]
            public long? CacheWriteTokensSnake { get; set; }

            [JsonPropertyName("totalTokens")]
            public long? TotalTokens { get; set; }

            [JsonPropertyName("total_tokens")]
            public long? TotalTokensSnake { get; set; }

            public bool HasReportedValue()
            {
                return InputTokens.HasValue || InputTokensSnake.HasValue ||
                    OutputTokens.HasValue || OutputTokensSnake.HasValue ||
                    ReasoningTokens.HasValue || ReasoningTokensSnake.HasValue ||
                    CacheReadTokens.HasValue || CacheReadTokensSnake.HasValue ||
                    CacheWriteTokens.HasValue || CacheWriteTokensSnake.HasValue ||
                    TotalTokens.HasValue || TotalTokensSnake.HasValue;
            }
        }

        #endregion
    }
}
