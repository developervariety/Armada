namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>The cached storage inventory the administrator route returns.</summary>
    public class StorageSnapshot
    {
        /// <summary>When the cached figures were measured; null before the first collection finishes.</summary>
        public DateTime? MeasuredUtc { get; set; } = null;

        /// <summary>True while a collection is running.</summary>
        public bool Collecting { get; set; } = false;

        /// <summary>Code from <see cref="StorageWarningCodes"/> when the last collection failed; the previous figures stay cached.</summary>
        public string? LastError { get; set; } = null;

        /// <summary>Seconds between background collections; 0 when background collection is off.</summary>
        public int RefreshIntervalSeconds { get; set; } = 0;

        /// <summary>Maximum number of history samples kept.</summary>
        public int HistoryCapacity { get; set; } = 288;

        /// <summary>File scan of the data directory categories; null before the first collection.</summary>
        public StorageScanReport? Files { get; set; } = null;

        /// <summary>Database size and table metadata.</summary>
        public StorageDatabase Database { get; set; } = new StorageDatabase();

        /// <summary>Growth history, oldest first.</summary>
        public List<StorageHistoryPoint> History { get; set; } = new List<StorageHistoryPoint>();

        /// <summary>Codes from <see cref="StorageWarningCodes"/> about the inventory as a whole.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
