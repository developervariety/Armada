namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>One growth-history sample.</summary>
    public class StorageHistoryPoint
    {
        /// <summary>When the sample was taken, in UTC.</summary>
        public DateTime MeasuredUtc { get; set; } = DateTime.MinValue;

        /// <summary>Sum of the bytes of every measured location.</summary>
        public long ObservedFileBytes { get; set; } = 0;

        /// <summary>Database allocation at the time; null when unavailable.</summary>
        public long? DatabaseBytes { get; set; } = null;

        /// <summary>Bytes per category.</summary>
        public Dictionary<string, long> Categories { get; set; } = new Dictionary<string, long>();
    }
}
