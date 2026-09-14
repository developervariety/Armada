namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A fully qualified git ref and the commit it points at.
    /// </summary>
    public class GitRefTip
    {
        #region Public-Members

        /// <summary>
        /// Fully qualified ref name, for example refs/heads/main.
        /// </summary>
        public string RefName { get; set; } = String.Empty;

        /// <summary>
        /// Commit SHA the ref points at.
        /// </summary>
        public string CommitSha { get; set; } = String.Empty;

        /// <summary>
        /// Committer time of the tip, when the listing could read it.
        /// </summary>
        public DateTime? CommitUtc { get; set; } = null;

        #endregion
    }
}
