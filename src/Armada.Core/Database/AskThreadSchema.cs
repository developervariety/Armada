namespace Armada.Core.Database
{
    /// <summary>Provider-specific DDL for saved Ask threads and idempotent turns.</summary>
    internal static class AskThreadSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            "CREATE TABLE IF NOT EXISTS ask_threads (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, title TEXT NOT NULL, captain_id TEXT NULL, pinned INTEGER NOT NULL DEFAULT 0, archived INTEGER NOT NULL DEFAULT 0, created_utc TEXT NOT NULL, last_activity_utc TEXT NOT NULL, active_turn_id TEXT NULL);",
            "CREATE TABLE IF NOT EXISTS ask_turns (thread_id TEXT NOT NULL, request_id TEXT NOT NULL, sequence INTEGER NOT NULL, captain_id TEXT NOT NULL, content TEXT NOT NULL, show_thinking INTEGER NOT NULL DEFAULT 0, state TEXT NOT NULL, response_json TEXT NULL, created_utc TEXT NOT NULL, completed_utc TEXT NULL, PRIMARY KEY (thread_id, request_id), UNIQUE (thread_id, sequence), FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE);",
            "CREATE INDEX IF NOT EXISTS idx_ask_threads_owner_activity ON ask_threads(tenant_id, user_id, archived, last_activity_utc DESC);",
            "CREATE INDEX IF NOT EXISTS idx_ask_turns_thread_sequence ON ask_turns(thread_id, sequence DESC);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS ask_threads (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, title TEXT NOT NULL, captain_id TEXT NULL, pinned BOOLEAN NOT NULL DEFAULT FALSE, archived BOOLEAN NOT NULL DEFAULT FALSE, created_utc TIMESTAMP NOT NULL, last_activity_utc TIMESTAMP NOT NULL, active_turn_id TEXT NULL);",
            "CREATE TABLE IF NOT EXISTS ask_turns (thread_id TEXT NOT NULL REFERENCES ask_threads(id) ON DELETE CASCADE, request_id TEXT NOT NULL, sequence BIGINT NOT NULL, captain_id TEXT NOT NULL, content TEXT NOT NULL, show_thinking BOOLEAN NOT NULL DEFAULT FALSE, state TEXT NOT NULL, response_json TEXT NULL, created_utc TIMESTAMP NOT NULL, completed_utc TIMESTAMP NULL, PRIMARY KEY (thread_id, request_id), UNIQUE (thread_id, sequence));",
            "CREATE INDEX IF NOT EXISTS idx_ask_threads_owner_activity ON ask_threads(tenant_id, user_id, archived, last_activity_utc DESC);",
            "CREATE INDEX IF NOT EXISTS idx_ask_turns_thread_sequence ON ask_turns(thread_id, sequence DESC);"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"CREATE TABLE IF NOT EXISTS ask_threads (
                id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                title VARCHAR(512) CHARACTER SET utf8mb4 NOT NULL,
                captain_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
                pinned BOOLEAN NOT NULL DEFAULT 0,
                archived BOOLEAN NOT NULL DEFAULT 0,
                created_utc DATETIME(6) NOT NULL,
                last_activity_utc DATETIME(6) NOT NULL,
                active_turn_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
                PRIMARY KEY (id)
            );",
            @"CREATE TABLE IF NOT EXISTS ask_turns (
                thread_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                request_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                sequence BIGINT NOT NULL,
                captain_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                content LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
                show_thinking BOOLEAN NOT NULL DEFAULT 0,
                state VARCHAR(24) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                response_json LONGTEXT CHARACTER SET utf8mb4 NULL,
                created_utc DATETIME(6) NOT NULL,
                completed_utc DATETIME(6) NULL,
                PRIMARY KEY (thread_id, request_id),
                UNIQUE KEY idx_ask_turns_thread_sequence (thread_id, sequence),
                CONSTRAINT fk_ask_turns_thread FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE
            );",
            "CREATE INDEX idx_ask_threads_owner_activity ON ask_threads(tenant_id, user_id, archived, last_activity_utc);",
            "CREATE INDEX idx_ask_turns_thread_sequence_read ON ask_turns(thread_id, sequence);"
        };

        internal static readonly string[] SqlServerStatements =
        {
            "IF OBJECT_ID('ask_threads','U') IS NULL CREATE TABLE ask_threads (id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, user_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, title NVARCHAR(512) NOT NULL, captain_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NULL, pinned BIT NOT NULL DEFAULT 0, archived BIT NOT NULL DEFAULT 0, created_utc DATETIME2 NOT NULL, last_activity_utc DATETIME2 NOT NULL, active_turn_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NULL);",
            "IF OBJECT_ID('ask_turns','U') IS NULL CREATE TABLE ask_turns (thread_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, request_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, sequence BIGINT NOT NULL, captain_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, content NVARCHAR(MAX) NOT NULL, show_thinking BIT NOT NULL DEFAULT 0, state NVARCHAR(24) NOT NULL, response_json NVARCHAR(MAX) NULL, created_utc DATETIME2 NOT NULL, completed_utc DATETIME2 NULL, CONSTRAINT pk_ask_turns PRIMARY KEY (thread_id, request_id), CONSTRAINT uq_ask_turns_thread_sequence UNIQUE (thread_id, sequence), CONSTRAINT fk_ask_turns_thread FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_ask_threads_owner_activity' AND object_id=OBJECT_ID('ask_threads')) CREATE INDEX idx_ask_threads_owner_activity ON ask_threads(tenant_id, user_id, archived, last_activity_utc DESC);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_ask_turns_thread_sequence' AND object_id=OBJECT_ID('ask_turns')) CREATE INDEX idx_ask_turns_thread_sequence ON ask_turns(thread_id, sequence DESC);"
        };
    }
}
