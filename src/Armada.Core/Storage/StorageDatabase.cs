namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>Size and table metadata of the configured database.</summary>
    public class StorageDatabase
    {
        /// <summary>Provider name: Sqlite, Postgresql, SqlServer or Mysql.</summary>
        public string Provider { get; set; } = String.Empty;

        /// <summary>When the database was measured, in UTC.</summary>
        public DateTime MeasuredUtc { get; set; } = DateTime.MinValue;

        /// <summary>Bytes the database allocates; null when unavailable.</summary>
        public long? AllocatedBytes { get; set; } = null;

        /// <summary>Allocated bytes the database can reuse (the SQLite free list); null when the provider reports none.</summary>
        public long? UnusedBytes { get; set; } = null;

        /// <summary>Measurement outcome.</summary>
        public StorageStatusEnum Status { get; set; } = StorageStatusEnum.Pending;

        /// <summary>Tables, in name order.</summary>
        public List<StorageTable> Tables { get; set; } = new List<StorageTable>();

        /// <summary>Codes from <see cref="StorageWarningCodes"/>.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
