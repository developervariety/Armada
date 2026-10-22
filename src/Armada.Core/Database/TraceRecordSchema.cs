namespace Armada.Core.Database
{
    /// <summary>
    /// The trace record tables and their per-provider migration statements. Every provider migration uses
    /// these definitions, so all providers store the same columns. <c>trace_records</c> holds one row per
    /// described record, keyed by that record's id; <c>trace_links</c> holds each record's outgoing links
    /// so a trace can also walk an edge backwards, from a target to the records that name it;
    /// <c>trace_cursors</c> holds the named positions the trace sweep and backfill resume from.
    /// </summary>
    internal static class TraceRecordSchema
    {
        #region Public-Members

        /// <summary>Trace record table name.</summary>
        internal const string TableName = "trace_records";

        /// <summary>Trace link table name.</summary>
        internal const string LinkTableName = "trace_links";

        /// <summary>Trace cursor table name.</summary>
        internal const string CursorTableName = "trace_cursors";

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS trace_records (id TEXT NOT NULL PRIMARY KEY, kind TEXT NOT NULL, tenant_id TEXT, status TEXT, subject_created_utc TEXT NOT NULL, subject_last_update_utc TEXT NOT NULL, recorded_utc TEXT NOT NULL, document TEXT NOT NULL);",
            @"CREATE INDEX IF NOT EXISTS idx_trace_records_last_update ON trace_records(subject_last_update_utc);",
            @"CREATE TABLE IF NOT EXISTS trace_cursors (name TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL, updated_utc TEXT NOT NULL);",
            @"CREATE TABLE IF NOT EXISTS trace_links (source_id TEXT NOT NULL, source_kind TEXT NOT NULL, target_id TEXT NOT NULL, target_kind TEXT NOT NULL, relation TEXT NOT NULL);",
            @"CREATE INDEX IF NOT EXISTS idx_trace_links_source ON trace_links(source_id);",
            @"CREATE INDEX IF NOT EXISTS idx_trace_links_target ON trace_links(target_id);"
        };

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS trace_records (id TEXT PRIMARY KEY, kind TEXT NOT NULL, tenant_id TEXT, status TEXT, subject_created_utc TIMESTAMPTZ NOT NULL, subject_last_update_utc TIMESTAMPTZ NOT NULL, recorded_utc TIMESTAMPTZ NOT NULL, document TEXT NOT NULL);",
            @"CREATE INDEX IF NOT EXISTS idx_trace_records_last_update ON trace_records(subject_last_update_utc);",
            @"CREATE TABLE IF NOT EXISTS trace_cursors (name TEXT PRIMARY KEY, value TEXT NOT NULL, updated_utc TIMESTAMPTZ NOT NULL);",
            @"CREATE TABLE IF NOT EXISTS trace_links (source_id TEXT NOT NULL, source_kind TEXT NOT NULL, target_id TEXT NOT NULL, target_kind TEXT NOT NULL, relation TEXT NOT NULL);",
            @"CREATE INDEX IF NOT EXISTS idx_trace_links_source ON trace_links(source_id);",
            @"CREATE INDEX IF NOT EXISTS idx_trace_links_target ON trace_links(target_id);"
        };

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS trace_records (id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, kind VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, status VARCHAR(64) CHARACTER SET utf8mb4 NULL, subject_created_utc DATETIME(6) NOT NULL, subject_last_update_utc DATETIME(6) NOT NULL, recorded_utc DATETIME(6) NOT NULL, document MEDIUMTEXT CHARACTER SET utf8mb4 NOT NULL, PRIMARY KEY (id));",
            @"CREATE INDEX idx_trace_records_last_update ON trace_records(subject_last_update_utc);",
            @"CREATE TABLE IF NOT EXISTS trace_cursors (name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, value VARCHAR(512) CHARACTER SET utf8mb4 NOT NULL, updated_utc DATETIME(6) NOT NULL, PRIMARY KEY (name));",
            @"CREATE TABLE IF NOT EXISTS trace_links (source_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, source_kind VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, target_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, target_kind VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, relation VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL);",
            @"CREATE INDEX idx_trace_links_source ON trace_links(source_id);",
            @"CREATE INDEX idx_trace_links_target ON trace_links(target_id);"
        };

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = new[]
        {
            @"IF OBJECT_ID('trace_records','U') IS NULL CREATE TABLE trace_records (id NVARCHAR(128) NOT NULL PRIMARY KEY, kind NVARCHAR(32) NOT NULL, tenant_id NVARCHAR(128) NULL, status NVARCHAR(64) NULL, subject_created_utc DATETIME2 NOT NULL, subject_last_update_utc DATETIME2 NOT NULL, recorded_utc DATETIME2 NOT NULL, document NVARCHAR(MAX) NOT NULL);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_trace_records_last_update' AND object_id=OBJECT_ID('trace_records')) CREATE INDEX idx_trace_records_last_update ON trace_records(subject_last_update_utc);",
            @"IF OBJECT_ID('trace_cursors','U') IS NULL CREATE TABLE trace_cursors (name NVARCHAR(128) NOT NULL PRIMARY KEY, value NVARCHAR(512) NOT NULL, updated_utc DATETIME2 NOT NULL);",
            @"IF OBJECT_ID('trace_links','U') IS NULL CREATE TABLE trace_links (source_id NVARCHAR(128) NOT NULL, source_kind NVARCHAR(32) NOT NULL, target_id NVARCHAR(128) NOT NULL, target_kind NVARCHAR(32) NOT NULL, relation NVARCHAR(64) NOT NULL);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_trace_links_source' AND object_id=OBJECT_ID('trace_links')) CREATE INDEX idx_trace_links_source ON trace_links(source_id);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_trace_links_target' AND object_id=OBJECT_ID('trace_links')) CREATE INDEX idx_trace_links_target ON trace_links(target_id);"
        };

        #endregion
    }
}
