namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage Table measurement contract.
    /// </summary>
    public class StorageTable
    {
        /// <summary>Database schema name.</summary>
        public string Schema { get; set; } = String.Empty;

        /// <summary>Catalog table name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Row count, null when unavailable.</summary>
        public long? Rows { get; set; } = null;

        /// <summary>Exact, Estimated or Unavailable.</summary>
        public string CountKind { get; set; } = "Unavailable";

        /// <summary>Table allocation where supported.</summary>
        public long? DataBytes { get; set; } = null;

        /// <summary>Index allocation where supported.</summary>
        public long? IndexBytes { get; set; } = null;

        /// <summary>Measurement failure, without credentials.</summary>
        public string? Warning { get; set; } = null;

    }
}
