namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for the Mux CLI.
    /// </summary>
    /// <remarks>
    /// Mux is a passthrough runtime: argument construction is fully delegated to
    /// <see cref="Armada.Core.Services.MuxCommandBuilder"/>, which reads model, output path,
    /// and per-captain <c>MuxCaptainOptions</c> from the captain record. Armada itself does
    /// not interpret Mux's model-routing or provider configuration.
    ///
    /// Reasoning effort: <c>CaptainRuntimeOptions.ReasoningEffort</c> is silently ignored for
    /// this runtime. Mux manages provider-level model routing internally and exposes no
    /// per-invocation reasoning-effort CLI flag that Armada can inject. The value is validated
    /// and stored in <c>RuntimeOptionsJson</c> but not forwarded to the Mux process.
    ///
    /// This runtime is kept in-tree but is not actively used in Armada's default captain pool;
    /// it is wired for operators who run a local Mux proxy in front of multiple LLM providers.
    /// </remarks>
    public class MuxRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Mux";

        /// <summary>
        /// Mux does not support session resume in Armada's current integration.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the mux CLI executable.
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

        #endregion

        #region Private-Members

        private string _ExecutablePath = "mux";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public MuxRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Get the mux CLI command.
        /// </summary>
        protected override string GetCommand()
        {
            return ResolveExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Build Mux CLI arguments.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            MuxCaptainOptions? options = CaptainRuntimeOptions.GetMuxOptions(captain);
            return MuxCommandBuilder.BuildPrintArguments(workingDirectory, prompt, model, finalMessageFilePath, options);
        }

        #endregion
    }
}
