namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Pure predicate that decides whether a passing mission's change is small and safe enough to land
    /// unattended, or must hold for review, given a vessel's <see cref="AutoLandPolicy"/>. It reads only
    /// the diff summary (file count, line count, changed paths) that the git layer already produces, so it
    /// unit tests without a repository. When the policy is disabled it imposes no hold; an empty change is
    /// treated as a no-op that does not land.
    /// </summary>
    public static class AutoLandPredicate
    {
        #region Public-Methods

        /// <summary>
        /// Evaluate the auto-land rules against a change summary.
        /// </summary>
        /// <param name="filesChanged">Number of files the mission changed.</param>
        /// <param name="linesChanged">Number of lines (added + removed) the mission changed.</param>
        /// <param name="changedPaths">Repo-relative paths the mission changed; may be null.</param>
        /// <param name="policy">The vessel's auto-land policy; may be null (treated as disabled).</param>
        /// <returns>The land-or-hold decision.</returns>
        public static AutoLandDecision Evaluate(int filesChanged, int linesChanged, IReadOnlyList<string>? changedPaths, AutoLandPolicy? policy)
        {
            if (policy == null || !policy.Enabled) return AutoLandDecision.Lands();

            // Nothing to land: a no-op completion should not auto-land.
            if (filesChanged <= 0) return AutoLandDecision.Holds("no changes to land");

            if (policy.MaxFiles > 0 && filesChanged > policy.MaxFiles)
                return AutoLandDecision.Holds("changed " + filesChanged + " files, above the auto-land limit of " + policy.MaxFiles);

            if (policy.MaxLines > 0 && linesChanged > policy.MaxLines)
                return AutoLandDecision.Holds("changed " + linesChanged + " lines, above the auto-land limit of " + policy.MaxLines);

            IReadOnlyList<string> paths = changedPaths ?? new List<string>();

            // Deny globs win: any matching path forces a hold.
            if (policy.PathDenyGlobs != null && policy.PathDenyGlobs.Count > 0)
            {
                foreach (string path in paths)
                {
                    foreach (string glob in policy.PathDenyGlobs)
                    {
                        if (GlobMatches(glob, path))
                            return AutoLandDecision.Holds("path '" + path + "' matches a protected auto-land deny rule");
                    }
                }
            }

            // Allow globs: when set, every changed path must match at least one, else hold.
            if (policy.PathAllowGlobs != null && policy.PathAllowGlobs.Count > 0)
            {
                foreach (string path in paths)
                {
                    bool allowed = false;
                    foreach (string glob in policy.PathAllowGlobs)
                    {
                        if (GlobMatches(glob, path)) { allowed = true; break; }
                    }
                    if (!allowed)
                        return AutoLandDecision.Holds("path '" + path + "' is outside the auto-land allow-list");
                }
            }

            return AutoLandDecision.Lands();
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Match a repo-relative path against a glob supporting <c>*</c> (any run within a segment),
        /// <c>**</c> (any run across segments), and <c>?</c> (single char). Case-insensitive, forward-slash
        /// normalized.
        /// </summary>
        private static bool GlobMatches(string glob, string path)
        {
            if (String.IsNullOrEmpty(glob) || String.IsNullOrEmpty(path)) return false;

            string normalizedPath = path.Replace('\\', '/').TrimStart('/');
            string pattern = GlobToRegex(glob.Replace('\\', '/').TrimStart('/'));

            return Regex.IsMatch(normalizedPath, pattern, RegexOptions.IgnoreCase);
        }

        private static string GlobToRegex(string glob)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('^');

            int i = 0;
            while (i < glob.Length)
            {
                char c = glob[i];
                if (c == '*')
                {
                    bool doubleStar = (i + 1) < glob.Length && glob[i + 1] == '*';
                    if (doubleStar)
                    {
                        builder.Append(".*");
                        i += 2;
                        if (i < glob.Length && glob[i] == '/') i++;
                    }
                    else
                    {
                        builder.Append("[^/]*");
                        i++;
                    }
                }
                else if (c == '?')
                {
                    builder.Append("[^/]");
                    i++;
                }
                else
                {
                    builder.Append(Regex.Escape(c.ToString()));
                    i++;
                }
            }

            builder.Append('$');
            return builder.ToString();
        }

        #endregion
    }
}
