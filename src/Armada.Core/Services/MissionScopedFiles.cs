namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// The file list a mission description scopes the mission to with a "Touch only", "Edit only" or "Modify only"
    /// directive, and the path forms the scope check compares.
    /// </summary>
    public static class MissionScopedFiles
    {
        private static readonly Regex _DirectiveRegex =
            new Regex(@"^\s*(?:Touch|Edit|Modify)\s+only\s+(?<files>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _TokenRegex =
            new Regex(
                @"(?<path>(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+|[A-Za-z0-9_.-]+\.(?:cs|csproj|sln|md|json|yaml|yml|ts|tsx|js|jsx|css|html|sh|bat))",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The normalized paths a description scopes the mission to, compared case-insensitively.
        /// </summary>
        /// <param name="description">Mission description.</param>
        /// <returns>The scoped paths; empty when the description has no directive.</returns>
        public static HashSet<string> Parse(string? description)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(description)) return files;

            foreach (Match directiveMatch in _DirectiveRegex.Matches(description))
            {
                foreach (Match pathMatch in _TokenRegex.Matches(directiveMatch.Groups["files"].Value))
                {
                    string normalized = NormalizeDirectivePath(pathMatch.Groups["path"].Value);
                    if (normalized.Length > 0) files.Add(normalized);
                }
            }
            return files;
        }

        /// <summary>
        /// A changed path in the form the scope check compares: trimmed, with forward slashes.
        /// </summary>
        /// <param name="path">Path reported by git.</param>
        /// <returns>The normalized path; empty when the path names nothing.</returns>
        public static string NormalizePath(string? path)
        {
            return (path ?? String.Empty).Trim().Replace('\\', '/');
        }

        /// <summary>
        /// A directive path in the form the scope check compares: a changed path's form without a leading "./" or a
        /// trailing period, which a sentence puts after the last path.
        /// </summary>
        /// <param name="path">Path written in the directive.</param>
        /// <returns>The normalized path; empty when nothing is left.</returns>
        public static string NormalizeDirectivePath(string? path)
        {
            string normalized = NormalizePath(path);
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            if (normalized.EndsWith(".", StringComparison.Ordinal)) normalized = normalized.Substring(0, normalized.Length - 1);
            return normalized;
        }
    }
}
