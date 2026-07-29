namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Snapshot of host/container resource pressure observed at admission time.
    /// </summary>
    public sealed class ResourcePressureSnapshot
    {
        /// <summary>
        /// Available memory in bytes reported by the resource probe, or null when
        /// the probe could not determine it.
        /// </summary>
        public long? AvailableMemoryBytes { get; set; }

        /// <summary>
        /// UTC timestamp at which the snapshot was observed.
        /// </summary>
        public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;
    }
}