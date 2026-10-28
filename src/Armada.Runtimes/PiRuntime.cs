namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for the pi coding agent CLI (pi.dev). pi runs headlessly in print mode with JSONL
    /// event output (<c>pi -p --mode json --no-session</c>), reads the prompt from stdin, addresses models as
    /// <c>provider/id</c>, and maps per-captain reasoning effort to <c>--thinking</c>. pi never prompts for
    /// permission: Bypass is its default behavior and Refuse removes the <c>bash</c> tool.
    /// </summary>
    public class PiRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Pi";

        /// <summary>
        /// pi session resume is not wired through Armada (launches use <c>--no-session</c>).
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the pi CLI executable.
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
        /// Whether pi may run shell commands. pi never prompts, so when false (or when the captain's effective
        /// auto-approve is off, the Refuse policy) the launch adds <c>--exclude-tools bash</c>.
        /// </summary>
        public bool AutoApprove { get; set; } = true;

        #endregion

        #region Private-Members

        private string _ExecutablePath = "pi";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public PiRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Recognize provider failures the CLI writes to stderr.
        /// </summary>
        /// <param name="line">One output line.</param>
        /// <param name="fromStdout">True for a stdout line.</param>
        /// <returns>The provider error, or null.</returns>
        protected override RuntimeProviderError? TryParseProviderError(string line, bool fromStdout)
        {
            if (fromStdout) return null;
            return RuntimeProviderErrorParser.TryParseCliStderrLine(line, "Pi");
        }

        /// <summary>
        /// The runtime this adapter drives.
        /// </summary>
        protected override Armada.Core.Enums.AgentRuntimeEnum RuntimeType => Armada.Core.Enums.AgentRuntimeEnum.Pi;

        /// <summary>
        /// Get the command to execute for this runtime.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build pi CLI arguments for a headless print-mode run.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            string? thinking = ReasoningEffortTranslator.ToPiThinkingLevel(captain?.ReasoningEffort);
            bool refuseShell = !(AutoApprove && CaptainRuntimeOptions.GetAutoApprove(captain));
            return PiCommandBuilder.BuildRunArguments(workingDirectory, prompt, model, thinking, refuseShell, false);
        }

        /// <summary>
        /// Deliver the prompt on stdin: with no positional message, 'pi -p' reads redirected stdin to EOF (verified on
        /// macOS with pi 1.4.0). Windows delivery through the npm ".cmd" wrapper is UNVERIFIED; stdin avoids the
        /// cmd.exe multi-line-argument truncation that a positional prompt would hit.
        /// </summary>
        protected override bool UsePromptStdin => true;

        #endregion
    }
}
