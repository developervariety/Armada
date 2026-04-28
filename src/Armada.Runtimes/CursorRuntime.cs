namespace Armada.Runtimes
{
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
        /// Get the cursor CLI command. Resolves Cursor's official Windows installer
        /// location (%LOCALAPPDATA%\cursor-agent\) when the standard PATH/npm
        /// resolution misses, so users running Cursor's official installer
        /// (irm 'https://cursor.com/install?win32=true' | iex) don't need to
        /// hand-create a wrapper at %APPDATA%\npm\cursor-agent.cmd.
        /// </summary>
        protected override string GetCommand()
        {
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
        /// Build Cursor agent CLI arguments. Uses --print as a boolean (current
        /// cursor-agent CLI semantics; older releases accepted -p &lt;prompt&gt; as a
        /// flag-with-value, which silently failed to enable headless mode and
        /// caused --trust to be ignored). The prompt is the trailing positional
        /// argument. --trust skips the "Workspace Trust Required" prompt that
        /// would otherwise hang headless invocations against fresh temp
        /// directories.
        /// </summary>
        protected override List<string> BuildArguments(string prompt, string? model, string? finalMessageFilePath)
        {
            List<string> args = new List<string>();

            args.Add("--print");

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("--model");
                args.Add(model);
            }

            args.Add("--force");
            args.Add("--trust");
            args.Add("--output-format");
            args.Add("text");

            // Positional prompt must come last after all flags.
            args.Add(prompt);

            return args;
        }

        #endregion
    }
}
