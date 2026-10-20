namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Reads the file manifest a mission brief names, so a context pack can put the files a mission must
    /// read or change ahead of ranked search evidence. Two lists come out of the text: repository files
    /// (a path with directories, or a bare file name with a known source or document extension) and
    /// sibling inputs (a path that starts with <c>../</c>, which names a file in a sibling checkout).
    ///
    /// Extraction is deterministic and does no git or index work, so the manifest a pack was built from
    /// can be reproduced from the mission text alone.
    /// </summary>
    public static class ContextPackManifest
    {
        #region Public-Members

        /// <summary>
        /// Maximum repository files returned. A brief that names more has more subjects than one pack
        /// can show without crowding out every other file.
        /// </summary>
        public const int MaxAssignedPaths = 16;

        /// <summary>
        /// Maximum sibling inputs returned.
        /// </summary>
        public const int MaxSiblingPaths = 8;

        #endregion

        #region Private-Members

        // A path with at least one directory and an extension, optionally led by ../ segments.
        private static readonly Regex _PathPattern = new Regex(
            @"(?<![\w/.\-])(?:\.\./)*(?:[\w.\-]+/)+[\w.\-]+\.[A-Za-z0-9]{1,6}(?![\w/])",
            RegexOptions.Compiled);

        // A bare file name. Only a known extension counts, because a dotted word in prose is far more
        // often a namespace or a version than a file.
        private static readonly Regex _BareFilePattern = new Regex(
            @"(?<![\w/.\-])[A-Za-z_][\w\-]*(?:\.[\w\-]+)*\.(?:cs|csproj|sln|props|targets|razor|cshtml|ts|tsx|js|jsx|mjs|py|go|rs|java|kt|swift|rb|php|c|h|cpp|hpp|sql|sh|ps1|md|json|yaml|yml|xml|toml)(?![\w/])",
            RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Repository files the mission text names, in first-appearance order, de-duplicated and capped at
        /// <see cref="MaxAssignedPaths"/>. A sibling path (<c>../</c>) and a path inside a sibling
        /// read-only source tree are not repository files and are left out.
        /// </summary>
        /// <param name="missionText">Mission title and description.</param>
        /// <returns>Repository-relative paths or bare file names; empty when the text names none.</returns>
        public static List<string> ExtractAssignedPaths(string? missionText)
        {
            List<string> results = new List<string>();
            if (String.IsNullOrWhiteSpace(missionText)) return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in _PathPattern.Matches(missionText))
            {
                string candidate = Clean(match.Value);
                if (String.IsNullOrEmpty(candidate)) continue;
                if (candidate.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                if (MissionSubjectExtractor.IsExternalSourceTreePath(candidate)) continue;
                if (!seen.Add(candidate)) continue;

                seenNames.Add(FileName(candidate));
                results.Add(candidate);
                if (results.Count >= MaxAssignedPaths) return results;
            }

            foreach (Match match in _BareFilePattern.Matches(missionText))
            {
                string candidate = Clean(match.Value);
                if (String.IsNullOrEmpty(candidate)) continue;

                // A bare name already covered by a full path adds nothing.
                if (seenNames.Contains(candidate)) continue;
                if (!seen.Add(candidate)) continue;

                results.Add(candidate);
                if (results.Count >= MaxAssignedPaths) break;
            }

            return results;
        }

        /// <summary>
        /// Sibling-checkout files the mission text names (a path that starts with <c>../</c>), in
        /// first-appearance order, de-duplicated and capped at <see cref="MaxSiblingPaths"/>.
        /// </summary>
        /// <param name="missionText">Mission title and description.</param>
        /// <returns>Sibling paths; empty when the text names none.</returns>
        public static List<string> ExtractSiblingPaths(string? missionText)
        {
            List<string> results = new List<string>();
            if (String.IsNullOrWhiteSpace(missionText)) return results;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in _PathPattern.Matches(missionText))
            {
                string candidate = Clean(match.Value);
                if (!candidate.StartsWith("../", StringComparison.Ordinal)) continue;
                if (!seen.Add(candidate)) continue;

                results.Add(candidate);
                if (results.Count >= MaxSiblingPaths) break;
            }

            return results;
        }

        /// <summary>
        /// Whether an indexed repository path is the file a manifest entry names: the same path, or a
        /// path that ends with the entry after a directory separator (a bare file name or a partial path).
        /// </summary>
        /// <param name="indexedPath">Repository-relative path from the index.</param>
        /// <param name="manifestEntry">A manifest entry from <see cref="ExtractAssignedPaths"/>.</param>
        /// <returns>True when the indexed path is the named file.</returns>
        public static bool Matches(string? indexedPath, string? manifestEntry)
        {
            if (String.IsNullOrWhiteSpace(indexedPath) || String.IsNullOrWhiteSpace(manifestEntry)) return false;
            string path = indexedPath!.Replace('\\', '/').TrimStart('/');
            string entry = manifestEntry!.Replace('\\', '/').TrimStart('/');
            if (String.Equals(path, entry, StringComparison.OrdinalIgnoreCase)) return true;
            return path.EndsWith("/" + entry, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Private-Methods

        private static string Clean(string value)
        {
            return (value ?? "").Trim().TrimEnd('.', ',', ')', ';', ':');
        }

        private static string FileName(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        #endregion
    }
}
