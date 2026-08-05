namespace Armada.Core.Models
{
    /// <summary>
    /// One auditable disposition record from a disk-lifecycle scan or reconciliation.
    /// </summary>
    public class DiskLifecycleAction
    {
        #region Public-Members

        /// <summary>
        /// Category the item belongs to.
        /// </summary>
        public string Category { get; set; } = String.Empty;

        /// <summary>
        /// Absolute path of the item.
        /// </summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>
        /// Disposition: dry-run-reclaim, reclaimed, skipped, or protected.
        /// </summary>
        public string Disposition { get; set; } = String.Empty;

        /// <summary>
        /// Human-readable reason for the disposition.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// Size of the item in bytes when known.
        /// </summary>
        public long Bytes { get; set; } = 0;

        #endregion
    }
}
