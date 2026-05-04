namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for the Mux CLI.
    /// </summary>
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
