namespace Armada.Core.Database
{
    /// <summary>
    /// Schema of the battery run table for each supported database. The per-task grades are one JSON document on the
    /// run; per-attempt detail stays in the mission attempt provenance rows.
    /// </summary>
    internal static class BatteryRunSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            "CREATE TABLE IF NOT EXISTS battery_runs (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT, user_id TEXT, captain_id TEXT, captain_name TEXT, pack_id TEXT NOT NULL, pack_version INTEGER NOT NULL, status TEXT NOT NULL, error TEXT, started_utc TEXT NOT NULL, completed_utc TEXT, results_json TEXT NOT NULL);",
            "CREATE INDEX IF NOT EXISTS idx_battery_runs_started ON battery_runs(started_utc);",
            "CREATE INDEX IF NOT EXISTS idx_battery_runs_captain ON battery_runs(captain_id, started_utc);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS battery_runs (id TEXT PRIMARY KEY, tenant_id TEXT, user_id TEXT, captain_id TEXT, captain_name TEXT, pack_id TEXT NOT NULL, pack_version INTEGER NOT NULL, status TEXT NOT NULL, error TEXT, started_utc TIMESTAMPTZ NOT NULL, completed_utc TIMESTAMPTZ, results_json TEXT NOT NULL);",
            "CREATE INDEX IF NOT EXISTS idx_battery_runs_started ON battery_runs(started_utc);",
            "CREATE INDEX IF NOT EXISTS idx_battery_runs_captain ON battery_runs(captain_id, started_utc);"
        };

        internal static readonly string[] MysqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS battery_runs (id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, captain_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, captain_name VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, pack_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, pack_version INT NOT NULL, status VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, error TEXT NULL, started_utc DATETIME(6) NOT NULL, completed_utc DATETIME(6) NULL, results_json LONGTEXT NOT NULL, PRIMARY KEY (id));",
            "CREATE INDEX idx_battery_runs_started ON battery_runs(started_utc);",
            "CREATE INDEX idx_battery_runs_captain ON battery_runs(captain_id, started_utc);"
        };

        internal static readonly string[] SqlServerStatements =
        {
            "IF OBJECT_ID('battery_runs','U') IS NULL CREATE TABLE battery_runs (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NULL, user_id NVARCHAR(128) NULL, captain_id NVARCHAR(128) NULL, captain_name NVARCHAR(256) NULL, pack_id NVARCHAR(128) NOT NULL, pack_version INT NOT NULL, status NVARCHAR(32) NOT NULL, error NVARCHAR(MAX) NULL, started_utc DATETIME2 NOT NULL, completed_utc DATETIME2 NULL, results_json NVARCHAR(MAX) NOT NULL);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_battery_runs_started' AND object_id=OBJECT_ID('battery_runs')) CREATE INDEX idx_battery_runs_started ON battery_runs(started_utc);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_battery_runs_captain' AND object_id=OBJECT_ID('battery_runs')) CREATE INDEX idx_battery_runs_captain ON battery_runs(captain_id, started_utc);"
        };
    }
}
