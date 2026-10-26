namespace Armada.Core.Database
{
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>Provider DDL for Ask action proposal persistence.</summary>
    internal static class AskActionProposalSchema
    {
        internal static readonly string[] SqliteStatements = { "CREATE TABLE IF NOT EXISTS ask_action_proposals (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, thread_id TEXT NOT NULL, request_id TEXT NOT NULL, tool_name TEXT NOT NULL, arguments_json TEXT NOT NULL, fingerprint TEXT NOT NULL, state TEXT NOT NULL, created_utc TEXT NOT NULL, expires_utc TEXT NOT NULL, decided_utc TEXT NULL, completed_utc TEXT NULL, outcome TEXT NULL, work_references_json TEXT NOT NULL, UNIQUE(thread_id, request_id, fingerprint), FOREIGN KEY(thread_id, request_id) REFERENCES ask_turns(thread_id, request_id) ON DELETE CASCADE);", "CREATE INDEX IF NOT EXISTS idx_ask_action_proposals_owner ON ask_action_proposals(tenant_id, user_id, thread_id, created_utc DESC);" };
        internal static readonly string[] PostgresqlStatements = { "CREATE TABLE IF NOT EXISTS ask_action_proposals (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, thread_id TEXT NOT NULL, request_id TEXT NOT NULL, tool_name TEXT NOT NULL, arguments_json TEXT NOT NULL, fingerprint TEXT NOT NULL, state TEXT NOT NULL, created_utc TIMESTAMP NOT NULL, expires_utc TIMESTAMP NOT NULL, decided_utc TIMESTAMP NULL, completed_utc TIMESTAMP NULL, outcome TEXT NULL, work_references_json TEXT NOT NULL, UNIQUE(thread_id, request_id, fingerprint), FOREIGN KEY(thread_id, request_id) REFERENCES ask_turns(thread_id, request_id) ON DELETE CASCADE);", "CREATE INDEX IF NOT EXISTS idx_ask_action_proposals_owner ON ask_action_proposals(tenant_id, user_id, thread_id, created_utc DESC);" };
        internal static readonly string[] MysqlStatements =
        {
            @"CREATE TABLE IF NOT EXISTS ask_action_proposals (
                id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                thread_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                request_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tool_name VARCHAR(128) CHARACTER SET utf8mb4 NOT NULL,
                arguments_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
                fingerprint VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                state VARCHAR(24) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                created_utc DATETIME(6) NOT NULL,
                expires_utc DATETIME(6) NOT NULL,
                decided_utc DATETIME(6) NULL,
                completed_utc DATETIME(6) NULL,
                outcome VARCHAR(2000) CHARACTER SET utf8mb4 NULL,
                work_references_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY uq_ask_action_dedup (thread_id, request_id, fingerprint),
                KEY idx_ask_action_owner (tenant_id, user_id, thread_id, created_utc),
                CONSTRAINT fk_ask_action_turn FOREIGN KEY (thread_id, request_id) REFERENCES ask_turns(thread_id, request_id) ON DELETE CASCADE
            );"
        };
        internal static readonly string[] SqlServerStatements = { "IF OBJECT_ID('ask_action_proposals','U') IS NULL CREATE TABLE ask_action_proposals (id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, user_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, thread_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, request_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, tool_name NVARCHAR(128) NOT NULL, arguments_json NVARCHAR(MAX) NOT NULL, fingerprint CHAR(64) COLLATE Latin1_General_BIN2 NOT NULL, state NVARCHAR(24) NOT NULL, created_utc DATETIME2 NOT NULL, expires_utc DATETIME2 NOT NULL, decided_utc DATETIME2 NULL, completed_utc DATETIME2 NULL, outcome NVARCHAR(2000) NULL, work_references_json NVARCHAR(MAX) NOT NULL, CONSTRAINT uq_ask_action_dedup UNIQUE(thread_id, request_id, fingerprint), CONSTRAINT fk_ask_action_turn FOREIGN KEY(thread_id, request_id) REFERENCES ask_turns(thread_id, request_id) ON DELETE CASCADE);", "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_ask_action_proposals_owner' AND object_id=OBJECT_ID('ask_action_proposals')) CREATE INDEX idx_ask_action_proposals_owner ON ask_action_proposals(tenant_id, user_id, thread_id, created_utc DESC);" };
        internal static async Task EnsureAsync(DbConnection connection, DbTransaction? transaction, DatabaseTypeEnum provider, CancellationToken token)
        {
            string sql = provider switch { DatabaseTypeEnum.Sqlite => "SELECT name FROM pragma_table_info(@table);", DatabaseTypeEnum.Postgresql => "SELECT column_name FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table;", DatabaseTypeEnum.Mysql => "SELECT column_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table;", DatabaseTypeEnum.SqlServer => "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=SCHEMA_NAME() AND TABLE_NAME=@table;", _ => throw new System.NotSupportedException() };
            using (DbCommand command = connection.CreateCommand()) { command.Transaction = transaction; command.CommandText = sql; DbParameter p = command.CreateParameter(); p.ParameterName = "@table"; p.Value = "ask_action_proposals"; command.Parameters.Add(p); using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) { if (!await reader.ReadAsync(token).ConfigureAwait(false)) return; } }
            string[] required = { "id", "tenant_id", "user_id", "thread_id", "request_id", "tool_name", "arguments_json", "fingerprint", "state", "created_utc", "expires_utc", "decided_utc", "completed_utc", "outcome", "work_references_json" };
            using (DbCommand command = connection.CreateCommand()) { command.Transaction = transaction; command.CommandText = sql; DbParameter p = command.CreateParameter(); p.ParameterName = "@table"; p.Value = "ask_action_proposals"; command.Parameters.Add(p); System.Collections.Generic.HashSet<string> cols = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase); using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) cols.Add(System.Convert.ToString(reader.GetValue(0))!); foreach (string col in required) if (!cols.Contains(col)) throw new System.InvalidOperationException("Incompatible " + provider + " ask_action_proposals schema: missing " + col); }
        }
    }
}
