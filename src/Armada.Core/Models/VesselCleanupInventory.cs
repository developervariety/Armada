namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Read-only cleanup inventory of one vessel's docks, worktrees, and Armada-managed branches.
    /// </summary>
    public class VesselCleanupInventory
    {
        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = string.Empty;

        /// <summary>
        /// Vessel name.
        /// </summary>
        public string VesselName { get; set; } = string.Empty;

        /// <summary>
        /// When the scan ran.
        /// </summary>
        public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Every inventoried resource.
        /// </summary>
        public List<VesselCleanupResource> Resources { get; set; } = new List<VesselCleanupResource>();

        /// <summary>
        /// Number of resources safe to clean up.
        /// </summary>
        public int SafeCount { get; set; } = 0;

        /// <summary>
        /// Number of resources still in use.
        /// </summary>
        public int InUseCount { get; set; } = 0;

        /// <summary>
        /// Number of resources with unknown disposition.
        /// </summary>
        public int UnknownCount { get; set; } = 0;

        /// <summary>
        /// Total measured bytes of safe resources (resources without a measured size are not counted).
        /// </summary>
        public long SafeMeasuredBytes { get; set; } = 0;

        /// <summary>
        /// Number of safe resources whose size could not be measured.
        /// </summary>
        public int SafeUnmeasuredCount { get; set; } = 0;
    }
}
