namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Result of a disk-lifecycle scan or reconciliation pass.
    /// </summary>
    public class DiskLifecycleReport
    {
        #region Public-Members

        /// <summary>
        /// UTC timestamp of the scan.
        /// </summary>
        public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether destructive reclamation is enabled in settings.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Whether this pass ran in dry-run mode (nothing deleted).
        /// </summary>
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// Total bytes scanned across categories.
        /// </summary>
        public long TotalBytes { get; set; } = 0;

        /// <summary>
        /// Total bytes eligible for reclamation.
        /// </summary>
        public long TotalReclaimableBytes { get; set; } = 0;

        /// <summary>
        /// Total items eligible for reclamation.
        /// </summary>
        public int ReclaimableItems { get; set; } = 0;

        /// <summary>
        /// Total items skipped with a recorded reason (fail-closed).
        /// </summary>
        public int SkippedItems { get; set; } = 0;

        /// <summary>
        /// Total items protected because they are active or referenced.
        /// </summary>
        public int ProtectedItems { get; set; } = 0;

        /// <summary>
        /// Per-category byte accounting.
        /// </summary>
        public List<DiskLifecycleCategory> Categories { get; set; } = new List<DiskLifecycleCategory>();

        /// <summary>
        /// Per-item disposition records. Capped to keep the payload bounded.
        /// </summary>
        public List<DiskLifecycleAction> Actions { get; set; } = new List<DiskLifecycleAction>();

        #endregion
    }
}
