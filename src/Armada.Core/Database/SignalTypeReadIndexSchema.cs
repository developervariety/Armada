namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes for the signal index on type, read flag and creation time. The mailbox and
    /// wake pollers ask for the unread signals of one type, newest first, and count them. Without this index each
    /// count scans the whole signals table and each page walks the creation-time index past every signal of other
    /// types, so the cost of a poll grows with every Progress signal ever recorded.
    /// </summary>
    internal static class SignalTypeReadIndexSchema
    {
        /// <summary>Name of the index on signal type, read flag and creation time.</summary>
        internal const string IndexName = "idx_signals_type_read_created";

        internal static readonly string[] SqliteStatements =
        {
            @"CREATE INDEX IF NOT EXISTS idx_signals_type_read_created ON signals(type, read, created_utc DESC);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"CREATE INDEX IF NOT EXISTS idx_signals_type_read_created ON signals(type, read, created_utc DESC);"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"CREATE INDEX idx_signals_type_read_created ON signals(type, `read`, created_utc DESC);"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_signals_type_read_created') CREATE INDEX idx_signals_type_read_created ON signals(type, [read], created_utc DESC);"
        };
    }
}
