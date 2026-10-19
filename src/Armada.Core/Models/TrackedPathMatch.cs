namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// How a path named in mission or objective text matched the paths a revision tracks. A name can
    /// match a file or a directory, exactly or as a suffix, and in a different letter case than the
    /// repository uses; a name that matches several tracked paths is ambiguous and resolves to none.
    /// </summary>
    public class TrackedPathMatch
    {
        #region Public-Members

        /// <summary>
        /// The tracked path the name resolves to, or null when it matched no path or several.
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// True when <see cref="Path"/> is a directory that holds tracked files rather than a file.
        /// </summary>
        public bool IsDirectory { get; set; } = false;

        /// <summary>
        /// True when the name matched only after ignoring letter case, so the text spells the path
        /// differently from the repository.
        /// </summary>
        public bool CaseDiffers { get; set; } = false;

        /// <summary>
        /// The number of distinct tracked paths the name matched at the first tier that matched any.
        /// One when the name resolved, zero when it matched nothing, more than one when it is ambiguous.
        /// </summary>
        public int CandidateCount { get; set; } = 0;

        #endregion
    }
}
