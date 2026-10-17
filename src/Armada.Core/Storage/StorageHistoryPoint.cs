namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage HistoryPoint measurement contract.
    /// </summary>
    public class StorageHistoryPoint
    {
        /// <summary>Sample completion time.</summary>
        public DateTime MeasuredUtc { get; set; } = DateTime.MinValue;

        /// <summary>Observed filesystem bytes across reported hosts.</summary>
        public long ObservedFileBytes { get; set; } = 0;

        /// <summary>Database allocation, counted separately.</summary>
        public long? DatabaseBytes { get; set; } = null;

        /// <summary>Observed bytes by bounded category.</summary>
        public Dictionary<string, long> Categories { get; set; } = new Dictionary<string, long>();

        /// <summary>Observed bytes by reported host.</summary>
        public Dictionary<string, long> Hosts { get; set; } = new Dictionary<string, long>();

        /// <summary>Database table allocation by schema and table.</summary>
        public Dictionary<string, long> Tables { get; set; } = new Dictionary<string, long>();

    }
}
