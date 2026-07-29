namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using System.Diagnostics;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for OpenAI Codex CLI.
    /// </summary>
    /// <remarks>
    /// Per-invocation reasoning effort: when <c>CaptainRuntimeOptions.ReasoningEffort</c> is set
    /// for a captain, Codex CLI receives <c>-c model_reasoning_effort=&lt;value&gt;</c> on each call.
    /// Accepted values: low|medium|high. The flag is injected before
    /// <c>--output-last-message</c> and the prompt argument so Codex parses it as a config
    /// override rather than prompt text.
    ///
    /// ReasoningEffort is silently ignored if absent from the captain's RuntimeOptionsJson,
    /// preserving backward compatibility for captains provisioned before the setting existed.
    /// </remarks>
    public class CodexRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Codex";

        /// <summary>
        /// Codex does not support session resume.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the codex CLI executable.
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
        /// Approval mode for codex operations.
        /// </summary>
        public string ApprovalMode { get; set; } = "full-auto";

        #endregion

        #region Private-Members

        private string _ExecutablePath = "codex";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public CodexRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Codex receives its prompt as a CLI argument, not via stdin. Suppressing the stdin pipe
        /// prevents Codex from detecting a piped context and printing "Reading additional input
        /// from stdin..." to stderr on every invocation.
        /// </summary>
        protected override bool RedirectStdin => false;

        /// <summary>
        /// Codex exec streams its ENTIRE human-readable working transcript (session header,
        /// reasoning, every exec command and its full stdout) to stderr, bloating mission logs
        /// 75-220x versus Claude/Cursor. Suppressing the stderr log-FILE write keeps logs bounded;
        /// the final answer is still captured via --output-last-message and echoed on exit, and
        /// heartbeat/progress detection is preserved because OnOutputReceived still fires for stderr.
        /// </summary>
        protected override bool WriteStderrToLogFile => false;

        /// <summary>
        /// Get the codex CLI command.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build Codex CLI arguments.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            List<string> args = new List<string>();

            args.Add("exec");
            args.Add("--json");
            if (String.Equals(ApprovalMode, "dangerous", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else if (String.Equals(ApprovalMode, "full-auto", StringComparison.OrdinalIgnoreCase)
                && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                // Armada already confines captains to an isolated dock worktree. In Linux
                // containers Codex's nested bubblewrap sandbox can fail on user namespace
                // and loopback setup even when the captain itself is correctly isolated.
                args.Add("--dangerously-bypass-approvals-and-sandbox");
            }
            else if (String.Equals(ApprovalMode, "full-auto", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--full-auto");
            }

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("--model");
                args.Add(model);
            }

            // Forward per-captain reasoning effort to Codex CLI as a per-invocation
            // config override. Codex CLI accepts -c model_reasoning_effort=<value>
            // for low|medium|high. Position before --output-last-message and
            // the prompt argument so Codex parses it as a config flag rather than
            // part of the prompt text. Null reasoningEffort preserves existing args
            // exactly (regression guard for captains without RuntimeOptionsJson).
            string? reasoningEffort = CaptainRuntimeOptions.GetReasoningEffort(captain);
            if (!String.IsNullOrWhiteSpace(reasoningEffort))
            {
                args.Add("-c");
                args.Add("model_reasoning_effort=" + reasoningEffort.Trim().ToLowerInvariant());
            }

            if (!String.IsNullOrEmpty(finalMessageFilePath))
            {
                args.Add("--output-last-message");
                args.Add(finalMessageFilePath);
            }

            args.Add(prompt);

            return args;
        }

        /// <summary>
        /// Capture exact usage from Codex's terminal turn event.
        /// </summary>
        protected override void HandleRawOutputLine(int processId, string line)
        {
            CodexEvent? evt = Deserialize(line);
            if (evt == null || !String.Equals(evt.Type, "turn.completed", StringComparison.Ordinal) || evt.Usage == null)
                return;

            CodexUsage reported = evt.Usage;
            PublishTokenUsage(processId, new RuntimeTokenUsage
            {
                Source = "codex.turn.completed",
                InputTokens = NonNegative(reported.InputTokens),
                OutputTokens = NonNegative(reported.OutputTokens),
                ReasoningTokens = NonNegative(reported.ReasoningOutputTokens),
                CacheReadTokens = NonNegative(reported.CachedInputTokens),
                CacheWriteTokens = NonNegative(reported.CacheWriteInputTokens)
            });
        }

        /// <summary>
        /// Keep Codex JSONL out of mission logs while preserving assistant protocol markers.
        /// </summary>
        protected override string TransformOutputLine(string line)
        {
            CodexEvent? evt = Deserialize(line);
            if (evt == null)
                return line;

            CodexItem? item = evt.Item;
            if (String.Equals(evt.Type, "item.completed", StringComparison.Ordinal) &&
                item != null &&
                String.Equals(item.Type, "agent_message", StringComparison.Ordinal) &&
                !String.IsNullOrEmpty(item.Text))
            {
                return item.Text;
            }

            return "[ARMADA:ACTIVITY] codex " + (evt.Type ?? "event").Replace('.', ' ');
        }

        private static CodexEvent? Deserialize(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<CodexEvent>(line);
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

        private sealed class CodexEvent
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("item")]
            public CodexItem? Item { get; set; }

            [JsonPropertyName("usage")]
            public CodexUsage? Usage { get; set; }
        }

        private sealed class CodexItem
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }
        }

        private sealed class CodexUsage
        {
            [JsonPropertyName("input_tokens")]
            public long? InputTokens { get; set; }

            [JsonPropertyName("cached_input_tokens")]
            public long? CachedInputTokens { get; set; }

            [JsonPropertyName("cache_write_input_tokens")]
            public long? CacheWriteInputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public long? OutputTokens { get; set; }

            [JsonPropertyName("reasoning_output_tokens")]
            public long? ReasoningOutputTokens { get; set; }
        }

        #endregion
    }
}
