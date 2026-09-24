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

        /// <summary>
        /// Label written into this runtime's provider failure records.
        /// </summary>
        private const string RuntimeLabel = "gemini";

        private string _ExecutablePath = "gemini";

        // Streamed assistant text is joined into whole lines before it becomes a record, so a
        // protocol marker split across two delta events still starts a line of one record.
        private readonly StreamingTextLineAssembler _AssistantText = new StreamingTextLineAssembler();

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
            return String.Join(Environment.NewLine, BuildRecords(line));
        }

        /// <summary>
        /// Render one Gemini stream-json event as zero or more mission-log records.
        /// </summary>
        protected override IEnumerable<string> TransformOutputRecords(string line)
        {
            return BuildRecords(line);
        }

        /// <summary>
        /// Write the unfinished streamed line when the process exits without a terminal event.
        /// </summary>
        protected override IEnumerable<string> BuildProcessExitRecords()
        {
            List<string> records = new List<string>();
            AppendFlushed(records);
            return records;
        }

        /// <summary>
        /// Build the records for one event. Assistant text streamed as delta pieces is emitted one whole line
        /// at a time; any other event first writes the unfinished streamed line so the text keeps its order.
        /// </summary>
        private List<string> BuildRecords(string line)
        {
            List<string> records = new List<string>();
            GeminiEvent? evt = Deserialize(line);
            if (evt == null)
            {
                AppendFlushed(records);
                records.Add(line);
                return records;
            }

            bool assistantMessage = String.Equals(evt.Type, "message", StringComparison.Ordinal) &&
                String.Equals(evt.Role, "assistant", StringComparison.Ordinal);

            if (assistantMessage && evt.Delta == true)
            {
                foreach (string completed in _AssistantText.Append(evt.Content))
                {
                    if (completed.Length > 0) records.Add(completed);
                }
                return records;
            }

            AppendFlushed(records);

            if (assistantMessage && !String.IsNullOrEmpty(evt.Content))
            {
                records.Add(evt.Content);
                return records;
            }

            if (StructuredRuntimeLogFormatter.TryBuildToolActivity(line, WorkingDirectory, out string activity))
            {
                records.Add(activity);
                return records;
            }

            // A failure event carries no assistant or tool field, and suppressing it would hide the failure.
            if (StructuredRuntimeLogFormatter.TryBuildErrorRecord(line, RuntimeLabel, out string error))
                records.Add(error);

            return records;
        }

        private void AppendFlushed(List<string> records)
        {
            string rest = _AssistantText.Flush();
            if (rest.Length > 0) records.Add(rest);
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
            [JsonConverter(typeof(LenientStringConverter))]
            public string? Content { get; set; }

            /// <summary>
            /// True when the message is one streamed piece of a longer assistant message.
            /// </summary>
            [JsonPropertyName("delta")]
            public bool? Delta { get; set; }

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
