namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of one branch-cleanup maintenance sweep.
    /// </summary>
    public class BranchCleanupSweepResult
    {
        #region Public-Members

        /// <summary>
        /// UTC timestamp of the sweep.
        /// </summary>
        public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Branches deleted from the local bare repository.
        /// </summary>
        public int SweptLocal { get; set; } = 0;

        /// <summary>
        /// Branches additionally deleted from origin (LocalAndRemote policy).
        /// </summary>
        public int SweptRemote { get; set; } = 0;

        /// <summary>
        /// Unmerged branches preserved (never eligible for a sweep).
        /// </summary>
        public int KeptUnmerged { get; set; } = 0;

        /// <summary>
        /// Deletions that failed; each is logged and emitted as an event.
        /// </summary>
        public int Failed { get; set; } = 0;

        /// <summary>
        /// Vessels skipped because their cleanup policy is None or they lack a local repository.
        /// </summary>
        public int SkippedVessels { get; set; } = 0;

        #endregion
    }
}
