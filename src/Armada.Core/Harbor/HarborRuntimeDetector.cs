namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Decides which agent runtimes a Harbor can launch, so it advertises a runtime as available only when that runtime's
    /// CLI resolves on the PATH (claude, codex, gemini, cursor-agent, mux, opencode). The Admiral routes a captain only to
    /// a Harbor that has its runtime, so a Harbor without the CLI is skipped instead of failing every launch. Runtimes
    /// without a CLI (API endpoint, custom) are always available. The resolver is injectable for tests.
    /// </summary>
    public class HarborRuntimeDetector
    {
        #region Public-Members

        /// <summary>
        /// Resolves a command name to the full path of its executable, or null when it is not found. Defaults to a search
        /// of the process's PATH (<see cref="ResolveOnPath"/>).
        /// </summary>
        public Func<string, string?> Resolver
        {
            get => _Resolver;
            set => _Resolver = value ?? throw new ArgumentNullException(nameof(Resolver));
        }

        #endregion

        #region Private-Members

        private Func<string, string?> _Resolver = command => ResolveOnPath(command, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the default PATH resolver.
        /// </summary>
        public HarborRuntimeDetector()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The CLI command a runtime launches (as each runtime class names it), or null for a runtime without one.
        /// </summary>
        /// <param name="runtime">The runtime.</param>
        /// <returns>The command name, or null.</returns>
        public static string? CommandFor(AgentRuntimeEnum runtime)
        {
            switch (runtime)
            {
                case AgentRuntimeEnum.ClaudeCode: return "claude";
                case AgentRuntimeEnum.Codex: return "codex";
                case AgentRuntimeEnum.Gemini: return "gemini";
                case AgentRuntimeEnum.Cursor: return "cursor-agent";
                case AgentRuntimeEnum.Mux: return "mux";
                case AgentRuntimeEnum.OpenCode: return "opencode";
                default: return null;
            }
        }

        /// <summary>
        /// One capability per agent runtime: available when it has no CLI or its CLI resolves, with the resolved path (or
        /// what is missing) in <see cref="HarborCapability.Detail"/>.
        /// </summary>
        /// <returns>The capabilities, in runtime order.</returns>
        public List<HarborCapability> Detect()
        {
            List<HarborCapability> capabilities = new List<HarborCapability>();
            foreach (AgentRuntimeEnum runtime in Enum.GetValues<AgentRuntimeEnum>())
            {
                string? command = CommandFor(runtime);
                if (command == null)
                {
                    capabilities.Add(new HarborCapability { Name = runtime.ToString(), Available = true });
                    continue;
                }

                string? resolved = null;
                try { resolved = _Resolver(command); }
                catch { resolved = null; }

                capabilities.Add(new HarborCapability
                {
                    Name = runtime.ToString(),
                    Available = !String.IsNullOrEmpty(resolved),
                    Detail = !String.IsNullOrEmpty(resolved) ? resolved : "'" + command + "' was not found on PATH"
                });
            }

            return capabilities;
        }

        /// <summary>
        /// Find a command on a PATH: a name with a directory part is checked as given; otherwise each PATH entry is tried,
        /// with the PATHEXT extensions on Windows (and the npm global folder in %APPDATA%, where npm installs CLIs).
        /// </summary>
        /// <param name="command">Command name or path.</param>
        /// <param name="path">The PATH to search, or null.</param>
        /// <param name="isWindows">Whether to apply Windows executable extensions.</param>
        /// <returns>The full path, or null when not found.</returns>
        public static string? ResolveOnPath(string command, string? path, bool isWindows)
        {
            if (String.IsNullOrWhiteSpace(command)) return null;

            List<string> extensions = new List<string> { String.Empty };
            if (isWindows)
            {
                string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
                foreach (string extension in pathExt.Split(';'))
                    if (!String.IsNullOrWhiteSpace(extension)) extensions.Add(extension.Trim());
            }

            if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
                return FindWithExtensions(command, extensions, isWindows);

            List<string> directories = new List<string>();
            if (!String.IsNullOrEmpty(path))
            {
                foreach (string entry in path.Split(Path.PathSeparator))
                    if (!String.IsNullOrWhiteSpace(entry)) directories.Add(entry.Trim().Trim('"'));
            }

            if (isWindows)
                directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));

            foreach (string directory in directories)
            {
                string? found = FindWithExtensions(Path.Combine(directory, command), extensions, isWindows);
                if (found != null) return found;
            }

            return null;
        }

        #endregion

        #region Private-Methods

        private static string? FindWithExtensions(string candidate, List<string> extensions, bool isWindows)
        {
            foreach (string extension in extensions)
            {
                string full = candidate + extension;
                try
                {
                    if (!File.Exists(full) || Directory.Exists(full)) continue;
                    if (isWindows || OperatingSystem.IsWindows()) return full;
                    UnixFileMode mode = File.GetUnixFileMode(full);
                    if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) return full;
                }
                catch
                {
                }
            }

            return null;
        }

        #endregion
    }
}
