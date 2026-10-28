namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Builds the argument list for a headless <c>pi -p --mode json</c> invocation. Kept separate from the
    /// runtime adapter so the argument shape can be unit tested without launching a process, mirroring
    /// <see cref="OpenCodeCommandBuilder"/>.
    /// </summary>
    public static class PiCommandBuilder
    {
        #region Public-Members

        /// <summary>
        /// The built-in pi tool removed from the tool set under the Refuse permission policy.
        /// </summary>
        public const string ShellToolName = "bash";

        /// <summary>
        /// Start of the first line of <c>pi --help</c>, which identifies the pi coding agent. <c>pi --version</c>
        /// prints a bare version number, so it cannot tell pi apart from another command named <c>pi</c>.
        /// </summary>
        public const string HelpIdentityPrefix = "pi - ";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build arguments for <c>pi</c> in print mode with JSONL event output.
        /// </summary>
        /// <param name="workingDirectory">Directory pi runs in. pi has no directory flag; the runtime sets the
        /// process working directory, so the value is only validated here.</param>
        /// <param name="prompt">The message/prompt to send (validated; delivered on stdin by the runtime).</param>
        /// <param name="model">Optional <c>provider/id</c> model; omitted when null/blank.</param>
        /// <param name="thinkingLevel">Optional pi thinking level (<c>off</c> through <c>high</c>); omitted when null/blank.</param>
        /// <param name="refuseShell">When true, add <c>--exclude-tools bash</c> (the Refuse permission policy).</param>
        /// <param name="approveProject">When true, add <c>--approve</c> so the headless run trusts the project and
        /// loads its <c>.pi/mcp.json</c> (used for the Armada MCP binding).</param>
        /// <returns>The ordered argument list.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="workingDirectory"/> or <paramref name="prompt"/> is null or blank.</exception>
        public static List<string> BuildRunArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? thinkingLevel,
            bool refuseShell,
            bool approveProject)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (String.IsNullOrWhiteSpace(prompt)) throw new ArgumentNullException(nameof(prompt));

            List<string> args = new List<string>
            {
                "-p",
                "--mode",
                "json",
                "--no-session"
            };

            if (approveProject)
            {
                args.Add("--approve");
            }

            if (!String.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model!.Trim());
            }

            if (!String.IsNullOrWhiteSpace(thinkingLevel))
            {
                args.Add("--thinking");
                args.Add(thinkingLevel!.Trim());
            }

            if (refuseShell)
            {
                args.Add("--exclude-tools");
                args.Add(ShellToolName);
            }

            // The prompt is delivered on stdin by the runtime (PiRuntime.UsePromptStdin), not as a positional
            // argument: with no positional message 'pi -p' reads redirected stdin to EOF, and a multi-line
            // argument passed through an npm ".cmd" wrapper on Windows would be truncated at the first newline.
            return args;
        }

        /// <summary>
        /// Whether <c>--help</c> output comes from the pi coding agent: its first non-blank line starts with
        /// <see cref="HelpIdentityPrefix"/> (for example <c>pi - AI coding assistant ...</c>). Any other output
        /// means the command on PATH is not pi.
        /// </summary>
        /// <param name="helpOutput">Captured <c>--help</c> output, or null.</param>
        /// <returns>True when the output identifies pi.</returns>
        public static bool IsPiHelpOutput(string? helpOutput)
        {
            if (String.IsNullOrWhiteSpace(helpOutput)) return false;
            foreach (string raw in helpOutput!.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                return line.StartsWith(HelpIdentityPrefix, StringComparison.Ordinal);
            }

            return false;
        }

        #endregion
    }
}
