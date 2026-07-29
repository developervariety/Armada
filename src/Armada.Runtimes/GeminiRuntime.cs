namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using System.Diagnostics;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for Google Gemini CLI.
    /// </summary>
    public class GeminiRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Gemini";

        /// <summary>
        /// Gemini CLI does not support session resume.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the gemini CLI executable.
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

        /// <summary>
        /// Approval mode for Gemini operations.
        /// Current CLI values include default, auto_edit, yolo, and plan.
        /// </summary>
        public string ApprovalMode { get; set; } = "yolo";

        #endregion

        #region Private-Members

        private string _ExecutablePath = "gemini";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public GeminiRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Get the gemini CLI command.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build Gemini CLI arguments.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            List<string> args = new List<string>();

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("--model");
                args.Add(model);
            }

            args.Add("-p");
            args.Add(prompt);
            args.Add("--approval-mode");
            args.Add(ApprovalMode);
            args.Add("--output-format");
            args.Add("stream-json");

            return args;
        }

        /// <summary>
        /// Capture Gemini CLI's exact terminal per-model statistics.
        /// </summary>
        protected override void HandleRawOutputLine(int processId, string line)
        {
            GeminiEvent? evt = Deserialize(line);
            if (evt == null || !String.Equals(evt.Type, "result", StringComparison.Ordinal) || evt.Stats?.Models == null)
                return;

            Dictionary<string, GeminiModelStats> models = evt.Stats.Models;
            foreach (KeyValuePair<string, GeminiModelStats> model in models)
            {
                PublishTokenUsage(processId, new RuntimeTokenUsage
                {
                    Runtime = Name,
                    Model = model.Key,
                    Source = "gemini.result.stats.models",
                    InputTokens = NonNegative(model.Value.InputTokens),
                    OutputTokens = NonNegative(model.Value.OutputTokens),
                    CacheReadTokens = NonNegative(model.Value.Cached),
                    ProviderTotalTokens = model.Value.TotalTokens.HasValue
                        ? NonNegative(model.Value.TotalTokens)
                        : null
                });
            }
        }

        /// <summary>
        /// Keep Gemini JSONL readable while preserving assistant protocol markers.
        /// </summary>
        protected override string TransformOutputLine(string line)
        {
            GeminiEvent? evt = Deserialize(line);
            if (evt == null)
                return line;

            if (String.Equals(evt.Type, "message", StringComparison.Ordinal) &&
                String.Equals(evt.Role, "assistant", StringComparison.Ordinal) &&
                !String.IsNullOrEmpty(evt.Content))
            {
                return evt.Content;
            }

            if (StructuredRuntimeLogFormatter.TryBuildToolActivity(line, out string activity))
                return activity;

            return String.Empty;
        }

        private static GeminiEvent? Deserialize(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<GeminiEvent>(line);
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

        #endregion

        #region Private-Types

        private sealed class GeminiEvent
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("role")]
            public string? Role { get; set; }

            [JsonPropertyName("content")]
            public string? Content { get; set; }

            [JsonPropertyName("stats")]
            public GeminiStats? Stats { get; set; }
        }

        private sealed class GeminiStats
        {
            [JsonPropertyName("models")]
            public Dictionary<string, GeminiModelStats>? Models { get; set; }
        }

        private sealed class GeminiModelStats
        {
            [JsonPropertyName("total_tokens")]
            public long? TotalTokens { get; set; }

            [JsonPropertyName("input_tokens")]
            public long? InputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public long? OutputTokens { get; set; }

            [JsonPropertyName("cached")]
            public long? Cached { get; set; }
        }

        #endregion
    }
}
