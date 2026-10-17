namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage HostReport measurement contract.
    /// </summary>
    public class StorageHostReport
    {
        /// <summary>Admiral or authenticated Harbor identifier.</summary>
        public string HostId { get; set; } = String.Empty;

        /// <summary>Host display name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Collection completion time in UTC.</summary>
        public DateTime MeasuredUtc { get; set; } = DateTime.MinValue;

        /// <summary>Source collection cadence in seconds, default 300.</summary>
        public int IntervalSeconds { get; set; } = 300;

        /// <summary>Whether measurements are old or the host is offline.</summary>
        public bool Stale { get; set; } = false;

        /// <summary>Complete, Partial, Pending or Unavailable.</summary>
        public string Status { get; set; } = "Pending";

        /// <summary>Exclusive filesystem measurements.</summary>
        public List<StorageLocation> Locations { get; set; } = new List<StorageLocation>();

        /// <summary>Distinct containing volumes.</summary>
        public List<StorageVolume> Volumes { get; set; } = new List<StorageVolume>();

        /// <summary>Bounded host collection warnings.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

    }
}
