namespace Armada.Core.Database
{
    /// <summary>
    /// Schema of the incident projection table for each supported database. The table holds the latest state of each
    /// incident; the incident snapshot events remain the record.
    /// </summary>
    internal static class IncidentSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            "CREATE TABLE IF NOT EXISTS incidents (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT, user_id TEXT, status TEXT NOT NULL, severity TEXT NOT NULL, title TEXT NOT NULL, summary TEXT, environment_id TEXT, environment_name TEXT, check_run_id TEXT, deployment_id TEXT, release_id TEXT, vessel_id TEXT, mission_id TEXT, voyage_id TEXT, impact TEXT, root_cause TEXT, recovery_notes TEXT, postmortem TEXT, detected_utc TEXT NOT NULL, last_update_utc TEXT NOT NULL, snapshot_id TEXT NOT NULL, payload TEXT NOT NULL);",
            "CREATE INDEX IF NOT EXISTS idx_incidents_status_updated ON incidents(status, last_update_utc, id);",
            "CREATE INDEX IF NOT EXISTS idx_incidents_tenant_updated ON incidents(tenant_id, last_update_utc, id);",
            "CREATE INDEX IF NOT EXISTS idx_incidents_updated ON incidents(last_update_utc, id);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS incidents (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT, user_id TEXT, status TEXT NOT NULL, severity TEXT NOT NULL, title TEXT NOT NULL, summary TEXT, environment_id TEXT, environment_name TEXT, check_run_id TEXT, deployment_id TEXT, release_id TEXT, vessel_id TEXT, mission_id TEXT, voyage_id TEXT, impact TEXT, root_cause TEXT, recovery_notes TEXT, postmortem TEXT, detected_utc TIMESTAMP NOT NULL, last_update_utc TIMESTAMP NOT NULL, snapshot_id TEXT NOT NULL, payload TEXT NOT NULL);",
            "CREATE INDEX IF NOT EXISTS idx_incidents_status_updated ON incidents(status, last_update_utc, id);",
            "CREATE INDEX IF NOT EXISTS idx_incidents_tenant_updated ON incidents(tenant_id, last_update_utc, id);",
            "CREATE INDEX IF NOT EXISTS idx_incidents_updated ON incidents(last_update_utc, id);"
        };

        internal static readonly string[] MysqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS incidents (id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, status VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, severity VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, title LONGTEXT NOT NULL, summary LONGTEXT NULL, environment_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, environment_name LONGTEXT NULL, check_run_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, deployment_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, release_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, vessel_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, mission_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, voyage_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, impact LONGTEXT NULL, root_cause LONGTEXT NULL, recovery_notes LONGTEXT NULL, postmortem LONGTEXT NULL, detected_utc DATETIME(6) NOT NULL, last_update_utc DATETIME(6) NOT NULL, snapshot_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, payload LONGTEXT NOT NULL, PRIMARY KEY (id));",
            "CREATE INDEX idx_incidents_status_updated ON incidents(status, last_update_utc, id);",
            "CREATE INDEX idx_incidents_tenant_updated ON incidents(tenant_id, last_update_utc, id);",
            "CREATE INDEX idx_incidents_updated ON incidents(last_update_utc, id);"
        };

        internal static readonly string[] SqlServerStatements =
        {
            "IF OBJECT_ID('incidents','U') IS NULL CREATE TABLE incidents (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NULL, user_id NVARCHAR(128) NULL, status NVARCHAR(32) NOT NULL, severity NVARCHAR(32) NOT NULL, title NVARCHAR(MAX) NOT NULL, summary NVARCHAR(MAX) NULL, environment_id NVARCHAR(128) NULL, environment_name NVARCHAR(MAX) NULL, check_run_id NVARCHAR(128) NULL, deployment_id NVARCHAR(128) NULL, release_id NVARCHAR(128) NULL, vessel_id NVARCHAR(128) NULL, mission_id NVARCHAR(128) NULL, voyage_id NVARCHAR(128) NULL, impact NVARCHAR(MAX) NULL, root_cause NVARCHAR(MAX) NULL, recovery_notes NVARCHAR(MAX) NULL, postmortem NVARCHAR(MAX) NULL, detected_utc DATETIME2 NOT NULL, last_update_utc DATETIME2 NOT NULL, snapshot_id NVARCHAR(128) NOT NULL, payload NVARCHAR(MAX) NOT NULL);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_incidents_status_updated' AND object_id=OBJECT_ID('incidents')) CREATE INDEX idx_incidents_status_updated ON incidents(status, last_update_utc, id);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_incidents_tenant_updated' AND object_id=OBJECT_ID('incidents')) CREATE INDEX idx_incidents_tenant_updated ON incidents(tenant_id, last_update_utc, id);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_incidents_updated' AND object_id=OBJECT_ID('incidents')) CREATE INDEX idx_incidents_updated ON incidents(last_update_utc, id);"
        };
    }
}
