namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Counts from one trace backfill batch. The backfill walks the objective, voyage, mission, merge
    /// entry, deployment and incident tables in creation order, each from its own cursor, and records
    /// every row it visits together with the check runs, merge entries and deployments of each mission
    /// and voyage.
    /// </summary>
    public sealed class TraceBackfillResult
    {
        #region Public-Members

        /// <summary>Oldest creation time the backfill starts each table from when its cursor was never written.</summary>
        public DateTime SinceUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Source rows visited in this batch.</summary>
        public int RowsRead { get; set; } = 0;

        /// <summary>Trace records written, including the children found under a mission or voyage.</summary>
        public int RecordsWritten { get; set; } = 0;

        /// <summary>Rows that disappeared before they could be recorded.</summary>
        public int RecordsMissing { get; set; } = 0;

        /// <summary>Records whose write failed; the batch logs each one and continues.</summary>
        public int Failures { get; set; } = 0;

        /// <summary>Rows visited per table in this batch, by kind.</summary>
        public Dictionary<string, int> RowsByKind { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Cursor position per table after this batch, by kind.</summary>
        public Dictionary<string, DateTime> Cursors { get; set; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>True when every table reached its newest row, so no further batch is needed now.</summary>
        public bool Done { get; set; } = false;

        /// <summary>Stored trace records after the batch.</summary>
        public long StoredRecords { get; set; } = 0;

        #endregion
    }
}
