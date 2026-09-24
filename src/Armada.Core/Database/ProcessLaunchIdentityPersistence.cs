namespace Armada.Core.Database
{
    /// <summary>
    /// Provider-specific schema changes that store the start time of an agent process next to every persisted process
    /// identifier (captains and missions). The start time is the process's launch identity: after the admiral process
    /// restarts, it tells the process Armada launched from an unrelated process that reuses the identifier. A row
    /// with a process identifier and no start time was written before the column existed and stays unverified.
    /// </summary>
    internal static class ProcessLaunchIdentityPersistence
    {
        /// <summary>SQLite migration statements.</summary>
        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE captains ADD COLUMN process_started_utc TEXT NULL;",
            @"ALTER TABLE missions ADD COLUMN process_started_utc TEXT NULL;"
        };

        /// <summary>PostgreSQL migration statements.</summary>
        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE captains ADD COLUMN IF NOT EXISTS process_started_utc TIMESTAMP NULL;",
            @"ALTER TABLE missions ADD COLUMN IF NOT EXISTS process_started_utc TIMESTAMP NULL;"
        };

        /// <summary>MySQL migration statements.</summary>
        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE captains ADD COLUMN process_started_utc DATETIME(6) NULL;",
            @"ALTER TABLE missions ADD COLUMN process_started_utc DATETIME(6) NULL;"
        };

        /// <summary>SQL Server migration statements.</summary>
        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('captains', 'process_started_utc') IS NULL ALTER TABLE captains ADD process_started_utc NVARCHAR(64) NULL;",
            @"IF COL_LENGTH('missions', 'process_started_utc') IS NULL ALTER TABLE missions ADD process_started_utc NVARCHAR(64) NULL;"
        };
    }
}
