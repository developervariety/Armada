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

        /// <summary>
        /// Ceiling on tool calls awaiting a result. Far above any real turn, so it only ever
        /// bounds a stream whose results stopped arriving.
        /// </summary>
        private const int _MaxPendingToolCalls = 512;

        private string _ExecutablePath = "claude";

        private readonly object _PendingLock = new object();

        private readonly Dictionary<string, PendingToolCall> _PendingToolCalls =
            new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);

        private readonly Queue<string> _PendingOrder = new Queue<string>();

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
        /// output, session bookkeeping, reasoning, and partial-message deltas are suppressed
        /// because they are large, duplicated, private, or carry no operator value.
        /// Unrecognized event types keep a generic activity record so new CLI events
        /// (e.g. rate-limit notices) stay visible.
        /// </summary>
        private List<string> BuildRecords(string line)
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

            // Session bookkeeping and partial-message deltas add no operator value; the deltas
            // also duplicate the completed assistant event. A tool_progress heartbeat carries no
            // tool name, argument, or outcome -- the CLI emits one every 30s while a long command
            // runs, so a single mission logged 22 identical "claude tool progress" lines. The
            // call itself is already recorded once, with its result.
            if (IsSuppressedSystemEvent(evt) ||
                String.Equals(evt.Type, "stream_event", StringComparison.Ordinal) ||
                String.Equals(evt.Type, "tool_progress", StringComparison.Ordinal))
            {
                return records;
            }

            if (String.Equals(evt.Type, "result", StringComparison.Ordinal))
            {
                // Terminal event: any call still open never reported an outcome, so render it
                // now rather than dropping it silently.
                FlushPendingToolCalls(records);
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
        /// Append assistant text in message order and register each tool call for correlation.
        /// </summary>
        /// <remarks>
        /// A tool call is NOT rendered here. Claude Code reports the call and its outcome as two
        /// separate events, so rendering at call time can only ever produce a status-less line --
        /// which is why a ClaudeCode log used to show a bare "tool result (error)" that did not
        /// even name the failing tool. The call is held until its result arrives and then written
        /// once, with a status, matching what every other runtime writes.
        /// </remarks>
        private void AppendAssistantRecords(ClaudeEvent evt, List<string> records)
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

                // Reasoning is private model deliberation, not mission progress. Every runtime
                // drops it so the log reads the same everywhere.
                if (String.Equals(content.Type, "thinking", StringComparison.Ordinal))
                    continue;

                if (String.Equals(content.Type, "tool_use", StringComparison.Ordinal) ||
                    String.Equals(content.Type, "server_tool_use", StringComparison.Ordinal) ||
                    String.Equals(content.Type, "mcp_tool_use", StringComparison.Ordinal))
                {
                    RegisterPendingToolCall(content, records);
                }
            }

            if (text.Length > 0)
                records.Add(text.ToString());
        }

        /// <summary>
        /// Hold a tool call until its result arrives so the rendered record carries a status.
        /// </summary>
        private void RegisterPendingToolCall(ClaudeContent content, List<string> records)
        {
            if (String.IsNullOrWhiteSpace(content.Name))
                return;

            PendingToolCall pending = new PendingToolCall
            {
                Name = content.Name!,
                Detail = BuildToolDetail(content.Input)
            };

            if (String.IsNullOrWhiteSpace(content.Id))
            {
                // Nothing to correlate on. Render immediately without a status rather than lose
                // the call entirely.
                records.Add(RenderToolCall(pending, null));
                return;
            }

            lock (_PendingLock)
            {
                // Bound the map: a run whose results never arrive must not grow it without limit.
                if (_PendingOrder.Count >= _MaxPendingToolCalls)
                {
                    string oldestId = _PendingOrder.Dequeue();
                    PendingToolCall? evicted;
                    if (_PendingToolCalls.Remove(oldestId, out evicted) && evicted != null)
                        records.Add(RenderToolCall(evicted, StructuredRuntimeLogFormatter.IncompleteStatus));
                }

                _PendingToolCalls[content.Id!] = pending;
                _PendingOrder.Enqueue(content.Id!);
            }
        }

        /// <summary>
        /// Render each tool result against the call it belongs to. Tool output itself is never
        /// copied because it is unbounded and can carry secrets.
        /// </summary>
        private void AppendToolResultRecords(ClaudeEvent evt, List<string> records)
        {
            if (evt.Message?.Content == null)
                return;

            foreach (ClaudeContent content in evt.Message.Content)
            {
                if (!String.Equals(content.Type, "tool_result", StringComparison.Ordinal))
                    continue;

                string status = content.IsError == true
                    ? StructuredRuntimeLogFormatter.ErrorStatus
                    : StructuredRuntimeLogFormatter.OkStatus;

                PendingToolCall? pending = null;
                if (!String.IsNullOrWhiteSpace(content.ToolUseId))
                {
                    lock (_PendingLock)
                    {
                        _PendingToolCalls.Remove(content.ToolUseId!, out pending);
                    }
                }

                if (pending != null)
                {
                    records.Add(RenderToolCall(pending, status));
                    continue;
                }

                // Result with no matching call (truncated stream, resumed session). Keep failures
                // visible; a nameless success record would carry no information.
                if (content.IsError == true)
                    records.Add("[ARMADA:ACTIVITY] tool result (error)");
            }
        }

        /// <summary>
        /// Render one correlated tool call in the shared activity format.
        /// </summary>
        private string RenderToolCall(PendingToolCall pending, string? status)
        {
            return StructuredRuntimeLogFormatter.BuildToolActivity(
                pending.Name,
                pending.Detail,
                status,
                WorkingDirectory);
        }

        /// <summary>
        /// Render any tool call still open when the process exits. A killed or crashed captain
        /// never emits its terminal result event, and the call that was in flight is usually the
        /// reason it died -- so it has to reach the log.
        /// </summary>
        /// <returns>One record per unfinished tool call.</returns>
        protected override IEnumerable<string> BuildProcessExitRecords()
        {
            List<string> records = new List<string>();
            FlushPendingToolCalls(records);
            return records;
        }

        /// <summary>
        /// Render every still-open tool call as incomplete. Called on the terminal result event.
        /// </summary>
        private void FlushPendingToolCalls(List<string> records)
        {
            lock (_PendingLock)
            {
                while (_PendingOrder.Count > 0)
                {
                    string id = _PendingOrder.Dequeue();
                    PendingToolCall? pending;
                    if (_PendingToolCalls.Remove(id, out pending) && pending != null)
                        records.Add(RenderToolCall(pending, StructuredRuntimeLogFormatter.IncompleteStatus));
                }

                _PendingToolCalls.Clear();
            }
        }

        /// <summary>
        /// Select the most useful single argument of a tool call for the mission log. Argument
        /// values other than the primary one are excluded to keep the record compact and to avoid
        /// copying tool payloads into durable telemetry. Relativizing, redaction, and truncation
        /// are applied centrally by the formatter.
        /// </summary>
        private static string? BuildToolDetail(ClaudeToolInput? input)
        {
            if (input == null)
                return null;

            if (!String.IsNullOrWhiteSpace(input.FilePath)) return input.FilePath;
            if (!String.IsNullOrWhiteSpace(input.NotebookPath)) return input.NotebookPath;
            if (!String.IsNullOrWhiteSpace(input.Command)) return input.Command;
            if (!String.IsNullOrWhiteSpace(input.Pattern)) return input.Pattern;
            if (!String.IsNullOrWhiteSpace(input.Path)) return input.Path;
            if (!String.IsNullOrWhiteSpace(input.Url)) return input.Url;
            if (!String.IsNullOrWhiteSpace(input.Query)) return input.Query;
            if (!String.IsNullOrWhiteSpace(input.Description)) return input.Description;

            return null;
        }

        /// <summary>
        /// Summarize the terminal result event without copying the final message text, which the
        /// runtime already captures through the final-message file.
        /// </summary>
        private static string BuildResultActivity(ClaudeEvent evt)
        {
            StringBuilder builder = new StringBuilder("[ARMADA:ACTIVITY] claude result");

            // The CLI still reports subtype "success" on a turn that errored, so printing both
            // produced the self-contradicting "claude result success error". The outcome wins.
            if (evt.IsError == true)
            {
                builder.Append(' ').Append(StructuredRuntimeLogFormatter.ErrorStatus);
            }
            else if (!String.IsNullOrWhiteSpace(evt.Subtype))
            {
                builder.Append(' ').Append(StructuredRuntimeLogFormatter.TruncateActivityText(evt.Subtype.Trim(), 40));
            }

            if (evt.NumTurns.HasValue)
                builder.Append(" (").Append(Math.Max(0, evt.NumTurns.Value)).Append(" turns)");

            return builder.ToString();
        }

        /// <summary>
        /// True for system events. Every one of them reports session bookkeeping.
        /// </summary>
        /// <remarks>
        /// This used to suppress only the blank, "init", and "compact_boundary" subtypes. Any
        /// other subtype fell through to the generic branch and was written as
        /// "[ARMADA:ACTIVITY] claude system &lt;subtype&gt;" -- which the read-side noise filter
        /// could not remove, because it matches "[ARMADA:ACTIVITY] claude system" exactly and the
        /// suffix made every line distinct. A single run can emit dozens of them. Events that
        /// genuinely matter, such as rate-limit notices, arrive with their own type and are
        /// unaffected.
        /// </remarks>
        private static bool IsSuppressedSystemEvent(ClaudeEvent evt)
        {
            return String.Equals(evt.Type, "system", StringComparison.Ordinal);
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
        protected override void ApplyEnvironment(ProcessStartInfo startInfo, Captain? captain, string? model = null)
        {
            startInfo.Environment["CLAUDE_CODE_DISABLE_NONINTERACTIVE_HINT"] = "1";

            ApplyZylooModelRouting(startInfo, captain, captain?.Model ?? model);

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
        /// Point THIS captain process at Zyloo's Anthropic-native endpoint when its model is a
        /// <c>zyloo/</c> model. Does nothing for every other model, so a captain on the native
        /// Anthropic account is untouched.
        /// </summary>
        /// <remarks>
        /// Why the runtime and not a provider overlay: Claude models cache only when the request
        /// carries <c>cache_control</c> markers, and the OpenAI-compatible adapter Armada uses for
        /// Zyloo on OpenCode cannot emit them. Served that way, a captain re-reads its whole context
        /// at full price on every step -- 168M input tokens in one day, which reached the provider's
        /// daily spend cap. The Claude Code CLI speaks Anthropic natively and sends the markers, so
        /// pointing it at Zyloo's own Anthropic endpoint restores caching.
        ///
        /// The credential is resolved per captain: <see cref="Captain.ApiKey"/> wins when set, and
        /// the host-level <c>ZYLOO_KEY</c> environment variable is the fallback, so captains on
        /// separate Zyloo subscriptions run side by side. The same precedence applies to
        /// <see cref="Captain.ApiBaseUrl"/> against the default Zyloo endpoint.
        ///
        /// Isolation is per process, not global: <see cref="ProcessStartInfo.Environment"/> is a
        /// private copy for the child being launched. A native Claude captain launched in the same
        /// second keeps its own credentials and endpoint. Nothing else in Armada sets ANTHROPIC_*,
        /// so there is no value here to collide with.
        ///
        /// When the key is absent the captain is left on the native endpoint rather than launched
        /// against a half-configured one, because an unauthenticated launch fails per step and reads
        /// as a provider outage.
        /// </remarks>
        /// <param name="startInfo">Start info for the captain process being launched.</param>
        /// <param name="captain">Captain being launched; may be null.</param>
        private static void ApplyZylooRouting(ProcessStartInfo startInfo, Captain? captain)
        {
            ApplyZylooModelRouting(startInfo, captain, captain?.Model);
        }

        /// <summary>
        /// Apply Zyloo routing when the requested model id is a Zyloo model. Used by both the
        /// mission launch path (where the model comes from the captain record) and the captain
        /// creation validation path (where the captain record has not been built yet, so the model
        /// id is passed directly). Without this overload, model validation launches the Claude CLI
        /// on the native endpoint and fails every Zyloo id with model_not_found.
        /// </summary>
        /// <param name="startInfo">Start info for the captain process being launched.</param>
        /// <param name="captain">Captain being launched; may be null.</param>
        /// <param name="model">Zyloo model id to validate, or null when the captain's model is used.</param>
        private static void ApplyZylooModelRouting(ProcessStartInfo startInfo, Captain? captain, string? model)
        {
            string? effectiveModel = captain?.Model ?? model;
            if (!Armada.Core.Services.OpenCodeZylooProviderConfigBuilder.IsZylooModel(effectiveModel)) return;

            string? key = ResolveZylooKey(captain);
            if (String.IsNullOrWhiteSpace(key)) return;

            // Exactly the form Zyloo documents for Claude Code: base URL, ANTHROPIC_API_KEY, and the
            // model id kept at its canonical "zyloo/<id>". The provider routes on that prefix, so it
            // is passed through to --model unchanged rather than rewritten here.
            startInfo.Environment["ANTHROPIC_BASE_URL"] = ResolveZylooBaseUrl(captain);
            startInfo.Environment["ANTHROPIC_API_KEY"] = key;

            // An inherited auth token outranks the API key inside the CLI, so clear it for this child
            // only; the native captain beside it keeps its own.
            startInfo.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        }

        /// <summary>
        /// Resolve the Zyloo credential for a captain: the per-captain key wins, the host-level
        /// <c>ZYLOO_KEY</c> environment variable is the fallback.
        /// </summary>
        /// <param name="captain">Captain being launched; may be null.</param>
        /// <returns>Credential string, or null when none is configured.</returns>
        private static string? ResolveZylooKey(Captain? captain)
        {
            if (captain != null && !String.IsNullOrWhiteSpace(captain.ApiKey))
                return captain.ApiKey;

            return Environment.GetEnvironmentVariable("ZYLOO_KEY");
        }

        /// <summary>
        /// Resolve the Zyloo endpoint for a captain: the per-captain base URL wins, the default
        /// Anthropic-native endpoint is the fallback.
        /// </summary>
        /// <param name="captain">Captain being launched; may be null.</param>
        /// <returns>Base URL string.</returns>
        private static string ResolveZylooBaseUrl(Captain? captain)
        {
            if (captain != null && !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                return captain.ApiBaseUrl;

            return Armada.Core.Services.OpenCodeZylooProviderConfigBuilder.AnthropicBaseUrl;
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

        /// <summary>
        /// A tool call held until its result event arrives.
        /// </summary>
        private sealed class PendingToolCall
        {
            /// <summary>
            /// Tool name as reported by the CLI.
            /// </summary>
            public string Name { get; set; } = String.Empty;

            /// <summary>
            /// Primary argument, unnormalized. The formatter relativizes and redacts it.
            /// </summary>
            public string? Detail { get; set; }
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

            /// <summary>
            /// Identifier of a tool_use block, correlated against a later tool_result.
            /// </summary>
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            /// <summary>
            /// Identifier of the tool_use block a tool_result answers.
            /// </summary>
            [JsonPropertyName("tool_use_id")]
            public string? ToolUseId { get; set; }

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
