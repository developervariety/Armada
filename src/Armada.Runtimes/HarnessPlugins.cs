namespace Armada.Runtimes
{
    using System;
    using System.IO;

    /// <summary>
    /// The harness plugins Armada ships beside its own binaries, and where each runtime finds them. A plugin
    /// is delivered only when its files are actually present, so a build that omits them launches captains
    /// exactly as before rather than pointing a harness at a path that does not exist.
    /// </summary>
    public static class HarnessPlugins
    {
        #region Public-Members

        /// <summary>The environment switch Claude Code requires before it runs a plugin's function hooks.
        /// Without it the plugin loads, its commands register and its hooks validate, and the hook module
        /// never executes, with no error or log line.</summary>
        public const string ClaudeCodeFunctionHooksVariable = "CLAUDE_CODE_ENABLE_FUNCTION_HOOKS";

        /// <summary>The directory the shipped plugins live under. Defaults to the running binary's own
        /// directory; a test points it elsewhere.</summary>
        public static string Root
        {
            get => _Root ?? Path.Combine(AppContext.BaseDirectory, "Plugins");
            set => _Root = value;
        }

        /// <summary>The Claude Code context-compaction plugin directory, or null when it is not shipped.</summary>
        public static string? ClaudeCodeContextCompaction
        {
            get
            {
                string directory = Path.Combine(Root, "claude-code", "armada-context-compaction");
                return File.Exists(Path.Combine(directory, ".claude-plugin", "plugin.json")) ? directory : null;
            }
        }

        /// <summary>The OpenCode context-compaction plugin module, or null when it is not shipped.</summary>
        public static string? OpenCodeContextCompaction
        {
            get
            {
                string file = Path.Combine(Root, "opencode", "armada-context-compaction.js");
                return File.Exists(file) ? file : null;
            }
        }

        #endregion

        #region Private-Members

        private static string? _Root;

        #endregion
    }
}
