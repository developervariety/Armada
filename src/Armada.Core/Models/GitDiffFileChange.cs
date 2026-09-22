namespace Armada.Core.Models
{
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

        #endregion
    }
}
