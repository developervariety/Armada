namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Reads the set of changed repository paths out of a unified git diff.
    /// </summary>
    /// <remarks>
    /// A mission stores its work as a diff snapshot rather than a file list, so any question about
    /// WHAT changed has to be answered from that text. Only the "diff --git" headers are read: the
    /// hunk bodies can contain lines that look like headers, and trusting those would let a change
    /// set describe itself.
    /// </remarks>
    public static class DiffPathExtractor
    {
        #region Public-Methods

        /// <summary>
        /// Extract the distinct repository-relative paths a unified diff touches.
        /// </summary>
        /// <param name="unifiedDiff">Unified diff text. Null or empty yields an empty list.</param>
        /// <returns>Distinct changed paths, in the order they appear.</returns>
        public static IReadOnlyList<string> ExtractChangedPaths(string? unifiedDiff)
        {
            List<string> paths = new List<string>();
            if (String.IsNullOrWhiteSpace(unifiedDiff)) return paths;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string rawLine in unifiedDiff.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (!line.StartsWith("diff --git ", StringComparison.Ordinal)) continue;

                string? path = ParseGitHeaderPath(line);
                if (path == null) continue;
                if (!seen.Add(path)) continue;
                paths.Add(path);
            }

            return paths;
        }

        #endregion

        #region Private-Methods

        private static string? ParseGitHeaderPath(string line)
        {
            // Shape: diff --git a/<path> b/<path>. The b-side is authoritative because it is the
            // post-change name, so a rename reports where the file ended up.
            const string prefix = "diff --git ";
            string remainder = line.Substring(prefix.Length).Trim();
            if (remainder.Length == 0) return null;

            int bMarker = remainder.LastIndexOf(" b/", StringComparison.Ordinal);
            if (bMarker >= 0)
            {
                string bSide = remainder.Substring(bMarker + 3).Trim();
                if (bSide.Length > 0) return StripQuotes(bSide);
            }

            // A malformed header still tells us something changed; fall back to the a-side.
            if (remainder.StartsWith("a/", StringComparison.Ordinal))
            {
                string aSide = remainder.Substring(2).Trim();
                int space = aSide.IndexOf(' ');
                if (space > 0) aSide = aSide.Substring(0, space);
                if (aSide.Length > 0) return StripQuotes(aSide);
            }

            return null;
        }

        private static string StripQuotes(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2);
            return value;
        }

        #endregion
    }
}
