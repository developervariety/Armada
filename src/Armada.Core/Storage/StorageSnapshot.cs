namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage Snapshot measurement contract.
    /// </summary>
    public class StorageSnapshot
    {
        /// <summary>Last inventory completion, null before first sample.</summary>
        public DateTime? MeasuredUtc { get; set; } = null;

        /// <summary>Whether a background collection is in progress.</summary>
        public bool Collecting { get; set; } = false;

        /// <summary>Last collection failure, without secrets.</summary>
        public string? LastError { get; set; } = null;

        /// <summary>Database allocation and records.</summary>
        public StorageDatabase Database { get; set; } = new StorageDatabase();

        /// <summary>Local and last-known remote host measurements.</summary>
        public List<StorageHostReport> Hosts { get; set; } = new List<StorageHostReport>();

        /// <summary>Bounded process-local history, reset on server restart.</summary>
        public List<StorageHistoryPoint> History { get; set; } = new List<StorageHistoryPoint>();

        /// <summary>Current retention settings in days, except build slots.</summary>
        public Dictionary<string, int> Retention { get; set; } = new Dictionary<string, int>();

        /// <summary>Collection interval in seconds.</summary>
        public int IntervalSeconds { get; set; } = 300;

        /// <summary>Maximum retained process-local samples.</summary>
        public int HistoryCapacity { get; set; } = 288;

    }
}
