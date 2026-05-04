namespace Armada.Runtimes
{
    using Armada.Core.Models;
    using System.Diagnostics;
    using System.IO;
    using SyslogLogging;

    /// <summary>
    /// Agent runtime adapter for Cursor agent CLI.
    /// </summary>
    /// <remarks>
    /// KNOWN LIMITATION -- reasoning effort not forwarded (cursor-agent CLI v2026.04.29-c83a488):
    /// cursor-agent exposes no <c>--thinking-effort</c> or <c>--reasoning-effort</c> flag as of
    /// this version. The value from <c>CaptainRuntimeOptions.ReasoningEffort</c> is validated and
    /// stored in RuntimeOptionsJson but NOT forwarded to the process. When cursor-agent CLI gains
    /// this flag, wire it in <c>BuildArguments</c> at the marked comment block.
    ///
    /// KNOWN ISSUE -- silent no-op failure (orchestrator-observed 2026-05-03):
    /// Cursor-agent headless invocations from within Armada dock worktrees can silently produce
    /// zero diff. Symptom: 8 stderr lines reading "The system cannot find the path specified.",
    /// single stdout line "ok", exit code 0 -- admiral interprets exit 0 as WorkProduced and
    /// reports mission complete with an empty diff. Root cause is a PATH / CWD / stdin / Cursor
    /// IDE auth-state mismatch in the headless process context spawned by Armada; the
    /// cursor-agent.cmd bootstrap probes fail before any agent logic runs. Observed on voyages
    /// vyg_moq2qawf (composer-2-fast) and vyg_moq2qxhz (gemini-3.5-pro). A runtime-level fix
    /// is in progress in a separate session.
    ///
    /// Windows install path: <c>%LOCALAPPDATA%\cursor-agent\cursor-agent.cmd</c> (official
    /// installer). Prompt is delivered via stdin (not CLI argument) to avoid the Windows
    /// cmd.exe ~8KB command-line length limit.
    /// </remarks>
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
        /// Get the cursor CLI command. Resolution order on Windows with the default
        /// "cursor-agent" command:
        /// 1. ARMADA_TEST_CURSOR_AGENT env var (test shim override, avoids polluting system paths).
        /// 2. Official Cursor installer path (%LOCALAPPDATA%\cursor-agent\cursor-agent.cmd).
        /// 3. PATH/npm fallback via ResolveExecutable.
        /// A stale npm shim at %APPDATA%\npm\cursor-agent.cmd exits 0 without invoking Cursor,
        /// silently misclassifying missions as WorkProduced; checking the official path first
        /// prevents it from winning.
        /// </summary>
        protected override string GetCommand()
        {
            // Test shim override: ARMADA_TEST_CURSOR_AGENT lets test harnesses point to a
            // shim in a temp directory without writing to real system paths (npm or the
            // official install directory).
            string? testOverride = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
            if (!String.IsNullOrEmpty(testOverride))
                return testOverride;

            // On Windows with the default command, prefer the official Cursor installer path
            // before falling through to PATH/npm. This prevents a stale npm shim from winning.
            if (OperatingSystem.IsWindows() &&
                String.Equals(_ExecutablePath, "cursor-agent", StringComparison.OrdinalIgnoreCase))
            {
                string? officialPath = GetWindowsOfficialInstallPath();
                if (!String.IsNullOrEmpty(officialPath) && File.Exists(officialPath))
                    return officialPath;
            }

            return ResolveConfiguredExecutable(_ExecutablePath);
        }

        /// <summary>
        /// Resolve the configured executable path. Virtual so tests can inject a stale
        /// npm-style fallback without touching user-global npm directories.
        /// </summary>
        protected virtual string ResolveConfiguredExecutable(string executablePath)
        {
            return ResolveExecutable(executablePath);
        }

        /// <summary>
        /// Returns the expected Windows official Cursor install command path, or null when
        /// LOCALAPPDATA is unavailable. Virtual so tests can inject a fake path without
        /// touching real system directories.
        /// </summary>
        protected virtual string? GetWindowsOfficialInstallPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrEmpty(localAppData))
                return null;
            return Path.Combine(localAppData, "cursor-agent", "cursor-agent.cmd");
        }

        /// <summary>
        /// Build Cursor agent CLI arguments. Uses --print as a boolean (current
        /// cursor-agent CLI semantics; older releases accepted -p as a
        /// flag-with-value, which silently failed to enable headless mode and
        /// caused --trust to be ignored). --trust skips the "Workspace Trust
        /// Required" prompt that would otherwise hang headless invocations against
        /// fresh temp directories. The prompt is NOT included here; it is written
        /// to stdin instead (see UsePromptStdin) to avoid the Windows cmd.exe
        /// ~8KB command-line length limit when long mission briefs are dispatched
        /// via cursor-agent.cmd.
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

            if (!String.IsNullOrEmpty(model))
            {
                args.Add("--model");
                args.Add(model);
            }

            // reasoningEffort from CaptainRuntimeOptions is validated and stored but not
            // forwarded: cursor-agent CLI v2026.04.29-c83a488 exposes no --thinking-effort /
            // --reasoning-effort flag. Wire this block when cursor-agent CLI gains the flag.

            args.Add("--force");
            args.Add("--trust");
            args.Add("--output-format");
            args.Add("text");

            return args;
        }

        /// <summary>
        /// Cursor agent reads the prompt from stdin when launched with --print
        /// and no positional prompt argument. Writing via stdin avoids the
        /// Windows cmd.exe ~8KB command-line length limit that causes
        /// cursor-agent.cmd to silently fail on long mission briefs.
        /// </summary>
        protected override bool UsePromptStdin => true;

        #endregion
    }
}
