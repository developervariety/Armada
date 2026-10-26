namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>Provider schema for fleet action definitions and immutable run snapshots.</summary>
    internal static class FleetActionSchema
    {
        internal static readonly string[] SqliteStatements = { "CREATE TABLE IF NOT EXISTS fleet_actions (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, name TEXT NOT NULL, active INTEGER NOT NULL, kind TEXT NOT NULL, prompt_template TEXT NOT NULL, pipeline_id TEXT NULL, concurrency INTEGER NOT NULL, created_utc TEXT NOT NULL, last_update_utc TEXT NOT NULL);", "CREATE INDEX IF NOT EXISTS idx_fleet_actions_owner_updated ON fleet_actions(tenant_id, user_id, last_update_utc);", "CREATE TABLE IF NOT EXISTS fleet_action_runs (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, action_id TEXT NOT NULL, request_id TEXT NOT NULL, request_fingerprint CHAR(64) NOT NULL, state TEXT NOT NULL, revision INTEGER NOT NULL, snapshot_json TEXT NOT NULL, state_json TEXT NOT NULL, created_utc TEXT NOT NULL, started_utc TEXT NULL, completed_utc TEXT NULL, last_update_utc TEXT NOT NULL, UNIQUE(tenant_id, user_id, request_id));", "CREATE INDEX IF NOT EXISTS idx_fleet_action_runs_owner_created ON fleet_action_runs(tenant_id, user_id, created_utc DESC);", "CREATE INDEX IF NOT EXISTS idx_fleet_action_runs_state_updated ON fleet_action_runs(state, last_update_utc);" };
        internal static readonly string[] PostgresqlStatements = { "CREATE TABLE IF NOT EXISTS fleet_actions (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, name TEXT NOT NULL, active BOOLEAN NOT NULL, kind TEXT NOT NULL, prompt_template TEXT NOT NULL, pipeline_id TEXT NULL, concurrency INTEGER NOT NULL, created_utc TIMESTAMP NOT NULL, last_update_utc TIMESTAMP NOT NULL);", "CREATE INDEX IF NOT EXISTS idx_fleet_actions_owner_updated ON fleet_actions(tenant_id, user_id, last_update_utc);", "CREATE TABLE IF NOT EXISTS fleet_action_runs (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, action_id TEXT NOT NULL, request_id TEXT NOT NULL, request_fingerprint CHAR(64) NOT NULL, state TEXT NOT NULL, revision BIGINT NOT NULL, snapshot_json TEXT NOT NULL, state_json TEXT NOT NULL, created_utc TIMESTAMP NOT NULL, started_utc TIMESTAMP NULL, completed_utc TIMESTAMP NULL, last_update_utc TIMESTAMP NOT NULL, UNIQUE(tenant_id, user_id, request_id));", "CREATE INDEX IF NOT EXISTS idx_fleet_action_runs_owner_created ON fleet_action_runs(tenant_id, user_id, created_utc DESC);", "CREATE INDEX IF NOT EXISTS idx_fleet_action_runs_state_updated ON fleet_action_runs(state, last_update_utc);" };
        internal static readonly string[] MysqlStatements =
        {
            @"CREATE TABLE IF NOT EXISTS fleet_actions (
                id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                name VARCHAR(255) CHARACTER SET utf8mb4 NOT NULL,
                active BOOLEAN NOT NULL,
                kind VARCHAR(24) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                prompt_template LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
                pipeline_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
                concurrency INT NOT NULL,
                created_utc DATETIME(6) NOT NULL,
                last_update_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                KEY idx_fleet_actions_owner_updated (tenant_id, user_id, last_update_utc)
            );",
            @"CREATE TABLE IF NOT EXISTS fleet_action_runs (
                id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                action_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                request_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                request_fingerprint VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                state VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                revision BIGINT NOT NULL,
                snapshot_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
                state_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL,
                created_utc DATETIME(6) NOT NULL,
                started_utc DATETIME(6) NULL,
                completed_utc DATETIME(6) NULL,
                last_update_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY uq_fleet_action_run_request (tenant_id, user_id, request_id),
                KEY idx_fleet_action_runs_owner_created (tenant_id, user_id, created_utc),
                KEY idx_fleet_action_runs_state_updated (state, last_update_utc)
            );"
        };
        internal static readonly string[] SqlServerStatements = { "IF OBJECT_ID('fleet_actions','U') IS NULL CREATE TABLE fleet_actions (id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, user_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, name NVARCHAR(255) NOT NULL, active BIT NOT NULL, kind NVARCHAR(24) NOT NULL, prompt_template NVARCHAR(MAX) NOT NULL, pipeline_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NULL, concurrency INT NOT NULL, created_utc DATETIME2 NOT NULL, last_update_utc DATETIME2 NOT NULL);", "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_fleet_actions_owner_updated' AND object_id=OBJECT_ID('fleet_actions')) CREATE INDEX idx_fleet_actions_owner_updated ON fleet_actions(tenant_id,user_id,last_update_utc);", "IF OBJECT_ID('fleet_action_runs','U') IS NULL CREATE TABLE fleet_action_runs (id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, user_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, action_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, request_id NVARCHAR(128) COLLATE Latin1_General_BIN2 NOT NULL, request_fingerprint CHAR(64) COLLATE Latin1_General_BIN2 NOT NULL, state NVARCHAR(32) NOT NULL, revision BIGINT NOT NULL, snapshot_json NVARCHAR(MAX) NOT NULL, state_json NVARCHAR(MAX) NOT NULL, created_utc DATETIME2 NOT NULL, started_utc DATETIME2 NULL, completed_utc DATETIME2 NULL, last_update_utc DATETIME2 NOT NULL, CONSTRAINT uq_fleet_action_run_request UNIQUE(tenant_id,user_id,request_id));", "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_fleet_action_runs_owner_created' AND object_id=OBJECT_ID('fleet_action_runs')) CREATE INDEX idx_fleet_action_runs_owner_created ON fleet_action_runs(tenant_id,user_id,created_utc DESC);", "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_fleet_action_runs_state_updated' AND object_id=OBJECT_ID('fleet_action_runs')) CREATE INDEX idx_fleet_action_runs_state_updated ON fleet_action_runs(state,last_update_utc);" };

        internal static async Task EnsureAsync(DbConnection connection, DbTransaction? transaction, DatabaseTypeEnum provider, CancellationToken token)
        {
            string query = provider switch { DatabaseTypeEnum.Sqlite => "SELECT name FROM pragma_table_info(@table);", DatabaseTypeEnum.Postgresql => "SELECT column_name FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table;", DatabaseTypeEnum.Mysql => "SELECT column_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table;", DatabaseTypeEnum.SqlServer => "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=SCHEMA_NAME() AND TABLE_NAME=@table;", _ => throw new NotSupportedException() };
            await EnsureTableAsync(connection, transaction, provider, "fleet_actions", query, new[] { "id", "tenant_id", "user_id", "name", "active", "kind", "prompt_template", "pipeline_id", "concurrency", "created_utc", "last_update_utc" }, token).ConfigureAwait(false);
            await EnsureTableAsync(connection, transaction, provider, "fleet_action_runs", query, new[] { "id", "tenant_id", "user_id", "action_id", "request_id", "request_fingerprint", "state", "revision", "snapshot_json", "state_json", "created_utc", "started_utc", "completed_utc", "last_update_utc" }, token).ConfigureAwait(false);
            await EnsureRequestKeyAsync(connection, transaction, provider, token).ConfigureAwait(false);
        }

        private static async Task EnsureTableAsync(DbConnection connection, DbTransaction? transaction, DatabaseTypeEnum provider, string table, string query, string[] required, CancellationToken token)
        {
            HashSet<string> columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = query; Add(command, "@table", table);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) columns.Add(Convert.ToString(reader.GetValue(0))!);
            }
            if (columns.Count == 0) throw new InvalidOperationException("Incompatible " + provider + " schema: missing table " + table + ".");
            foreach (string column in required) if (!columns.Contains(column)) throw new InvalidOperationException("Incompatible " + provider + " " + table + " schema: missing " + column + ".");
        }

        private static async Task EnsureRequestKeyAsync(DbConnection connection, DbTransaction? transaction, DatabaseTypeEnum provider, CancellationToken token)
        {
            string sql = provider switch
            {
                DatabaseTypeEnum.Sqlite => "SELECT name FROM pragma_index_list('fleet_action_runs') WHERE \"unique\"=1;",
                DatabaseTypeEnum.Postgresql => "SELECT string_agg(k.column_name, ',' ORDER BY k.ordinal_position) FROM information_schema.table_constraints c JOIN information_schema.key_column_usage k ON k.constraint_name=c.constraint_name AND k.constraint_schema=c.constraint_schema AND k.table_name=c.table_name WHERE c.table_schema=current_schema() AND c.table_name='fleet_action_runs' AND c.constraint_type='UNIQUE' GROUP BY c.constraint_name;",
                DatabaseTypeEnum.Mysql => "SELECT GROUP_CONCAT(column_name ORDER BY ordinal_position SEPARATOR ',') FROM information_schema.key_column_usage WHERE table_schema=DATABASE() AND table_name='fleet_action_runs' AND constraint_name<>'PRIMARY' GROUP BY constraint_name;",
                DatabaseTypeEnum.SqlServer => "SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('fleet_action_runs') AND i.is_unique=1 GROUP BY i.index_id;",
                _ => throw new NotSupportedException()
            };
            const string expected = "tenant_id,user_id,request_id";
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = sql;
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        string? value = Convert.ToString(reader.GetValue(0));
                        if (provider == DatabaseTypeEnum.Sqlite && !String.IsNullOrEmpty(value)) value = await ReadSqliteIndexColumnsAsync(connection, transaction, value, token).ConfigureAwait(false);
                        if (String.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) return;
                    }
                }
            }
            throw new InvalidOperationException("Incompatible " + provider + " fleet_action_runs schema: missing unique actor request key.");
        }

        private static async Task<string?> ReadSqliteIndexColumnsAsync(DbConnection connection, DbTransaction? transaction, string index, CancellationToken token)
        {
            List<string> columns = new List<string>();
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "SELECT name FROM pragma_index_info(@index) ORDER BY seqno;"; Add(command, "@index", index);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) columns.Add(Convert.ToString(reader.GetValue(0))!);
            }
            return String.Join(",", columns);
        }

        private static void Add(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
        }
    }
}
