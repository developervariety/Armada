namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using System.Diagnostics;
    using System.IO;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for Cursor agent CLI.
    /// </summary>
    public class CursorRuntime : BaseAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public override string Name => "Cursor";

        /// <summary>
        /// Cursor does not support session resume.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Path to the cursor CLI executable.
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

        private string _ExecutablePath = "cursor-agent";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public CursorRuntime(LoggingModule logging) : base(logging)
        {
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Get the cursor CLI command.
        /// </summary>
        /// <summary>
        /// The runtime this adapter drives.
        /// </summary>
        protected override Armada.Core.Enums.AgentRuntimeEnum RuntimeType => Armada.Core.Enums.AgentRuntimeEnum.Cursor;

        /// <summary>
        /// Get the command to execute for this runtime.
        /// </summary>
        protected override string GetCommand()
        {
            // Resolve Cursor's official Windows installer location (%LOCALAPPDATA%\cursor-agent\)
            // when the standard PATH/npm resolution misses, so users of the official installer
            // do not need to hand-create a wrapper at %APPDATA%\npm\cursor-agent.cmd.
            string resolved = ResolveExecutable(_ExecutablePath);
            if (!String.Equals(resolved, _ExecutablePath, StringComparison.Ordinal))
                return resolved;

            if (OperatingSystem.IsWindows() &&
                String.Equals(_ExecutablePath, "cursor-agent", StringComparison.OrdinalIgnoreCase))
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!String.IsNullOrEmpty(localAppData))
                {
                    string installCmd = Path.Combine(localAppData, "cursor-agent", "cursor-agent.cmd");
                    if (File.Exists(installCmd))
                        return installCmd;
                }
            }

            return resolved;
        }

        /// <summary>
        /// Build Cursor agent CLI arguments.
        /// </summary>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain)
        {
            List<string> args = new List<string>();

            // -p selects non-interactive print mode. The prompt itself is delivered on stdin (see
            // UsePromptStdin), not as a positional argument, because on Windows the cursor-agent executable
            // is an npm ".cmd" wrapper and a multi-line argument passed through cmd.exe is truncated at the
            // first newline. cursor-agent reads the prompt from stdin in print mode.
            args.Add("-p");

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("--model");
                args.Add(model);
            }

            args.Add("--force");
            args.Add("--output-format");
            args.Add("text");

            return args;
        }

        /// <summary>
        /// Deliver the prompt on stdin rather than as a positional argument, avoiding the Windows cmd.exe
        /// multi-line-argument truncation. cursor-agent reads the prompt from stdin in -p (print) mode.
        /// </summary>
        protected override bool UsePromptStdin => true;

        #endregion
    }
}
