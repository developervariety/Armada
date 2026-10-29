namespace Armada.Core.Database
{
    /// <summary>Provider DDL for Ask native tool permission requests and remembered rules.</summary>
    internal static class AskToolPermissionSchema
    {
        internal static readonly string[] SqliteStatements =
        {
            "CREATE TABLE IF NOT EXISTS ask_tool_permission_requests (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, thread_id TEXT NOT NULL, request_id TEXT NOT NULL, tool_name TEXT NOT NULL, input_json TEXT NOT NULL, tool_use_id TEXT NULL, state TEXT NOT NULL, decision_source TEXT NULL, rule_id TEXT NULL, created_utc TEXT NOT NULL, expires_utc TEXT NOT NULL, decided_utc TEXT NULL, FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE);",
            "CREATE INDEX IF NOT EXISTS idx_ask_tool_permission_requests_thread ON ask_tool_permission_requests(tenant_id, user_id, thread_id, created_utc DESC);",
            "CREATE TABLE IF NOT EXISTS ask_tool_permission_rules (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, thread_id TEXT NOT NULL, tool_name TEXT NOT NULL, action TEXT NOT NULL, created_utc TEXT NOT NULL, UNIQUE (thread_id, tool_name), FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE);"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            "CREATE TABLE IF NOT EXISTS ask_tool_permission_requests (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, thread_id TEXT NOT NULL REFERENCES ask_threads(id) ON DELETE CASCADE, request_id TEXT NOT NULL, tool_name TEXT NOT NULL, input_json TEXT NOT NULL, tool_use_id TEXT NULL, state TEXT NOT NULL, decision_source TEXT NULL, rule_id TEXT NULL, created_utc TIMESTAMP NOT NULL, expires_utc TIMESTAMP NOT NULL, decided_utc TIMESTAMP NULL);",
            "CREATE INDEX IF NOT EXISTS idx_ask_tool_permission_requests_thread ON ask_tool_permission_requests(tenant_id, user_id, thread_id, created_utc DESC);",
            "CREATE TABLE IF NOT EXISTS ask_tool_permission_rules (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, thread_id TEXT NOT NULL REFERENCES ask_threads(id) ON DELETE CASCADE, tool_name TEXT NOT NULL, action TEXT NOT NULL, created_utc TIMESTAMP NOT NULL, UNIQUE (thread_id, tool_name));"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"CREATE TABLE IF NOT EXISTS ask_tool_permission_requests (
                id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                thread_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                request_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tool_name VARCHAR(128) CHARACTER SET utf8mb4 NOT NULL,
                input_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
                tool_use_id VARCHAR(256) CHARACTER SET utf8mb4 NULL,
                state VARCHAR(24) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                decision_source VARCHAR(24) CHARACTER SET utf8mb4 NULL,
                rule_id VARCHAR(128) CHARACTER SET utf8mb4 NULL,
                created_utc DATETIME(6) NOT NULL,
                expires_utc DATETIME(6) NOT NULL,
                decided_utc DATETIME(6) NULL,
                PRIMARY KEY (id),
                KEY idx_ask_tool_permission_requests_thread (tenant_id, user_id, thread_id, created_utc),
                CONSTRAINT fk_ask_tool_permission_request_thread FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE
            );",
            @"CREATE TABLE IF NOT EXISTS ask_tool_permission_rules (
                id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                thread_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tool_name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                action VARCHAR(24) CHARACTER SET utf8mb4 NOT NULL,
                created_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY uq_ask_tool_permission_rule (thread_id, tool_name),
                CONSTRAINT fk_ask_tool_permission_rule_thread FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE
            );"
        };

        internal static readonly string[] SqlServerStatements =
        {
            "IF OBJECT_ID('ask_tool_permission_requests','U') IS NULL CREATE TABLE ask_tool_permission_requests (id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, user_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, thread_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, request_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, tool_name NVARCHAR(128) NOT NULL, input_json NVARCHAR(MAX) NOT NULL, tool_use_id NVARCHAR(256) NULL, state NVARCHAR(24) NOT NULL, decision_source NVARCHAR(24) NULL, rule_id NVARCHAR(128) NULL, created_utc DATETIME2 NOT NULL, expires_utc DATETIME2 NOT NULL, decided_utc DATETIME2 NULL, CONSTRAINT fk_ask_tool_permission_request_thread FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_ask_tool_permission_requests_thread' AND object_id=OBJECT_ID('ask_tool_permission_requests')) CREATE INDEX idx_ask_tool_permission_requests_thread ON ask_tool_permission_requests(tenant_id, user_id, thread_id, created_utc DESC);",
            "IF OBJECT_ID('ask_tool_permission_rules','U') IS NULL CREATE TABLE ask_tool_permission_rules (id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, user_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, thread_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, tool_name NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, action NVARCHAR(24) NOT NULL, created_utc DATETIME2 NOT NULL, CONSTRAINT uq_ask_tool_permission_rule UNIQUE (thread_id, tool_name), CONSTRAINT fk_ask_tool_permission_rule_thread FOREIGN KEY (thread_id) REFERENCES ask_threads(id) ON DELETE CASCADE);"
        };
    }
}
