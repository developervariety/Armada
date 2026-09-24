namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Reads one changed repository path per file out of a unified git diff.
    /// </summary>
    /// <remarks>
    /// A mission stores its work as a diff snapshot rather than a file list, so any question about
    /// WHAT changed has to be answered from that text. Each file entry is named once: by the path
    /// after the change, or by the old path of a deletion, so a rename reports where the file ended
    /// up. Entries are read through <see cref="GitDiffPaths"/>, which decodes Git C-quoted names and
    /// reads hunk bodies by their counts, so a body line that looks like a header never names a file.
    /// </remarks>
    public static class DiffPathExtractor
    {
        #region Public-Methods

        /// <summary>
        /// Extract one repository-relative path per changed file.
        /// </summary>
        /// <param name="unifiedDiff">Unified diff text. Null or empty yields an empty list.</param>
        /// <returns>Distinct changed paths, in the order they appear.</returns>
        public static IReadOnlyList<string> ExtractChangedPaths(string? unifiedDiff)
        {
            List<string> paths = new List<string>();
            if (String.IsNullOrWhiteSpace(unifiedDiff)) return paths;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (GitDiffFileChange change in GitDiffPaths.ParseFiles(unifiedDiff))
            {
                string? path = change.DisplayPath;
                if (String.IsNullOrEmpty(path)) continue;
                if (!seen.Add(path)) continue;
                paths.Add(path);
            }

            return paths;
        }

        #endregion
    }
}
