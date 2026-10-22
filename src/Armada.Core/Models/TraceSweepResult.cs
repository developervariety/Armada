namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Counts from one trace sweep or backfill batch. A batch reads events after its cursor, writes the
    /// trace record of every record those events name, and moves the cursor to the last event it read.
    /// </summary>
    public sealed class TraceSweepResult
    {
        #region Public-Members

        /// <summary>Cursor the batch advanced.</summary>
        public string CursorName { get; set; } = String.Empty;

        /// <summary>Cursor position before the batch.</summary>
        public DateTime? StartUtc { get; set; } = null;

        /// <summary>Cursor position after the batch.</summary>
        public DateTime? CursorUtc { get; set; } = null;

        /// <summary>Events read.</summary>
        public int EventsRead { get; set; } = 0;

        /// <summary>Distinct records the events named.</summary>
        public int RecordsSeen { get; set; } = 0;

        /// <summary>Trace records written, including the check runs, merge entries and deployments found under a mission or voyage.</summary>
        public int RecordsWritten { get; set; } = 0;

        /// <summary>Named records whose source row no longer exists, so no trace record could be built.</summary>
        public int RecordsMissing { get; set; } = 0;

        /// <summary>Records whose write failed; the batch logs each one and continues.</summary>
        public int Failures { get; set; } = 0;

        /// <summary>True when the batch reached the newest event, so no further batch is needed now.</summary>
        public bool Done { get; set; } = false;

        /// <summary>Stored trace records after the batch.</summary>
        public long StoredRecords { get; set; } = 0;

        #endregion
    }
}
