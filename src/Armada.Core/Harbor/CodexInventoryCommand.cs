namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The only Codex process a Harbor host may start for a tool inventory.
    /// The text form is <c>list</c> or <c>get</c> plus one server name. It never carries a shell command.
    /// </summary>
    public static class CodexInventoryCommand
    {
        /// <summary>Parse an allowlisted inventory command into Codex arguments.</summary>
        /// <param name="text">Command text from the workspace request.</param>
        /// <param name="arguments">Codex arguments when the text is allowlisted.</param>
        /// <returns>True when the text is an inventory list or a single-server read.</returns>
        public static bool TryParse(string? text, out IReadOnlyList<string> arguments)
        {
            arguments = Array.Empty<string>();
            if (String.Equals(text, "list", StringComparison.Ordinal))
            {
                arguments = new[] { "mcp", "list", "--json" };
                return true;
            }

            const string getPrefix = "get ";
            if (text == null || !text.StartsWith(getPrefix, StringComparison.Ordinal)) return false;
            string name = text.Substring(getPrefix.Length);
            if (!IsServerName(name)) return false;
            arguments = new[] { "mcp", "get", name, "--json" };
            return true;
        }

        /// <summary>Encode allowlisted Codex arguments as inventory command text.</summary>
        /// <param name="arguments">Arguments the local probe would pass to Codex.</param>
        /// <returns>The command text, or null when the arguments are not an inventory read.</returns>
        public static string? TextFor(IReadOnlyList<string> arguments)
        {
            if (arguments == null) return null;
            if (arguments.Count == 3
                && arguments[0] == "mcp"
                && arguments[1] == "list"
                && arguments[2] == "--json")
                return "list";
            if (arguments.Count == 4
                && arguments[0] == "mcp"
                && arguments[1] == "get"
                && arguments[3] == "--json"
                && IsServerName(arguments[2]))
                return "get " + arguments[2];
            return null;
        }

        /// <summary>Codex executable name for the host that runs the probe.</summary>
        /// <returns><c>codex.cmd</c> on Windows and <c>codex</c> elsewhere.</returns>
        public static string ExecutableName() => OperatingSystem.IsWindows() ? "codex.cmd" : "codex";

        private static bool IsServerName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 200) return false;
            foreach (char character in name)
            {
                bool allowed = character is (>= 'A' and <= 'Z')
                    or (>= 'a' and <= 'z')
                    or (>= '0' and <= '9')
                    or '_' or '-' or '.' or ':';
                if (!allowed) return false;
            }
            return true;
        }
    }
}
