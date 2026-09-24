namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One record of <c>git diff --name-status -z</c>: a status and the path it applies to. A rename
    /// or copy also carries the source path.
    /// </summary>
    public sealed class GitNameStatusEntry
    {
        #region Public-Members

        /// <summary>
        /// Status as Git printed it (<c>M</c>, <c>A</c>, <c>D</c>, <c>R100</c>, <c>C075</c>, ...).
        /// </summary>
        public string Status { get; set; } = String.Empty;

        /// <summary>
        /// Source path of a rename or copy; null for every other status.
        /// </summary>
        public string? OldPath { get; set; } = null;

        /// <summary>
        /// Path the status applies to: the destination of a rename or copy.
        /// </summary>
        public string Path { get; set; } = String.Empty;

        #endregion
    }
}
