namespace Armada.Core.Database
{
    /// <summary>
    /// Adds <c>mission_id</c> to signals, so mail addressed to a mission can follow the mission to another captain.
    /// </summary>
    internal static class SignalMissionSchema
    {
        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements =
        {
            "ALTER TABLE signals ADD COLUMN mission_id TEXT;",
            "CREATE INDEX IF NOT EXISTS idx_signals_mission ON signals(mission_id);"
        };

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements =
        {
            "ALTER TABLE signals ADD COLUMN IF NOT EXISTS mission_id TEXT NULL;",
            "CREATE INDEX IF NOT EXISTS idx_signals_mission ON signals(mission_id);"
        };

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements =
        {
            "ALTER TABLE signals ADD COLUMN mission_id VARCHAR(450) NULL;",
            "CREATE INDEX idx_signals_mission ON signals(mission_id);"
        };

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements =
        {
            "IF COL_LENGTH('signals', 'mission_id') IS NULL ALTER TABLE signals ADD mission_id NVARCHAR(450) NULL;",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_signals_mission' AND object_id=OBJECT_ID('signals')) CREATE INDEX idx_signals_mission ON signals(mission_id);"
        };
    }
}
