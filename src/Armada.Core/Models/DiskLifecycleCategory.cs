namespace Armada.Core.Models
{
    /// <summary>
    /// Byte accounting for one owned storage category in a disk-lifecycle report.
    /// </summary>
    public class DiskLifecycleCategory
    {
        #region Public-Members

        /// <summary>
        /// Category name (docks, missionLogs, diffs, instructions, tempArtifacts, ...).
        /// </summary>
        public string Category { get; set; } = String.Empty;

        /// <summary>
        /// Total bytes present in the category.
        /// </summary>
        public long TotalBytes { get; set; } = 0;

        /// <summary>
        /// Bytes eligible for reclamation.
        /// </summary>
        public long ReclaimableBytes { get; set; } = 0;

        /// <summary>
        /// Total item count in the category.
        /// </summary>
        public int TotalItems { get; set; } = 0;

        /// <summary>
        /// Item count eligible for reclamation.
        /// </summary>
        public int ReclaimableItems { get; set; } = 0;

        /// <summary>
        /// Item count protected because it is active, referenced, or ambiguous.
        /// </summary>
        public int ProtectedItems { get; set; } = 0;

        /// <summary>
        /// Optional free-text note (for example a docker-policy reminder).
        /// </summary>
        public string? Note { get; set; } = null;

        #endregion
    }
}
