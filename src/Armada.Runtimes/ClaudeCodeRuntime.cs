namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using System.Diagnostics;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for Anthropic Claude Code CLI.
    /// </summary>
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

            string? reasoningEffort = CaptainRuntimeOptions.GetReasoningEffort(captain);
            int? thinkingBudget = MapReasoningEffortToThinkingBudget(reasoningEffort);
            if (thinkingBudget.HasValue)
            {
                startInfo.Environment["MAX_THINKING_TOKENS"] = thinkingBudget.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Map a reasoning-effort tier to an Anthropic extended-thinking token budget.
        /// `max` triggers the per-model documented maximum (claude-opus-4-7 supports
        /// the largest budget; claude-sonnet-4-6 ships a smaller cap). Conservative
        /// upper bound of 128k is used at the `max` tier; Claude API silently caps
        /// to the model's actual max if smaller.
        /// </summary>
        private static int? MapReasoningEffortToThinkingBudget(string? reasoningEffort)
        {
            if (String.IsNullOrWhiteSpace(reasoningEffort)) return null;
            switch (reasoningEffort.Trim().ToLowerInvariant())
            {
                case "low":     return 4096;
                case "medium":  return 16384;
                case "high":    return 32768;
                case "xhigh":   return 65536;
                case "max":     return 128000;
                default:        return null;
            }
        }

        #endregion
    }
}
