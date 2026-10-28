namespace Armada.Core.Storage
{
    using System;

    /// <summary>Size and row count of one database table.</summary>
    public class StorageTable
    {
        /// <summary>Schema or database that owns the table.</summary>
        public string Schema { get; set; } = String.Empty;

        /// <summary>Table name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Row count; null when the provider reports none.</summary>
        public long? Rows { get; set; } = null;

        /// <summary>How <see cref="Rows"/> was obtained.</summary>
        public StorageCountKindEnum CountKind { get; set; } = StorageCountKindEnum.Unavailable;

        /// <summary>Bytes allocated to table data.</summary>
        public long? DataBytes { get; set; } = null;

        /// <summary>Bytes allocated to indexes.</summary>
        public long? IndexBytes { get; set; } = null;

        /// <summary>Code from <see cref="StorageWarningCodes"/> when a figure for this table is missing.</summary>
        public string? Warning { get; set; } = null;
    }
}
