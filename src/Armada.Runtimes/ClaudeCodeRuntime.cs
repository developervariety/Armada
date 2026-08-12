namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using System.Diagnostics;
    using System.Globalization;
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
        /// <summary>
        /// The runtime this adapter drives.
        /// </summary>
        protected override Armada.Core.Enums.AgentRuntimeEnum RuntimeType => Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode;

        /// <summary>
        /// Get the command to execute for this runtime.
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
        /// Apply Claude Code specific environment variables.
        /// </summary>
        protected override void ApplyEnvironment(ProcessStartInfo startInfo, Captain? captain)
        {
            startInfo.Environment["CLAUDE_CODE_DISABLE_NONINTERACTIVE_HINT"] = "1";

            // Remove nesting detection variables so captains can launch
            // even when the Admiral or CLI was started from within a Claude Code session
            startInfo.Environment.Remove("CLAUDECODE");
            startInfo.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

            // Per-captain reasoning effort -> Claude Code extended-thinking budget.
            int? thinkingTokens = ReasoningEffortTranslator.ToClaudeThinkingTokens(captain?.ReasoningEffort);
            if (thinkingTokens.HasValue)
            {
                startInfo.Environment["MAX_THINKING_TOKENS"] = thinkingTokens.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        #endregion
    }
}
