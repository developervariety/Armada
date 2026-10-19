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
    /// The Judge follow-up handled-item table: its per-provider migration statements and the guard that
    /// runs before the migration is marked applied. Every provider migration uses these definitions, so
    /// all providers store the same columns. One item holds at most one marker, enforced by a unique
    /// index on the follow-up id and item number. The guard refuses a table of the same name that lacks
    /// a required column.
    /// </summary>
    internal static class JudgeFollowUpHandledItemSchema
    {
        #region Public-Members

        /// <summary>Table name.</summary>
        internal const string TableName = "judge_follow_up_handled_items";

        /// <summary>Columns every provider must hold.</summary>
        internal static readonly string[] ColumnNames = new[]
        {
            "id", "tenant_id", "user_id", "follow_up_id", "item_number", "kind", "disposition", "handled_by", "handled_utc"
        };

        /// <summary>SQLite statements.</summary>
        internal static readonly string[] SqliteStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS judge_follow_up_handled_items (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT, user_id TEXT, follow_up_id TEXT NOT NULL, item_number INTEGER NOT NULL, kind TEXT NOT NULL, disposition TEXT NOT NULL DEFAULT '', handled_by TEXT NOT NULL DEFAULT '', handled_utc TEXT NOT NULL);",
            @"CREATE UNIQUE INDEX IF NOT EXISTS idx_judge_follow_up_handled_items_item ON judge_follow_up_handled_items(follow_up_id, item_number);"
        };

        /// <summary>PostgreSQL statements.</summary>
        internal static readonly string[] PostgresqlStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS judge_follow_up_handled_items (id TEXT PRIMARY KEY, tenant_id TEXT, user_id TEXT, follow_up_id TEXT NOT NULL, item_number INTEGER NOT NULL, kind TEXT NOT NULL, disposition TEXT NOT NULL DEFAULT '', handled_by TEXT NOT NULL DEFAULT '', handled_utc TIMESTAMPTZ NOT NULL);",
            @"CREATE UNIQUE INDEX IF NOT EXISTS idx_judge_follow_up_handled_items_item ON judge_follow_up_handled_items(follow_up_id, item_number);"
        };

        /// <summary>MySQL statements.</summary>
        internal static readonly string[] MysqlStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS judge_follow_up_handled_items (id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, tenant_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, user_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, follow_up_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, item_number INT NOT NULL, kind VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, disposition TEXT CHARACTER SET utf8mb4 NOT NULL, handled_by VARCHAR(256) CHARACTER SET utf8mb4 NOT NULL DEFAULT '', handled_utc DATETIME(6) NOT NULL, PRIMARY KEY (id));",
            @"CREATE UNIQUE INDEX idx_judge_follow_up_handled_items_item ON judge_follow_up_handled_items(follow_up_id, item_number);"
        };

        /// <summary>SQL Server statements.</summary>
        internal static readonly string[] SqlServerStatements = new[]
        {
            @"IF OBJECT_ID('judge_follow_up_handled_items','U') IS NULL CREATE TABLE judge_follow_up_handled_items (id NVARCHAR(128) NOT NULL PRIMARY KEY, tenant_id NVARCHAR(128) NULL, user_id NVARCHAR(128) NULL, follow_up_id NVARCHAR(128) NOT NULL, item_number INT NOT NULL, kind NVARCHAR(32) NOT NULL, disposition NVARCHAR(2000) NOT NULL DEFAULT '', handled_by NVARCHAR(256) NOT NULL DEFAULT '', handled_utc DATETIME2 NOT NULL);",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='idx_judge_follow_up_handled_items_item' AND object_id=OBJECT_ID('judge_follow_up_handled_items')) CREATE UNIQUE INDEX idx_judge_follow_up_handled_items_item ON judge_follow_up_handled_items(follow_up_id, item_number);"
        };

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Refuse an existing <c>judge_follow_up_handled_items</c> table that lacks a required column. An
        /// absent table passes, because the migration creates it.
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
                _ => throw new NotSupportedException("Unsupported Judge follow-up handled-item schema provider")
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
                    throw new InvalidOperationException("Incompatible " + provider + " judge_follow_up_handled_items schema: missing column " + column + "; migration history was not advanced");
            }
        }

        #endregion
    }
}
