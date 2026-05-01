namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Microsoft.Extensions.FileSystemGlobbing;

    /// <summary>
    /// Validates a captain's changed-file set against a vessel's protected glob list.
    /// Used by the orchestrator at the entry of mission landing to block direct
    /// captain edits to paths that must be curated through a structured proposal
    /// flow instead (e.g. CLAUDE.md, briefing material, skill libraries).
    /// </summary>
    public static class ProtectedPathsValidator
    {
        #region Public-Members

        /// <summary>
        /// Armada-owned paths that must never land into a vessel target branch.
        /// These are generated or briefing artifacts, not product changes.
        /// </summary>
        public static readonly IReadOnlyList<string> BuiltInProtectedPaths = new List<string>
        {
            "**/CLAUDE.md",
            ".armada/instructions/**",
            "_briefing/**",
            "**/_briefing/**"
        }.AsReadOnly();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Find the first changed file path that matches either Armada's built-in
        /// protected paths or the vessel's configured protected paths.
        /// </summary>
        /// <param name="changedFilePaths">Repository-relative paths the captain modified.</param>
        /// <param name="protectedPaths">Optional vessel-specific glob patterns.</param>
        /// <returns>The first offending path, or null when there is no violation.</returns>
        public static string? FindFirstBuiltInOrConfiguredViolation(
            IEnumerable<string>? changedFilePaths,
            IList<string>? protectedPaths)
        {
            List<string> effective = BuildEffectiveProtectedPaths(protectedPaths);
            return FindFirstViolation(changedFilePaths, effective);
        }

        /// <summary>
        /// Find the first changed file path that matches any glob in <paramref name="protectedPaths"/>.
        /// Returns null when nothing is protected, when no changed files were supplied,
        /// or when no path matches any pattern.
        /// </summary>
        /// <param name="changedFilePaths">Repository-relative paths the captain modified.</param>
        /// <param name="protectedPaths">Glob patterns to enforce. Null or empty = no protection.</param>
        /// <returns>The first offending path, or null when there is no violation.</returns>
        public static string? FindFirstViolation(
            IEnumerable<string>? changedFilePaths,
            IList<string>? protectedPaths)
        {
            if (protectedPaths == null || protectedPaths.Count == 0) return null;
            if (changedFilePaths == null) return null;

            Matcher matcher = BuildMatcher(protectedPaths);
            if (matcher == null) return null;

            foreach (string changed in changedFilePaths)
            {
                string normalized = NormalizePath(changed);
                if (String.IsNullOrEmpty(normalized)) continue;

                PatternMatchingResult result = matcher.Match(normalized);
                if (result.HasMatches)
                {
                    return normalized;
                }
            }

            return null;
        }

        /// <summary>
        /// Format the failure reason persisted on the mission and shown to the captain.
        /// The literal token <c>[CLAUDE.MD-PROPOSAL]</c> is included so captains learn
        /// the structured-proposal convention from a single failure.
        /// </summary>
        /// <param name="matchedPath">The first offending path returned by FindFirstViolation.</param>
        /// <param name="vesselName">The vessel's display name.</param>
        /// <returns>Failure-reason string suitable for Mission.FailureReason.</returns>
        public static string FormatFailureReason(string matchedPath, string vesselName)
        {
            return "Captain modified protected path '" + (matchedPath ?? "") +
                "' on vessel '" + (vesselName ?? "") +
                "'. Use a [CLAUDE.MD-PROPOSAL] block in your final response to propose changes (target / action / text / why) -- the orchestrator decides what lands.";
        }

        /// <summary>
        /// Parse repository-relative file paths out of a unified git-diff snapshot.
        /// Handles standard "diff --git a/path b/path" headers and is tolerant of
        /// quoted paths and renames. Returns an empty list on null/empty input.
        /// </summary>
        /// <param name="diffSnapshot">Captured unified diff text, or null.</param>
        /// <returns>Distinct repository-relative paths referenced by the diff.</returns>
        public static IReadOnlyList<string> ExtractChangedFilesFromDiff(string? diffSnapshot)
        {
            List<string> results = new List<string>();
            if (String.IsNullOrEmpty(diffSnapshot)) return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Walk lines and collect both sides of each "diff --git" header. Using both
            // sides catches renames where the a/ and b/ paths differ.
            string[] lines = diffSnapshot.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');
                if (!line.StartsWith("diff --git ", StringComparison.Ordinal)) continue;

                string remainder = line.Substring("diff --git ".Length).Trim();
                if (String.IsNullOrEmpty(remainder)) continue;

                // Split into the two path tokens. Handle quoted paths with spaces.
                List<string> tokens = SplitDiffHeaderPaths(remainder);
                foreach (string token in tokens)
                {
                    string stripped = StripDiffPrefix(token);
                    string normalized = NormalizePath(stripped);
                    if (String.IsNullOrEmpty(normalized)) continue;
                    if (seen.Add(normalized))
                    {
                        results.Add(normalized);
                    }
                }
            }

            return results;
        }

        #endregion

        #region Private-Methods

        private static List<string> BuildEffectiveProtectedPaths(IList<string>? protectedPaths)
        {
            List<string> effective = new List<string>();
            foreach (string builtIn in BuiltInProtectedPaths)
            {
                effective.Add(builtIn);
            }

            if (protectedPaths != null)
            {
                foreach (string configured in protectedPaths)
                {
                    if (!String.IsNullOrWhiteSpace(configured))
                    {
                        effective.Add(configured);
                    }
                }
            }

            return effective;
        }

        private static Matcher BuildMatcher(IList<string> patterns)
        {
            Matcher matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            int added = 0;
            foreach (string pattern in patterns)
            {
                if (String.IsNullOrWhiteSpace(pattern)) continue;
                matcher.AddInclude(pattern.Trim());
                added++;
            }
            return added > 0 ? matcher : null!;
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrEmpty(path)) return "";
            string trimmed = path.Trim().Replace('\\', '/');
            while (trimmed.StartsWith("./", StringComparison.Ordinal)) trimmed = trimmed.Substring(2);
            while (trimmed.StartsWith("/", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);
            return trimmed;
        }

        private static List<string> SplitDiffHeaderPaths(string remainder)
        {
            // Header forms:
            //   diff --git a/foo b/bar
            //   diff --git "a/foo bar" "b/baz qux"
            // Split on whitespace but respect double-quoted segments.
            List<string> tokens = new List<string>();
            int i = 0;
            int len = remainder.Length;
            while (i < len)
            {
                while (i < len && Char.IsWhiteSpace(remainder[i])) i++;
                if (i >= len) break;

                int start = i;
                if (remainder[i] == '"')
                {
                    i++;
                    while (i < len && remainder[i] != '"')
                    {
                        if (remainder[i] == '\\' && i + 1 < len) i++;
                        i++;
                    }
                    if (i < len) i++; // consume closing quote
                    string quoted = remainder.Substring(start, i - start).Trim('"');
                    tokens.Add(quoted);
                }
                else
                {
                    while (i < len && !Char.IsWhiteSpace(remainder[i])) i++;
                    tokens.Add(remainder.Substring(start, i - start));
                }
            }
            return tokens;
        }

        private static string StripDiffPrefix(string token)
        {
            if (String.IsNullOrEmpty(token)) return "";
            if (token.StartsWith("a/", StringComparison.Ordinal) ||
                token.StartsWith("b/", StringComparison.Ordinal))
            {
                return token.Substring(2);
            }
            return token;
        }

        #endregion
    }
}
