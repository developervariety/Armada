namespace Armada.Core.Database
{
    /// <summary>
    /// Schema of the per-attempt provenance table for each supported database, and the statement that deletes a
    /// mission's rows with the mission.
    /// </summary>
    internal static class MissionAttemptProvenanceSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            "CREATE TABLE IF NOT EXISTS mission_attempt_provenance (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT, user_id TEXT, mission_id TEXT NOT NULL, voyage_id TEXT, vessel_id TEXT, captain_id TEXT, persona TEXT, runtime TEXT, model TEXT, runtime_version TEXT, process_id INTEGER, started_utc TEXT NOT NULL, ended_utc TEXT, outcome TEXT NOT NULL, exit_code INTEGER, reason_code TEXT);",
            "CREATE INDEX IF NOT EXISTS idx_mission_attempt_provenance_mission ON mission_attempt_provenance(mission_id, started_utc);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS mission_attempt_provenance (id TEXT PRIMARY KEY, tenant_id TEXT, user_id TEXT, mission_id TEXT NOT NULL, voyage_id TEXT, vessel_id TEXT, captain_id TEXT, persona TEXT, runtime TEXT, model TEXT, runtime_version TEXT, process_id INTEGER, started_utc TIMESTAMPTZ NOT NULL, ended_utc TIMESTAMPTZ, outcome TEXT NOT NULL, exit_code INTEGER, reason_code TEXT);",
            "CREATE INDEX IF NOT EXISTS idx_mission_attempt_provenance_mission ON mission_attempt_provenance(mission_id, started_utc);"
        };

        internal static readonly string[] MysqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS mission_attempt_provenance (id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, mission_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, voyage_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, vessel_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, captain_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, persona VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, runtime VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, model VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, runtime_version VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, process_id INT NULL, started_utc DATETIME(6) NOT NULL, ended_utc DATETIME(6) NULL, outcome VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, exit_code INT NULL, reason_code VARCHAR(96) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, PRIMARY KEY (id));",
            "CREATE INDEX idx_mission_attempt_provenance_mission ON mission_attempt_provenance(mission_id, started_utc);"
        };

        internal static readonly string[] SqlServerStatements =
        {
            "IF OBJECT_ID('mission_attempt_provenance','U') IS NULL CREATE TABLE mission_attempt_provenance (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NULL, user_id NVARCHAR(128) NULL, mission_id NVARCHAR(128) NOT NULL, voyage_id NVARCHAR(128) NULL, vessel_id NVARCHAR(128) NULL, captain_id NVARCHAR(128) NULL, persona NVARCHAR(128) NULL, runtime NVARCHAR(64) NULL, model NVARCHAR(256) NULL, runtime_version NVARCHAR(128) NULL, process_id INT NULL, started_utc DATETIME2 NOT NULL, ended_utc DATETIME2 NULL, outcome NVARCHAR(32) NOT NULL, exit_code INT NULL, reason_code NVARCHAR(96) NULL);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_mission_attempt_provenance_mission' AND object_id=OBJECT_ID('mission_attempt_provenance')) CREATE INDEX idx_mission_attempt_provenance_mission ON mission_attempt_provenance(mission_id, started_utc);"
        };

        /// <summary>
        /// Statement that deletes the provenance rows of the missions a predicate selects. It runs before the
        /// statement that deletes those missions, in the same command, so no row outlives its mission.
        /// </summary>
        /// <param name="missionPredicate">Predicate over the <c>missions</c> table, such as <c>id = @id</c>.</param>
        /// <returns>The statement, ending with a space.</returns>
        internal static string DeleteWithMissionsSql(string missionPredicate)
        {
            return "DELETE FROM mission_attempt_provenance WHERE mission_id IN (SELECT id FROM missions WHERE " + missionPredicate + "); ";
        }
    }
}
