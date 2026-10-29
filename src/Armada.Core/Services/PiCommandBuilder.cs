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
        /// pi's built-in Bash tool, removed from the tool set under the Refuse permission policy.
        /// </summary>
        public const string ShellToolName = "bash";

        /// <summary>
        /// pi's optional built-in PowerShell tool (native Windows only), removed under the Refuse permission policy.
        /// </summary>
        public const string PowerShellToolName = "powershell";

        /// <summary>
        /// Every built-in pi tool that runs shell commands, removed together under the Refuse permission policy so a
        /// Windows captain that enables pi's <c>powershell</c> tool cannot run commands either. pi's other built-in
        /// tools (<c>read</c>, <c>edit</c>, <c>write</c>, <c>grep</c>, <c>find</c>, <c>ls</c>) do not execute commands, and
        /// <c>codemode</c> scripts reach commands only through these tools.
        /// </summary>
        public static readonly IReadOnlyList<string> ShellToolNames = new List<string> { ShellToolName, PowerShellToolName };

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
        /// <param name="refuseShell">When true, add <c>--exclude-tools bash,powershell</c> (the Refuse permission policy).</param>
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
            return BuildRunArguments(workingDirectory, prompt, model, thinkingLevel, refuseShell, approveProject, null, null);
        }

        /// <summary>
        /// Build arguments for <c>pi</c> in print mode with JSONL event output, optionally loading Armada's permission
        /// gate extension (Approve in Armada) and passing the prompt as an <c>@file</c> argument instead of on stdin.
        /// </summary>
        /// <param name="workingDirectory">Directory pi runs in (validated only; the runtime sets the process working
        /// directory).</param>
        /// <param name="prompt">The message/prompt to send (validated; delivered on stdin unless
        /// <paramref name="promptFilePath"/> is set).</param>
        /// <param name="model">Optional <c>provider/id</c> model; omitted when null/blank.</param>
        /// <param name="thinkingLevel">Optional pi thinking level; omitted when null/blank.</param>
        /// <param name="refuseShell">When true, add <c>--exclude-tools bash,powershell</c> (the Refuse permission
        /// policy). Ignored when <paramref name="permissionGateExtensionPath"/> is set: the gate asks Armada instead.</param>
        /// <param name="approveProject">When true, add <c>--approve</c> (trust the project's <c>.pi/mcp.json</c>).</param>
        /// <param name="permissionGateExtensionPath">Absolute path of <c>armada-permission-gate.ts</c>, added as
        /// <c>--extension &lt;path&gt;</c> (Approve in Armada), or null.</param>
        /// <param name="promptFilePath">Path of a file holding the prompt, passed last as <c>@&lt;path&gt;</c> (the Windows
        /// prompt delivery), or null to deliver the prompt on stdin.</param>
        /// <returns>The ordered argument list.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="workingDirectory"/> or <paramref name="prompt"/> is null or blank.</exception>
        public static List<string> BuildRunArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? thinkingLevel,
            bool refuseShell,
            bool approveProject,
            string? permissionGateExtensionPath,
            string? promptFilePath)
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

            if (!String.IsNullOrWhiteSpace(permissionGateExtensionPath))
            {
                args.Add("--extension");
                args.Add(permissionGateExtensionPath!);
            }
            else if (refuseShell)
            {
                args.Add("--exclude-tools");
                args.Add(String.Join(",", ShellToolNames));
            }

            // By default the prompt is delivered on stdin by the runtime (PiRuntime.UsePromptStdin), not as a
            // positional argument: with no positional message 'pi -p' reads redirected stdin to EOF, and a multi-line
            // argument passed through an npm ".cmd" wrapper on Windows would be truncated at the first newline. The
            // @file form (the Windows path) keeps the prompt text out of argv as well: pi reads the file into the first
            // message.
            if (!String.IsNullOrWhiteSpace(promptFilePath))
            {
                args.Add("@" + promptFilePath!);
            }

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
