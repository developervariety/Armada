namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// The memory proposal table: its per-provider migration statements and the guard that runs before
    /// the migration is marked applied. Every provider migration uses these definitions, so all
    /// providers store the same columns. The guard refuses a table of the same name that lacks a
    /// required column, so a foreign table never reads as the proposal store.
    /// </summary>
    internal static class MemoryProposalSchema
    {
        #region Public-Members

        /// <summary>Table name.</summary>
        internal const string TableName = "memory_proposals";

        /// <summary>Columns every provider must hold.</summary>
        internal static readonly string[] ColumnNames = new[]
        {
            "id", "tenant_id", "user_id", "source", "source_key", "title", "body", "target_hint", "confidence",
            "related_record_ids_json", "state", "dismissed_by", "dismissed_reason", "dismissed_utc", "created_utc", "last_update_utc"
        };

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS memory_proposals (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT, user_id TEXT, source TEXT NOT NULL, source_key TEXT NOT NULL, title TEXT NOT NULL DEFAULT '', body TEXT NOT NULL DEFAULT '', target_hint TEXT NOT NULL DEFAULT '', confidence REAL NOT NULL DEFAULT 0, related_record_ids_json TEXT NOT NULL DEFAULT '[]', state TEXT NOT NULL DEFAULT 'Open', dismissed_by TEXT, dismissed_reason TEXT, dismissed_utc TEXT, created_utc TEXT NOT NULL, last_update_utc TEXT NOT NULL);",
            @"CREATE INDEX IF NOT EXISTS idx_memory_proposals_state_created ON memory_proposals(state, created_utc);",
            @"CREATE INDEX IF NOT EXISTS idx_memory_proposals_source_key ON memory_proposals(source_key);"
        };

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS memory_proposals (id TEXT PRIMARY KEY, tenant_id TEXT, user_id TEXT, source TEXT NOT NULL, source_key TEXT NOT NULL, title TEXT NOT NULL DEFAULT '', body TEXT NOT NULL DEFAULT '', target_hint TEXT NOT NULL DEFAULT '', confidence DOUBLE PRECISION NOT NULL DEFAULT 0, related_record_ids_json TEXT NOT NULL DEFAULT '[]', state TEXT NOT NULL DEFAULT 'Open', dismissed_by TEXT, dismissed_reason TEXT, dismissed_utc TIMESTAMPTZ NULL, created_utc TIMESTAMPTZ NOT NULL, last_update_utc TIMESTAMPTZ NOT NULL);",
            @"CREATE INDEX IF NOT EXISTS idx_memory_proposals_state_created ON memory_proposals(state, created_utc);",
            @"CREATE INDEX IF NOT EXISTS idx_memory_proposals_source_key ON memory_proposals(source_key);"
        };

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS memory_proposals (id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, source VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, source_key VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, title VARCHAR(512) CHARACTER SET utf8mb4 NOT NULL DEFAULT '', body LONGTEXT CHARACTER SET utf8mb4 NOT NULL, target_hint VARCHAR(256) CHARACTER SET utf8mb4 NOT NULL DEFAULT '', confidence DOUBLE NOT NULL DEFAULT 0, related_record_ids_json LONGTEXT CHARACTER SET utf8mb4 NOT NULL, state VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT 'Open', dismissed_by VARCHAR(2000) CHARACTER SET utf8mb4 NULL, dismissed_reason TEXT CHARACTER SET utf8mb4 NULL, dismissed_utc DATETIME(6) NULL, created_utc DATETIME(6) NOT NULL, last_update_utc DATETIME(6) NOT NULL, PRIMARY KEY (id));",
            @"CREATE INDEX idx_memory_proposals_state_created ON memory_proposals(state, created_utc);",
            @"CREATE INDEX idx_memory_proposals_source_key ON memory_proposals(source_key);"
        };

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = new[]
        {
            @"IF OBJECT_ID('memory_proposals','U') IS NULL CREATE TABLE memory_proposals (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NULL, user_id NVARCHAR(128) NULL, source NVARCHAR(64) NOT NULL, source_key NVARCHAR(64) NOT NULL, title NVARCHAR(512) NOT NULL DEFAULT '', body NVARCHAR(MAX) NOT NULL DEFAULT '', target_hint NVARCHAR(256) NOT NULL DEFAULT '', confidence FLOAT NOT NULL DEFAULT 0, related_record_ids_json NVARCHAR(MAX) NOT NULL DEFAULT '[]', state NVARCHAR(32) NOT NULL DEFAULT 'Open', dismissed_by NVARCHAR(2000) NULL, dismissed_reason NVARCHAR(2000) NULL, dismissed_utc DATETIME2 NULL, created_utc DATETIME2 NOT NULL, last_update_utc DATETIME2 NOT NULL);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_memory_proposals_state_created' AND object_id=OBJECT_ID('memory_proposals')) CREATE INDEX idx_memory_proposals_state_created ON memory_proposals(state, created_utc);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_memory_proposals_source_key' AND object_id=OBJECT_ID('memory_proposals')) CREATE INDEX idx_memory_proposals_source_key ON memory_proposals(source_key);"
        };

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Refuse an existing <c>memory_proposals</c> table that lacks a required column. An absent table
        /// passes, because the migration creates it.
        /// </summary>
        /// <param name="connection">Open connection.</param>
        /// <param name="transaction">Migration transaction, when the provider uses one.</param>
        /// <param name="provider">Provider.</param>
        /// <param name="token">Cancellation token.</param>
        internal static async Task EnsureAsync(DbConnection connection, DbTransaction? transaction, DatabaseTypeEnum provider, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            string sql = provider switch
            {
                DatabaseTypeEnum.Sqlite => "SELECT name FROM pragma_table_info(@table);",
                DatabaseTypeEnum.Postgresql => "SELECT column_name FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table;",
                DatabaseTypeEnum.Mysql => "SELECT column_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table;",
                DatabaseTypeEnum.SqlServer => "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=SCHEMA_NAME() AND TABLE_NAME=@table;",
                _ => throw new NotSupportedException("Unsupported memory proposal schema provider")
            };

            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "@table";
                parameter.Value = TableName;
                command.Parameters.Add(parameter);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        present.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
                }
            }

            if (present.Count == 0) return;
            foreach (string column in ColumnNames)
            {
                if (!present.Contains(column))
                    throw new InvalidOperationException("Incompatible " + provider + " memory_proposals schema: missing column " + column + "; migration history was not advanced");
            }
        }

        #endregion
    }
}
