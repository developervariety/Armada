namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Recognizes scratch files an agent or editor leaves in a worktree (backups, merge leftovers, swap files).
    /// An untracked scratch file is not evidence that a captain did work.
    /// </summary>
    public static class ScratchFileMatcher
    {
        #region Public-Members

        /// <summary>
        /// File-name suffixes treated as scratch, compared without regard to case.
        /// </summary>
        public static IReadOnlyList<string> Suffixes { get; } = new List<string>
        {
            ".backup", ".bak", ".orig", ".rej", ".tmp", ".swp", ".swo", "~"
        };

        /// <summary>
        /// Whole file names treated as scratch, compared without regard to case.
        /// </summary>
        public static IReadOnlyList<string> FileNames { get; } = new List<string>
        {
            ".DS_Store", "Thumbs.db"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a repository-relative path names a scratch file.
        /// </summary>
        /// <param name="path">Path with forward or back slashes.</param>
        /// <returns>True for a scratch file; false for anything else, including null or empty.</returns>
        public static bool IsScratchFile(string? path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string trimmed = path!.Replace('\\', '/').TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            if (name.Length == 0) return false;

            foreach (string fileName in FileNames)
            {
                if (String.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            }

            foreach (string suffix in Suffixes)
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        #endregion
    }
}
