namespace Armada.Core.Database
{
    /// <summary>
    /// How one provider stores one timestamp column, and so what a write sends for it.
    /// </summary>
    internal enum StoredTimestampEnum
    {
        /// <summary>
        /// A text column holding the UTC instant as ISO 8601 text with seven fractional digits and a trailing Z.
        /// </summary>
        Iso8601Text,

        /// <summary>
        /// A text column holding the server's own rendering of a timestamp the write sends typed: PostgreSQL renders
        /// a zone-aware timestamp with its offset, MySQL renders a zone-less one.
        /// </summary>
        ServerRenderedText,

        /// <summary>
        /// A zone-less timestamp column holding the UTC wall-clock time (PostgreSQL TIMESTAMP, MySQL DATETIME,
        /// SQL Server DATETIME2).
        /// </summary>
        Timestamp,

        /// <summary>
        /// A zone-aware timestamp column (PostgreSQL TIMESTAMPTZ).
        /// </summary>
        TimestampWithZone
    }
}
