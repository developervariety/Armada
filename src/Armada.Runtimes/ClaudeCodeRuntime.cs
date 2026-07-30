namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using System.Diagnostics;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for Anthropic Claude Code CLI.
    /// </summary>
    /// <remarks>
    /// Isolation flags: <c>--setting-sources project,local</c> and <c>--strict-mcp-config</c>
    /// are forwarded on every invocation (commit ba27e9f). This prevents user-level Claude Code
    /// plugins and MCP servers -- e.g. Playwright or other browser-automation tools -- from leaking
    /// into headless captain processes that run inside Armada dock worktrees. Without these flags,
    /// any MCP server the operator has registered in their personal Claude Code settings would be
    /// silently activated for every captain, causing unpredictable tool availability and
    /// potential side effects.
    ///
    /// Reasoning effort: mapped from <c>CaptainRuntimeOptions.ReasoningEffort</c> to
    /// MAX_THINKING_TOKENS env var (low=4096, medium=16384, high=128000).
    /// Accepted values: low|medium|high.
    /// </remarks>
    public class ClaudeCodeRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Claude Code";

        /// <summary>
        /// Claude Code supports session resume.
        /// </summary>
        public override bool SupportsResume => true;

        /// <summary>
        /// Path to the claude CLI executable.
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
        /// Whether to use --dangerously-skip-permissions flag.
        /// </summary>
        public bool SkipPermissions { get; set; } = true;

        #endregion

        #region Private-Members

        private string _ExecutablePath = "claude";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public ClaudeCodeRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Get the claude CLI command.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build Claude Code CLI arguments.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            List<string> args = new List<string>();

            args.Add("--print");
            args.Add("--verbose");
            args.Add("--output-format");
            args.Add("stream-json");

            // Isolate captain settings to project and local sources only; prevents user-level
            // plugins and MCP servers (e.g. Playwright) from leaking into headless captain processes.
            args.Add("--setting-sources");
            args.Add("project,local");
            args.Add("--strict-mcp-config");

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("--model");
                args.Add(model);
            }

            if (SkipPermissions)
            {
                args.Add("--dangerously-skip-permissions");
            }

            args.Add(prompt);

            return args;
        }

        /// <summary>
        /// Capture Claude Code's exact aggregated result usage.
        /// </summary>
        protected override void HandleRawOutputLine(int processId, string line)
        {
            ClaudeEvent? evt = Deserialize(line);
            if (evt == null || !String.Equals(evt.Type, "result", StringComparison.Ordinal) || evt.Usage == null)
                return;

            ClaudeUsage reported = evt.Usage;
            PublishTokenUsage(processId, new RuntimeTokenUsage
            {
                Source = "claude.result",
                InputTokens = NonNegative(reported.InputTokens),
                OutputTokens = NonNegative(reported.OutputTokens),
                CacheReadTokens = NonNegative(reported.CacheReadInputTokens),
                CacheWriteTokens = NonNegative(reported.CacheCreationInputTokens)
            });
        }

        /// <summary>
        /// Render Claude stream events as readable mission output. Assistant text and tool calls
        /// in the same message become separate records, so callers see each one on its own.
        /// </summary>
        protected override string TransformOutputLine(string line)
        {
            List<string> records = BuildRecords(line);
            return records.Count == 0 ? String.Empty : String.Join(Environment.NewLine, records);
        }

        /// <summary>
        /// Render Claude stream events as one or more mission-log records.
        /// </summary>
        protected override IEnumerable<string> TransformOutputRecords(string line)
        {
            return BuildRecords(line);
        }

        /// <summary>
        /// Build the mission-log records for one Claude Code stream-json event.
        /// Named tool activity is preserved (name plus its primary argument, redacted); tool
        /// output, session bookkeeping, and partial-message deltas are suppressed because they
        /// are large, duplicated, or carry no operator value. Unrecognized event types keep a
        /// generic activity record so new CLI events (e.g. rate-limit notices) stay visible.
        /// </summary>
        private static List<string> BuildRecords(string line)
        {
            List<string> records = new List<string>();

            ClaudeEvent? evt = Deserialize(line);
            if (evt == null)
            {
                records.Add(line);
                return records;
            }

            if (String.Equals(evt.Type, "assistant", StringComparison.Ordinal))
            {
                AppendAssistantRecords(evt, records);
                return records;
            }

            if (String.Equals(evt.Type, "user", StringComparison.Ordinal))
            {
                AppendToolResultRecords(evt, records);
                return records;
            }

            // Session bookkeeping (init, compact boundary) and partial-message deltas add no
            // operator value; the deltas also duplicate the completed assistant event.
            if (IsSuppressedSystemEvent(evt) ||
                String.Equals(evt.Type, "stream_event", StringComparison.Ordinal))
            {
                return records;
            }

            if (String.Equals(evt.Type, "result", StringComparison.Ordinal))
            {
                records.Add(BuildResultActivity(evt));
                return records;
            }

            string generic = "[ARMADA:ACTIVITY] claude " + (evt.Type ?? "event").Replace('_', ' ');
            if (!String.IsNullOrWhiteSpace(evt.Subtype))
                generic += " " + StructuredRuntimeLogFormatter.TruncateActivityText(evt.Subtype.Trim().Replace('_', ' '), 40);

            records.Add(generic);
            return records;
        }

        /// <summary>
        /// Append assistant text, extended-thinking text, and named tool calls in message order.
        /// </summary>
        private static void AppendAssistantRecords(ClaudeEvent evt, List<string> records)
        {
            if (evt.Message?.Content == null)
                return;

            StringBuilder text = new StringBuilder();
            foreach (ClaudeContent content in evt.Message.Content)
            {
                if (String.Equals(content.Type, "text", StringComparison.Ordinal) && !String.IsNullOrEmpty(content.Text))
                {
                    text.Append(content.Text);
                    continue;
                }

                // Flush pending text before a tool call so protocol markers stay on their own record.
                if (text.Length > 0)
                {
                    records.Add(text.ToString());
                    text.Clear();
                }

                if (String.Equals(content.Type, "thinking", StringComparison.Ordinal) && !String.IsNullOrEmpty(content.Thinking))
                {
                    records.Add(StructuredRuntimeLogFormatter.RedactSecretValues(content.Thinking));
                    continue;
                }

                if (String.Equals(content.Type, "tool_use", StringComparison.Ordinal) ||
                    String.Equals(content.Type, "server_tool_use", StringComparison.Ordinal) ||
                    String.Equals(content.Type, "mcp_tool_use", StringComparison.Ordinal))
                {
                    string activity = StructuredRuntimeLogFormatter.BuildToolActivity(
                        content.Name,
                        BuildToolDetail(content.Input),
                        null);
                    if (!String.IsNullOrEmpty(activity))
                        records.Add(activity);
                }
            }

            if (text.Length > 0)
                records.Add(text.ToString());
        }

        /// <summary>
        /// Append a record for each failed tool result. Successful results are suppressed because
        /// the tool call itself is already logged and tool output can be large and sensitive.
        /// </summary>
        private static void AppendToolResultRecords(ClaudeEvent evt, List<string> records)
        {
            if (evt.Message?.Content == null)
                return;

            foreach (ClaudeContent content in evt.Message.Content)
            {
                if (String.Equals(content.Type, "tool_result", StringComparison.Ordinal) && content.IsError == true)
                    records.Add("[ARMADA:ACTIVITY] tool result (error)");
            }
        }

        /// <summary>
        /// Select the most useful single argument of a tool call for the mission log.
        /// Argument values other than the primary one are excluded to keep the record compact
        /// and to avoid copying tool payloads into durable telemetry.
        /// </summary>
        private static string? BuildToolDetail(ClaudeToolInput? input)
        {
            if (input == null)
                return null;

            if (!String.IsNullOrWhiteSpace(input.FilePath))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.FilePath.Trim(), StructuredRuntimeLogFormatter.ShortDetailLimit);

            if (!String.IsNullOrWhiteSpace(input.NotebookPath))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.NotebookPath.Trim(), StructuredRuntimeLogFormatter.ShortDetailLimit);

            if (!String.IsNullOrWhiteSpace(input.Command))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.Command.Trim(), StructuredRuntimeLogFormatter.CommandDetailLimit);

            if (!String.IsNullOrWhiteSpace(input.Pattern))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.Pattern.Trim(), StructuredRuntimeLogFormatter.ShortDetailLimit);

            if (!String.IsNullOrWhiteSpace(input.Path))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.Path.Trim(), StructuredRuntimeLogFormatter.ShortDetailLimit);

            if (!String.IsNullOrWhiteSpace(input.Url))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.Url.Trim(), StructuredRuntimeLogFormatter.ShortDetailLimit);

            if (!String.IsNullOrWhiteSpace(input.Query))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.Query.Trim(), StructuredRuntimeLogFormatter.ShortDetailLimit);

            if (!String.IsNullOrWhiteSpace(input.Description))
                return StructuredRuntimeLogFormatter.TruncateActivityText(input.Description.Trim(), StructuredRuntimeLogFormatter.ShortDetailLimit);

            return null;
        }

        /// <summary>
        /// Summarize the terminal result event without copying the final message text, which the
        /// runtime already captures through the final-message file.
        /// </summary>
        private static string BuildResultActivity(ClaudeEvent evt)
        {
            StringBuilder builder = new StringBuilder("[ARMADA:ACTIVITY] claude result");

            if (!String.IsNullOrWhiteSpace(evt.Subtype))
                builder.Append(' ').Append(StructuredRuntimeLogFormatter.TruncateActivityText(evt.Subtype.Trim(), 40));

            if (evt.IsError == true)
                builder.Append(" error");

            if (evt.NumTurns.HasValue)
                builder.Append(" (").Append(Math.Max(0, evt.NumTurns.Value)).Append(" turns)");

            return builder.ToString();
        }

        /// <summary>
        /// True for system events that only report session bookkeeping.
        /// </summary>
        private static bool IsSuppressedSystemEvent(ClaudeEvent evt)
        {
            if (!String.Equals(evt.Type, "system", StringComparison.Ordinal))
                return false;

            return String.IsNullOrWhiteSpace(evt.Subtype)
                || String.Equals(evt.Subtype, "init", StringComparison.Ordinal)
                || String.Equals(evt.Subtype, "compact_boundary", StringComparison.Ordinal);
        }

        private static ClaudeEvent? Deserialize(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<ClaudeEvent>(line);
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
        /// Apply Claude Code specific environment variables. Forwards per-captain
        /// reasoning effort (extended thinking budget) via MAX_THINKING_TOKENS, which
        /// the Claude Code CLI honors as the per-process extended-thinking budget.
        /// Null/absent reasoningEffort preserves the CLI default (no env var set).
        /// </summary>
        protected override void ApplyEnvironment(ProcessStartInfo startInfo, Captain? captain)
        {
            startInfo.Environment["CLAUDE_CODE_DISABLE_NONINTERACTIVE_HINT"] = "1";

            // Remove nesting detection variables so captains can launch
            // even when the Admiral or CLI was started from within a Claude Code session
            startInfo.Environment.Remove("CLAUDECODE");
            startInfo.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

            // Autonomous recovery sets DisableExtendedThinking when retrying a mission that failed
            // on an Anthropic thinking-block replay error. In that case suppress MAX_THINKING_TOKENS
            // entirely (including any value inherited from the parent process) so the retry runs
            // without extended thinking. The default path keeps honoring reasoningEffort.
            if (CaptainRuntimeOptions.GetDisableExtendedThinking(captain))
            {
                startInfo.Environment.Remove("MAX_THINKING_TOKENS");
                return;
            }

            string? reasoningEffort = CaptainRuntimeOptions.GetReasoningEffort(captain);
            int? thinkingBudget = MapReasoningEffortToThinkingBudget(reasoningEffort);
            if (thinkingBudget.HasValue)
            {
                startInfo.Environment["MAX_THINKING_TOKENS"] = thinkingBudget.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Map a reasoning-effort tier to an Anthropic extended-thinking token budget.
        /// The high tier is Armada's maximum user-facing thinking budget.
        /// </summary>
        private static int? MapReasoningEffortToThinkingBudget(string? reasoningEffort)
        {
            if (String.IsNullOrWhiteSpace(reasoningEffort)) return null;
            switch (reasoningEffort.Trim().ToLowerInvariant())
            {
                case "low":     return 4096;
                case "medium":  return 16384;
                case "high":    return 128000;
                default:        return null;
            }
        }

        #endregion

        #region Private-Types

        private sealed class ClaudeEvent
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("subtype")]
            public string? Subtype { get; set; }

            [JsonPropertyName("is_error")]
            public bool? IsError { get; set; }

            [JsonPropertyName("num_turns")]
            public int? NumTurns { get; set; }

            [JsonPropertyName("message")]
            public ClaudeMessage? Message { get; set; }

            [JsonPropertyName("usage")]
            public ClaudeUsage? Usage { get; set; }
        }

        private sealed class ClaudeMessage
        {
            [JsonPropertyName("content")]
            [JsonConverter(typeof(ClaudeContentListConverter))]
            public List<ClaudeContent>? Content { get; set; }
        }

        /// <summary>
        /// Tolerates a message whose content is a plain string instead of a block array. The CLI
        /// uses the string form for some user messages; without this the whole event would fail
        /// to deserialize and the raw JSON line would land in the mission log.
        /// </summary>
        private sealed class ClaudeContentListConverter : JsonConverter<List<ClaudeContent>?>
        {
            public override List<ClaudeContent>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    return null;
                }

                return JsonSerializer.Deserialize<List<ClaudeContent>>(ref reader, options);
            }

            public override void Write(Utf8JsonWriter writer, List<ClaudeContent>? value, JsonSerializerOptions options)
            {
                throw new NotSupportedException("Claude message content is read-only telemetry.");
            }
        }

        private sealed class ClaudeContent
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("thinking")]
            public string? Thinking { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("input")]
            [JsonConverter(typeof(ClaudeToolInputConverter))]
            public ClaudeToolInput? Input { get; set; }

            [JsonPropertyName("is_error")]
            public bool? IsError { get; set; }
        }

        /// <summary>
        /// The subset of tool arguments rendered in mission-log activity records. Any other
        /// argument a tool accepts is intentionally not deserialized so it cannot reach the log.
        /// </summary>
        private sealed class ClaudeToolInput
        {
            [JsonPropertyName("file_path")]
            public string? FilePath { get; set; }

            [JsonPropertyName("notebook_path")]
            public string? NotebookPath { get; set; }

            [JsonPropertyName("command")]
            public string? Command { get; set; }

            [JsonPropertyName("pattern")]
            public string? Pattern { get; set; }

            [JsonPropertyName("path")]
            public string? Path { get; set; }

            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("query")]
            public string? Query { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }
        }

        /// <summary>
        /// Tolerates a non-object tool input. Partial streaming events carry the input as a
        /// string fragment; without this the whole event would fail to deserialize and the raw
        /// JSON line would land in the mission log.
        /// </summary>
        private sealed class ClaudeToolInputConverter : JsonConverter<ClaudeToolInput?>
        {
            public override ClaudeToolInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    return null;
                }

                return JsonSerializer.Deserialize<ClaudeToolInput>(ref reader, options);
            }

            public override void Write(Utf8JsonWriter writer, ClaudeToolInput? value, JsonSerializerOptions options)
            {
                throw new NotSupportedException("Claude tool input is read-only telemetry.");
            }
        }

        private sealed class ClaudeUsage
        {
            [JsonPropertyName("input_tokens")]
            public long? InputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public long? OutputTokens { get; set; }

            [JsonPropertyName("cache_read_input_tokens")]
            public long? CacheReadInputTokens { get; set; }

            [JsonPropertyName("cache_creation_input_tokens")]
            public long? CacheCreationInputTokens { get; set; }
        }

        #endregion
    }
}
