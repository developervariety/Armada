namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Pulls the searchable subjects out of a mission's title and description: the repository paths
    /// it names, and the code identifiers it names. These become the paths whose history the brief
    /// anchors, and the terms the brief reports prior art for.
    ///
    /// Extraction is deterministic and does no git work, so the selection a brief was built from can
    /// be reproduced from the mission text alone.
    /// </summary>
    public static class MissionSubjectExtractor
    {
        #region Public-Members

        /// <summary>
        /// Maximum paths returned. A mission that names more than this has more subjects than a
        /// single brief can anchor without crowding out the source the captain came to read.
        /// </summary>
        internal const int MaxPaths = 8;

        /// <summary>
        /// Maximum identifier terms returned.
        /// </summary>
        internal const int MaxTerms = 6;

        /// <summary>
        /// Shortest identifier treated as a subject. Shorter tokens match too much to be worth a
        /// search line.
        /// </summary>
        internal const int MinTermLength = 6;

        #endregion

        #region Private-Members

        private static readonly Regex _PathPattern = new Regex(
            @"(?<![\w/.])(?:[\w.\-]+/)+[\w.\-]+\.[A-Za-z0-9]{1,6}(?![\w/])",
            RegexOptions.Compiled);

        private static readonly Regex _IdentifierPattern = new Regex(
            @"\b[A-Z][A-Za-z0-9]*(?:[A-Z][A-Za-z0-9]*)+\b",
            RegexOptions.Compiled);

        // Armada's own record ids never appear in vessel source, so searching for one always returns
        // the empty result and spends a line of the brief saying so.
        private static readonly Regex _ArmadaIdPattern = new Regex(
            @"^(flt|vsl|cpt|msn|vyg|dck|sig|art|obj|rbx|chk|inc)_",
            RegexOptions.Compiled);

        // Directory names that only ever occur in a sibling read-only source tree: a deobfuscator's
        // decompiled or decrypted output, or an extractor's export. A mission cites those trees as
        // the SOURCE it ports from, never as a path it may create, so an absent one is not new work.
        private static readonly string[] _ExternalSourceTreeMarkers = new string[]
        {
            "decompiled-src",
            "decompiled-src-clean",
            "decrypted-xml",
            "decrypted-db",
            "-export"
        };

        // Words that are PascalCase in prose but name nothing in a repository.
        private static readonly HashSet<string> _StopTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ACTIONABLE", "OUTPUT", "README", "TODO", "NOTE", "WARNING", "IMPORTANT",
            "ArmadaResult", "NEEDS", "REVISION", "COMPLETE", "PASS", "FAIL"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Extract repository-relative paths named in the supplied mission text, in first-appearance
        /// order, de-duplicated, and capped at <see cref="MaxPaths"/>.
        /// </summary>
        /// <param name="missionText">Mission title and description, concatenated by the caller.</param>
        /// <returns>Paths named by the mission; empty when none are found.</returns>
        public static List<string> ExtractPaths(string? missionText)
        {
            List<string> results = new List<string>();
            if (String.IsNullOrWhiteSpace(missionText)) return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // A mission description that embeds a prior-stage diff (the handoff path pastes one) carries
            // `diff --git a/... b/...`, `--- a/...`, and `+++ b/...` headers. In that text an `a/` or `b/`
            // prefix is a git old/new marker, not part of the repository path, so anchoring it as-is makes
            // the anchors block report a live file as missing. Only strip the prefix when a diff is embedded:
            // a repo that genuinely owns a top-level `a/` or `b/` folder must keep those paths intact.
            bool embedsDiff = missionText.IndexOf("diff --git ", StringComparison.Ordinal) >= 0;

            foreach (Match match in _PathPattern.Matches(missionText))
            {
                string candidate = match.Value.Trim().TrimEnd('.', ',', ')', ';', ':');
                if (String.IsNullOrEmpty(candidate)) continue;

                // A `../sibling/...` path cannot be anchored against the mission worktree, and feeding it to
                // git makes resolution abort ("path outside repository"). Such paths name a sibling checkout,
                // not this repository, so they are not anchor subjects.
                if (candidate.IndexOf("..", StringComparison.Ordinal) >= 0) continue;

                if (embedsDiff &&
                    (candidate.StartsWith("a/", StringComparison.Ordinal) ||
                     candidate.StartsWith("b/", StringComparison.Ordinal)))
                {
                    // `a/src/X.cs` and `b/src/X.cs` are the same repository path; dropping the prefix also
                    // de-duplicates the pair so the anchors block lists the file once.
                    candidate = candidate.Substring(2);
                }

                if (!seen.Add(candidate)) continue;

                results.Add(candidate);
                if (results.Count >= MaxPaths) break;
            }

            return results;
        }

        /// <summary>
        /// Whether a path names a read-only tree that belongs to a sibling checkout rather than to
        /// the mission's own repository. Callers use this to keep an absent path from being reported
        /// as new work: the source a port reads from is absent by design, and telling a captain to
        /// create it produces exactly the wrong diff.
        /// </summary>
        /// <param name="path">Repository-relative path, as named by the mission.</param>
        /// <returns>True when the path carries a sibling-source marker.</returns>
        public static bool IsExternalSourceTreePath(string? path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;

            // Matched per segment, not per substring, so a repository file whose NAME merely contains
            // a marker is not mistaken for a tree that lives somewhere else.
            string[] segments = path!.Split('/');
            foreach (string segment in segments)
            {
                foreach (string marker in _ExternalSourceTreeMarkers)
                {
                    if (segment.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Extract code identifiers named in the supplied mission text, in first-appearance order,
        /// de-duplicated, and capped at <see cref="MaxTerms"/>. Multi-word PascalCase only: a single
        /// capitalized word is ordinary prose far more often than it is a symbol.
        /// </summary>
        /// <param name="missionText">Mission title and description, concatenated by the caller.</param>
        /// <returns>Identifier terms named by the mission; empty when none are found.</returns>
        public static List<string> ExtractTerms(string? missionText)
        {
            List<string> results = new List<string>();
            if (String.IsNullOrWhiteSpace(missionText)) return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in _IdentifierPattern.Matches(missionText))
            {
                string candidate = match.Value;

                if (candidate.Length < MinTermLength) continue;
                if (_StopTerms.Contains(candidate)) continue;
                if (_ArmadaIdPattern.IsMatch(candidate)) continue;
                if (!seen.Add(candidate)) continue;

                results.Add(candidate);
                if (results.Count >= MaxTerms) break;
            }

            return results;
        }

        #endregion
    }
}
