namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One path that differs between two commits. Paths are repository-relative, exactly as git stores them:
    /// case is preserved and two paths that differ only in case are two entries.
    /// </summary>
    public sealed class ChangedPathEntry
    {
        #region Public-Members

        /// <summary>How the path changed.</summary>
        public ChangedPathStatusEnum Status { get; set; } = ChangedPathStatusEnum.Modified;

        /// <summary>The path at the later commit; for a deletion, the path that was removed.</summary>
        public string Path
        {
            get { return _Path; }
            set { _Path = value ?? throw new ArgumentNullException(nameof(Path)); }
        }

        /// <summary>The path at the earlier commit for a rename; null for every other status.</summary>
        public string? PreviousPath { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Path = String.Empty;

        #endregion
    }
}
