namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    /// <summary>
    /// A shim Mux CLI: prints a fixed list of jsonl lines on stdout, optionally commits a file in its working directory
    /// first (as a captain does its work), optionally writes a final message to the --output-last-message file, and exits
    /// with a chosen code (mux exits 1 or 2 after error events). The lines are read from a file next to the script, so they
    /// may contain any characters. Shell script on Unix, .cmd file on Windows.
    /// </summary>
    public static class MuxShimCli
    {
        #region Public-Methods

        /// <summary>
        /// Write a shim.
        /// </summary>
        /// <param name="directory">Directory to write the shim into.</param>
        /// <param name="lines">Stdout lines.</param>
        /// <param name="exitCode">Exit code.</param>
        /// <param name="finalMessage">Text written to the --output-last-message file (one ASCII line), or null.</param>
        /// <param name="commitFile">File to create and commit in the working directory before printing, or null.</param>
        /// <returns>Path of the shim executable.</returns>
        public static string Write(string directory, List<string> lines, int exitCode, string? finalMessage, string? commitFile)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            Directory.CreateDirectory(directory);
            string linesFile = Path.Combine(directory, "mux-lines.jsonl");
            File.WriteAllText(linesFile, String.Join("\n", lines) + "\n");
            return OperatingSystem.IsWindows()
                ? WriteCmd(directory, linesFile, exitCode, finalMessage, commitFile)
                : WriteSh(directory, linesFile, exitCode, finalMessage, commitFile);
        }

        #endregion

        #region Private-Methods

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static string WriteSh(string directory, string linesFile, int exitCode, string? finalMessage, string? commitFile)
        {
            StringBuilder script = new StringBuilder();
            script.Append("#!/usr/bin/env sh\n");
            script.Append("prev=\"\"\n");
            script.Append("out=\"\"\n");
            script.Append("for a in \"$@\"; do\n");
            script.Append("  if [ \"$prev\" = \"--output-last-message\" ]; then out=\"$a\"; fi\n");
            script.Append("  prev=\"$a\"\n");
            script.Append("done\n");
            if (commitFile != null)
            {
                script.Append("printf 'captain work\\n' > '" + commitFile + "'\n");
                script.Append("git add '" + commitFile + "' >/dev/null 2>&1\n");
                script.Append("git -c user.name=Captain -c user.email=captain@example.com commit -q -m 'Captain work' >/dev/null 2>&1\n");
            }

            script.Append("cat '" + linesFile + "'\n");
            if (finalMessage != null) script.Append("if [ -n \"$out\" ]; then printf '%s' '" + finalMessage + "' > \"$out\"; fi\n");
            script.Append("exit " + exitCode + "\n");

            string path = Path.Combine(directory, "mux");
            File.WriteAllText(path, script.ToString());
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return path;
        }

        private static string WriteCmd(string directory, string linesFile, int exitCode, string? finalMessage, string? commitFile)
        {
            StringBuilder script = new StringBuilder();
            script.Append("@echo off\r\n");
            script.Append("setlocal EnableExtensions\r\n");
            script.Append("set \"PREV=\"\r\n");
            script.Append("set \"OUT=\"\r\n");
            script.Append(":args\r\n");
            script.Append("if \"%~1\"==\"\" goto argsdone\r\n");
            script.Append("if \"%PREV%\"==\"--output-last-message\" set \"OUT=%~1\"\r\n");
            script.Append("set \"PREV=%~1\"\r\n");
            script.Append("shift\r\n");
            script.Append("goto args\r\n");
            script.Append(":argsdone\r\n");
            if (commitFile != null)
            {
                script.Append(">\"" + commitFile + "\" echo captain work\r\n");
                script.Append("git add \"" + commitFile + "\" >nul 2>&1\r\n");
                script.Append("git -c user.name=Captain -c user.email=captain@example.com commit -q -m \"Captain work\" >nul 2>&1\r\n");
            }

            script.Append("type \"" + linesFile + "\"\r\n");
            if (finalMessage != null) script.Append("if not \"%OUT%\"==\"\" (>\"%OUT%\" echo " + finalMessage + ")\r\n");
            script.Append("exit /b " + exitCode + "\r\n");

            string path = Path.Combine(directory, "mux.cmd");
            File.WriteAllText(path, script.ToString());
            return path;
        }

        #endregion
    }
}
