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

        // A constraint sentence that forbids changes: a negation and a change verb.
        private static readonly Regex _Forbids = new Regex(
            @"\b(?:no|not|never|nothing|without|must\s+not|do\s+not|don't)\b.*?\b(?:chang\w*|edit\w*|modif\w*|touch\w*|writ\w*|updat\w*)\b"
            + @"|\b(?:chang\w*|edit\w*|modif\w*|touch\w*|writ\w*|updat\w*)\b.*?\b(?:forbidden|not\s+allowed|out\s+of\s+scope)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // A gerund change statement inside NonGoals is an exclusion, even without a negation.
        private static readonly Regex _NonGoalChange = new Regex(
            @"^\s*(?:changing|editing|modifying|touching|writing|updating)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _CommitPathAnnotation = new Regex(
            @"\(\s*(?:comments\s+only|description\s+and\s+message\s+only)\s*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _ExclusiveCommitScope = new Regex(
            @"^\s*No\s+stage\s+commits\s+outside\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // A constraint sentence that allows only the files it names.
        private static readonly Regex _AllowsOnly = new Regex(
            @"\b(?:only|allow\w*\s+only|limited\s+to|restricted\s+to)\b.*?\b(?:chang\w*|edit\w*|modif\w*|touch\w*|writ\w*|file\w*)\b"
            + @"|\b(?:chang\w*|edit\w*|modif\w*|touch\w*|writ\w*|file\w*)\b.*?\b(?:only|limited\s+to|restricted\s+to)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

        /// <summary>
        /// The gate-input entries an objective's constraint text keeps out of the change. A sentence that forbids
        /// changes ("No src/ or docs/ file changes", "Do not edit docs/guide.md") excludes each entry it names or
        /// whose directory it names; a sentence that allows only the files it names ("Change only the two test
        /// files a.cs and b.cs") excludes each entry it does not name. A sentence that names no path excludes nothing.
        /// </summary>
        /// <param name="paths">The resolved profile's gate-input entries.</param>
        /// <param name="constraintTexts">The objective's rollout constraints.</param>
        /// <param name="nonGoalTexts">The objective's non-goals, whose change statements are exclusions.</param>
        /// <returns>The excluded entries, in declared order; empty when none is excluded.</returns>
        public static List<string> FindExcluded(IEnumerable<string>? paths, IEnumerable<string?>? constraintTexts, IEnumerable<string?>? nonGoalTexts)
        {
            List<string> entries = Normalize(paths);
            List<string> excluded = new List<string>();
            if (entries.Count == 0) return excluded;

            List<List<string>> forbidding = new List<List<string>>();
            List<List<string>> allowing = new List<List<string>>();
            IEnumerable<(string? Text, bool IsNonGoal)> constraints = (constraintTexts ?? Enumerable.Empty<string?>())
                .Select(text => (text, false))
                .Concat((nonGoalTexts ?? Enumerable.Empty<string?>()).Select(text => (text, true)));
            foreach (var constraint in constraints)
            {
                string? text = constraint.Text;
                if (String.IsNullOrWhiteSpace(text)) continue;
                foreach (string sentence in text.Split(new[] { ". ", "; ", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    List<string> tokens = PathTokens(sentence);
                    if (tokens.Count == 0) continue;
                    // This path-only allow-list must precede generic negation matching: a file
                    // named CHANGELOG.md is not a change verb that forbids its own allow-list.
                    if (IsExclusiveCommitAllowList(sentence, tokens)) allowing.Add(tokens);
                    else if (_Forbids.IsMatch(sentence) || (constraint.IsNonGoal && _NonGoalChange.IsMatch(sentence))) forbidding.Add(tokens);
                    else if (_AllowsOnly.IsMatch(sentence)) allowing.Add(tokens);
                }
            }

            foreach (string entry in entries)
            {
                bool forbidden = forbidding.Any(tokens => tokens.Any(token => Covers(token, entry)));
                bool outsideAllowList = allowing.Any(tokens => !tokens.Any(token => Covers(token, entry) || Names(token, entry)));
                if (forbidden || outsideAllowList) excluded.Add(entry);
            }
            return excluded;
        }

        #endregion

        #region Private-Methods

        // Path-shaped tokens of a sentence: a token with a slash or a file extension.
        private static bool IsExclusiveCommitAllowList(string sentence, List<string> paths)
        {
            Match scope = _ExclusiveCommitScope.Match(sentence);
            if (!scope.Success) return false;
            // Conditional prose and further directives are not this narrow path-list grammar.
            string pathList = _CommitPathAnnotation.Replace(sentence.Substring(scope.Length), String.Empty);
            return _PathToken.Matches(pathList).Cast<Match>().All(match =>
            {
                string token = NormalizeEntry(match.Value.TrimEnd('.', ','));
                return token.Length == 0 || token.Equals("and", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("or", StringComparison.OrdinalIgnoreCase) || paths.Contains(token, StringComparer.OrdinalIgnoreCase);
            });
        }

        private static List<string> PathTokens(string sentence)
        {
            List<string> tokens = new List<string>();
            foreach (Match match in _PathToken.Matches(sentence))
            {
                string token = NormalizeEntry(match.Value.TrimEnd('.', ','));
                if (token.Length < 2) continue;
                bool hasSlash = token.Contains('/');
                int dot = token.LastIndexOf('.');
                bool hasExtension = dot >= 2 && dot < token.Length - 1 && Char.IsLetter(token[dot + 1]);
                if (hasSlash || hasExtension) tokens.Add(token);
            }
            return tokens;
        }

        // A constraint token covers an entry when it is the entry, a directory that holds it, or a glob it matches.
        private static bool Covers(string token, string entry)
        {
            if (String.Equals(token, entry, StringComparison.OrdinalIgnoreCase)) return true;
            string directory = token.EndsWith("/", StringComparison.Ordinal) ? token : token + "/";
            if (entry.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) return true;
            Matcher matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(token);
            return matcher.Match(entry).HasMatches;
        }

        // An entry that is itself a glob is named by a token it matches.
        private static bool Names(string token, string entry)
        {
            Matcher matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(entry);
            return matcher.Match(token).HasMatches;
        }

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
