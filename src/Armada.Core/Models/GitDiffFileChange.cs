namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// One file entry parsed from a unified Git diff. A deletion carries only an old path, an
    /// addition only a new path, and a rename or copy carries both. Paths are repository-relative,
    /// with Git C-quoting decoded and the <c>a/</c> / <c>b/</c> prefixes removed.
    /// </summary>
    public sealed class GitDiffFileChange
    {
        #region Public-Members

        /// <summary>
        /// Path before the change, or null when the file was added.
        /// </summary>
        public string? OldPath { get; set; } = null;

        /// <summary>
        /// Path after the change, or null when the file was deleted.
        /// </summary>
        public string? NewPath { get; set; } = null;

        /// <summary>
        /// True when the file was added.
        /// </summary>
        public bool IsNew { get; set; } = false;

        /// <summary>
        /// True when the file was deleted.
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        /// <summary>
        /// True when Git reported the entry as a rename.
        /// </summary>
        public bool IsRename { get; set; } = false;

        /// <summary>
        /// True when Git reported the entry as a copy.
        /// </summary>
        public bool IsCopy { get; set; } = false;

        /// <summary>
        /// True when Git reported the content as binary.
        /// </summary>
        public bool IsBinary { get; set; } = false;

        /// <summary>
        /// The one name that identifies the entry: the path after the change, or the old path of a
        /// deletion. A rename or copy is named by where the file ended up.
        /// </summary>
        public string? DisplayPath => NewPath ?? OldPath;

        /// <summary>
        /// Number of added lines across the entry's hunks.
        /// </summary>
        public int AddedLineCount { get; set; } = 0;

        /// <summary>
        /// Number of removed lines across the entry's hunks.
        /// </summary>
        public int RemovedLineCount { get; set; } = 0;

        /// <summary>
        /// Hunks with their body lines. Filled only when the diff was parsed with lines requested;
        /// empty otherwise.
        /// </summary>
        public List<GitDiffHunk> Hunks { get; } = new List<GitDiffHunk>();

        #endregion
    }
}
