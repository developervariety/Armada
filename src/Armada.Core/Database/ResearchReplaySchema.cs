namespace Armada.Core.Database
{
    /// <summary>Append-only Research replay archive schema for each supported database.</summary>
    internal static class ResearchReplaySchema
    {
        internal static readonly string[] SqliteStatements = { "CREATE TABLE research_replays (id TEXT NOT NULL PRIMARY KEY,tenant_id TEXT NOT NULL,user_id TEXT NOT NULL,voyage_id TEXT NOT NULL,request_id TEXT NOT NULL,request_fingerprint TEXT NOT NULL,snapshot_json TEXT NOT NULL,receipt_json TEXT NOT NULL,created_utc TEXT NOT NULL,UNIQUE(tenant_id,user_id,request_id));", "CREATE INDEX idx_research_replay_voyage ON research_replays(voyage_id);", "ALTER TABLE missions ADD COLUMN research_replay_id TEXT NULL;" };
        internal static readonly string[] PostgresqlStatements = { "CREATE TABLE research_replays (id TEXT NOT NULL PRIMARY KEY,tenant_id TEXT NOT NULL,user_id TEXT NOT NULL,voyage_id TEXT NOT NULL,request_id TEXT NOT NULL,request_fingerprint TEXT NOT NULL,snapshot_json TEXT NOT NULL,receipt_json TEXT NOT NULL,created_utc TIMESTAMP NOT NULL,UNIQUE(tenant_id,user_id,request_id));", "CREATE INDEX idx_research_replay_voyage ON research_replays(voyage_id);", "ALTER TABLE missions ADD COLUMN research_replay_id TEXT NULL;" };
        internal static readonly string[] MysqlStatements = { @"CREATE TABLE research_replays (
    id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL PRIMARY KEY,
    tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    voyage_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    request_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    request_fingerprint VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    snapshot_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
    receipt_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
    created_utc DATETIME(6) NOT NULL,
    UNIQUE KEY uq_research_replay_request (tenant_id, user_id, request_id)
);", "CREATE INDEX idx_research_replay_voyage ON research_replays(voyage_id);", "ALTER TABLE missions ADD COLUMN research_replay_id VARCHAR(128) NULL;" };
        internal static readonly string[] SqlServerStatements = { "CREATE TABLE research_replays (id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY,tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL,user_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL,voyage_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL,request_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL,request_fingerprint VARCHAR(64) COLLATE Latin1_General_BIN2 NOT NULL,snapshot_json NVARCHAR(MAX) NOT NULL,receipt_json NVARCHAR(MAX) NOT NULL,created_utc DATETIME2 NOT NULL,CONSTRAINT uq_research_replay_request UNIQUE(tenant_id,user_id,request_id));", "CREATE INDEX idx_research_replay_voyage ON research_replays(voyage_id);", "ALTER TABLE missions ADD research_replay_id NVARCHAR(128) NULL;" };
    }
}
