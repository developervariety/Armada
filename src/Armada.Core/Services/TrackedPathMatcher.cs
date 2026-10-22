namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Matches a path named in mission or objective text against the files a revision tracks. Text names
    /// files and directories the way a reader says them: from the repository root, by a suffix of the
    /// tracked path, with a trailing slash, or in a different letter case. Each of those names a present
    /// path, and calling it absent tells a captain to create something that already exists.
    ///
    /// Matching runs in tiers and stops at the first tier with any candidate: exact case before ignored
    /// case. Within a tier an exact file or directory wins at once; otherwise a suffix resolves only when
    /// it names exactly one file or directory, because asserting one of several candidates sends a
    /// captain to the wrong file.
    /// </summary>
    public static class TrackedPathMatcher
    {
        #region Public-Methods

        /// <summary>
        /// Match a named path against tracked file paths.
        /// </summary>
        /// <param name="trackedFiles">Every file path the revision tracks, slash-separated.</param>
        /// <param name="requested">The path as the text names it.</param>
        /// <returns>The match; its <see cref="TrackedPathMatch.Path"/> is null when nothing or several paths match.</returns>
        public static TrackedPathMatch Match(IEnumerable<string> trackedFiles, string? requested)
        {
            if (trackedFiles == null) throw new ArgumentNullException(nameof(trackedFiles));
            string needle = Normalize(requested);
            if (needle.Length == 0) return new TrackedPathMatch();

            List<string> files = new List<string>();
            foreach (string file in trackedFiles)
            {
                if (!String.IsNullOrWhiteSpace(file)) files.Add(file.Trim());
            }

            TrackedPathMatch exactCase = MatchTier(files, needle, StringComparison.Ordinal);
            if (exactCase.CandidateCount > 0) return exactCase;

            // The exact-case tier found nothing, so any match here spells the path differently.
            TrackedPathMatch ignoredCase = MatchTier(files, needle, StringComparison.OrdinalIgnoreCase);
            ignoredCase.CaseDiffers = ignoredCase.Path != null;
            return ignoredCase;
        }

        /// <summary>
        /// List every tracked file a named path matches: the exact file, else each file that ends with the name as
        /// a path suffix, in exact case first and then ignoring case. Directories are not listed.
        /// </summary>
        /// <param name="trackedFiles">Every file path the revision tracks, slash-separated.</param>
        /// <param name="requested">The path as the text names it.</param>
        /// <returns>The matching files in ordinal order; empty when none match.</returns>
        public static List<string> FileCandidates(IEnumerable<string> trackedFiles, string? requested)
        {
            if (trackedFiles == null) throw new ArgumentNullException(nameof(trackedFiles));
            string needle = Normalize(requested);
            List<string> results = new List<string>();
            if (needle.Length == 0) return results;
            foreach (StringComparison comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            {
                foreach (string raw in trackedFiles)
                {
                    if (String.IsNullOrWhiteSpace(raw)) continue;
                    string file = raw.Trim();
                    if (String.Equals(file, needle, comparison)) return new List<string> { file };
                    if (file.EndsWith("/" + needle, comparison) && !results.Contains(file)) results.Add(file);
                }
                if (results.Count > 0) break;
            }
            results.Sort(StringComparer.Ordinal);
            return results;
        }

        #endregion

        #region Private-Methods

        private static string Normalize(string? requested)
        {
            if (String.IsNullOrWhiteSpace(requested)) return "";
            string needle = requested.Trim().Replace('\\', '/');
            while (needle.StartsWith("./", StringComparison.Ordinal)) needle = needle.Substring(2);
            return needle.Trim('/');
        }

        private static TrackedPathMatch MatchTier(List<string> files, string needle, StringComparison comparison)
        {
            string fileSuffix = "/" + needle;
            string directoryInfix = "/" + needle + "/";
            string directoryPrefix = needle + "/";
            Dictionary<string, bool> candidates = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (string file in files)
            {
                if (String.Equals(file, needle, comparison))
                    return new TrackedPathMatch { Path = file, CandidateCount = 1 };
                if (file.StartsWith(directoryPrefix, comparison))
                    return new TrackedPathMatch { Path = file.Substring(0, needle.Length), IsDirectory = true, CandidateCount = 1 };

                if (file.EndsWith(fileSuffix, comparison)) candidates[file] = false;
                int infix = file.IndexOf(directoryInfix, comparison);
                if (infix >= 0) candidates[file.Substring(0, infix + 1 + needle.Length)] = true;
            }

            TrackedPathMatch result = new TrackedPathMatch { CandidateCount = candidates.Count };
            if (candidates.Count == 1)
            {
                foreach (KeyValuePair<string, bool> candidate in candidates)
                {
                    result.Path = candidate.Key;
                    result.IsDirectory = candidate.Value;
                }
            }
            return result;
        }

        #endregion
    }
}
