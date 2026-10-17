namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage Database measurement contract.
    /// </summary>
    public class StorageDatabase
    {
        /// <summary>Database provider.</summary>
        public string Provider { get; set; } = String.Empty;

        /// <summary>Measurement completion time.</summary>
        public DateTime MeasuredUtc { get; set; } = DateTime.MinValue;

        /// <summary>Provider allocation, separate from filesystem bytes.</summary>
        public long? AllocatedBytes { get; set; } = null;

        /// <summary>Reusable database pages when supported.</summary>
        public long? UnusedBytes { get; set; } = null;

        /// <summary>Complete, Partial, Pending or Unavailable.</summary>
        public string Status { get; set; } = "Pending";

        /// <summary>Configured database table statistics.</summary>
        public List<StorageTable> Tables { get; set; } = new List<StorageTable>();

        /// <summary>Capability and permission warnings.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

    }
}
