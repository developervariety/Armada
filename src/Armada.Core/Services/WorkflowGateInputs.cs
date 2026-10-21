namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Microsoft.Extensions.FileSystemGlobbing;

    /// <summary>
    /// The rule for a workflow profile's <c>GateInputPaths</c>: repository-relative paths or glob patterns that the
    /// profile's required checks read. A profile save validates and normalizes them, an Architect brief names them,
    /// and an Architect plan is checked against them, so all three apply the same list. Armada never derives the
    /// list from command text; an operator declares it.
    /// </summary>
    public static class WorkflowGateInputs
    {
        #region Public-Members

        /// <summary>
        /// Most gate-input entries one profile may declare.
        /// </summary>
        public const int MaxEntries = 50;

        /// <summary>
        /// Longest gate-input entry, in characters.
        /// </summary>
        public const int MaxEntryLength = 260;

        /// <summary>
        /// Event type recorded when an Architect plan assigns none of the files a declared gate input names.
        /// </summary>
        public const string PlanMissingEventType = "mission.architect_plan_gate_inputs_missing";

        #endregion

        #region Private-Members

        private static readonly Regex _PathToken = new Regex(@"[A-Za-z0-9_\-./\\*]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Trim each entry, use forward slashes, drop blanks and leading "./", and remove duplicates in order.
        /// </summary>
        /// <param name="paths">Entries as submitted.</param>
        /// <returns>The normalized list; empty when none remain.</returns>
        public static List<string> Normalize(IEnumerable<string>? paths)
        {
            List<string> normalized = new List<string>();
            if (paths == null) return normalized;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string entry = NormalizeEntry(path);
                if (entry.Length == 0) continue;
                if (seen.Add(entry)) normalized.Add(entry);
            }
            return normalized;
        }

        /// <summary>
        /// Validate the entries as a profile save stores them. An entry must be repository-relative: not rooted,
        /// no drive letter, and no ".." segment.
        /// </summary>
        /// <param name="paths">Entries as submitted.</param>
        /// <returns>Null when every entry is valid or there are none; otherwise the named error.</returns>
        public static string? Validate(IEnumerable<string>? paths)
        {
            List<string> entries = Normalize(paths);
            if (entries.Count > MaxEntries)
                return "gateInputPaths may declare at most " + MaxEntries + " entries; it declares " + entries.Count + ".";
            foreach (string entry in entries)
            {
                if (entry.Length > MaxEntryLength)
                    return "gateInputPaths entry is longer than " + MaxEntryLength + " characters: " + entry.Substring(0, 40) + "...";
                if (entry.StartsWith("/", StringComparison.Ordinal) || (entry.Length > 1 && entry[1] == ':'))
                    return "gateInputPaths entry must be repository-relative: " + entry;
                if (entry.Split('/').Any(segment => segment == ".."))
                    return "gateInputPaths entry must not contain a '..' segment: " + entry;
            }
            return null;
        }

        /// <summary>
        /// The Architect brief section that names the declared gate inputs, or an empty string when there are none.
        /// </summary>
        /// <param name="paths">The resolved profile's gate-input entries.</param>
        /// <returns>The section text.</returns>
        public static string BuildArchitectSection(IEnumerable<string>? paths)
        {
            List<string> entries = Normalize(paths);
            if (entries.Count == 0) return String.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("## Gate Input Files\n\n");
            sb.Append("The required checks on this vessel read these files. When the planned change touches what one of them ");
            sb.Append("describes, assign that file to the mission that makes the change and list its path in that mission's ");
            sb.Append("description, or the mission fails its gate on a file it was not allowed to edit:\n");
            foreach (string entry in entries) sb.Append("- `").Append(entry).Append("`\n");
            sb.Append("A plan that names none of these files is flagged for the operator.\n");
            return sb.ToString();
        }

        /// <summary>
        /// The gate-input entries that no planned mission names. An entry counts as named when a path-shaped token
        /// in a planned mission's title or description equals it or matches it as a glob pattern.
        /// </summary>
        /// <param name="paths">The resolved profile's gate-input entries.</param>
        /// <param name="planTexts">Title and description of every planned mission.</param>
        /// <returns>The entries no planned mission names, in declared order; empty when all are named.</returns>
        public static List<string> FindUnplanned(IEnumerable<string>? paths, IEnumerable<string?>? planTexts)
        {
            List<string> entries = Normalize(paths);
            List<string> unplanned = new List<string>();
            if (entries.Count == 0) return unplanned;

            HashSet<string> tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (planTexts != null)
            {
                foreach (string? text in planTexts)
                {
                    if (String.IsNullOrWhiteSpace(text)) continue;
                    foreach (Match match in _PathToken.Matches(text))
                    {
                        string token = NormalizeEntry(match.Value.TrimEnd('.', ','));
                        if (token.Length > 0) tokens.Add(token);
                    }
                }
            }

            foreach (string entry in entries)
            {
                if (tokens.Contains(entry)) continue;
                Matcher matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(entry);
                bool named = tokens.Any(token => matcher.Match(token).HasMatches);
                if (!named) unplanned.Add(entry);
            }
            return unplanned;
        }

        #endregion

        #region Private-Methods

        private static string NormalizeEntry(string? path)
        {
            if (String.IsNullOrWhiteSpace(path)) return String.Empty;
            string entry = path.Trim().Replace('\\', '/');
            while (entry.StartsWith("./", StringComparison.Ordinal)) entry = entry.Substring(2);
            return entry;
        }

        #endregion
    }
}
