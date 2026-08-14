namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for the OpenCode CLI. OpenCode runs headlessly via <c>opencode run</c>,
    /// speaks OpenAI-compatible providers addressed as <c>provider/model</c>, and can emit raw JSONL
    /// events (<c>--format json</c>) in the same spirit as Mux. Per-captain reasoning effort maps to
    /// OpenCode's <c>--variant</c>, and requesting thinking maps to <c>--thinking</c>.
    /// </summary>
    public class OpenCodeRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "OpenCode";

        /// <summary>
        /// OpenCode session resume is not wired through Armada yet.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the opencode CLI executable.
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
        /// Whether to auto-approve permissions that are not explicitly denied. Required for autonomous
        /// mission execution; without it OpenCode blocks waiting for interactive approval.
        /// </summary>
        public bool AutoApprove { get; set; } = true;

        #endregion

        #region Private-Members

        private string _ExecutablePath = "opencode";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public OpenCodeRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// The runtime this adapter drives.
        /// </summary>
        protected override Armada.Core.Enums.AgentRuntimeEnum RuntimeType => Armada.Core.Enums.AgentRuntimeEnum.OpenCode;

        /// <summary>
        /// Get the command to execute for this runtime.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build OpenCode CLI arguments for a headless run.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            // Per-captain reasoning effort -> OpenCode provider variant (minimal/high).
            string? variant = ReasoningEffortTranslator.ToOpenCodeVariant(captain?.ReasoningEffort);
            return OpenCodeCommandBuilder.BuildRunArguments(workingDirectory, prompt, model, variant, ShowThinking, AutoApprove);
        }

        /// <summary>
        /// Deliver the prompt on stdin rather than as a positional argument, avoiding the Windows cmd.exe
        /// multi-line-argument truncation. 'opencode run' reads the prompt from stdin when none is supplied.
        /// </summary>
        protected override bool UsePromptStdin => true;

        #endregion
    }
}
