namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using System.Diagnostics;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for Google Gemini CLI.
    /// </summary>
    /// <remarks>
    /// This runtime invokes the standalone <c>gemini</c> CLI directly and is distinct from
    /// cursor-gemini: when Cursor IDE routes a captain to Google's Gemini model it uses
    /// <see cref="CursorRuntime"/>, not this class. Use GeminiRuntime only when the captain
    /// is explicitly configured with <c>AgentRuntimeEnum.Gemini</c>.
    ///
    /// Reasoning effort: <c>CaptainRuntimeOptions.ReasoningEffort</c> is silently ignored for
    /// this runtime. The Gemini CLI exposes no equivalent per-invocation reasoning-effort flag
    /// as of the current supported version. The value is validated and stored in
    /// <c>RuntimeOptionsJson</c> but not forwarded to the process. Model routing and thinking
    /// depth are controlled by the Gemini CLI's own configuration and the <c>--model</c> flag.
    /// </remarks>
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

            return args;
        }

        #endregion
    }
}
