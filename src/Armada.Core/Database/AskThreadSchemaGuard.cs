namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>Rejects unrelated tables that use the Ask storage names.</summary>
    internal static class AskThreadSchemaGuard
    {
        internal static async Task EnsureAsync(DbConnection connection, DbTransaction? transaction, DatabaseTypeEnum provider, CancellationToken token)
        {
            await EnsureTableAsync(connection, transaction, provider, "ask_threads", new[] { "id", "tenant_id", "user_id", "title", "captain_id", "pinned", "archived", "created_utc", "last_activity_utc", "active_turn_id" }, token).ConfigureAwait(false);
            await EnsureTableAsync(connection, transaction, provider, "ask_turns", new[] { "thread_id", "request_id", "sequence", "captain_id", "content", "show_thinking", "state", "response_json", "created_utc", "completed_utc" }, token).ConfigureAwait(false);
        }

        private static async Task EnsureTableAsync(DbConnection connection, DbTransaction? transaction, DatabaseTypeEnum provider, string table, string[] required, CancellationToken token)
        {
            string sql = provider switch
            {
                DatabaseTypeEnum.Sqlite => "SELECT name FROM pragma_table_info(@table);",
                DatabaseTypeEnum.Postgresql => "SELECT column_name FROM information_schema.columns WHERE table_schema=current_schema() AND table_name=@table;",
                DatabaseTypeEnum.Mysql => "SELECT column_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table;",
                DatabaseTypeEnum.SqlServer => "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=SCHEMA_NAME() AND TABLE_NAME=@table;",
                _ => throw new NotSupportedException("Unsupported Ask thread schema provider")
            };
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "@table";
                parameter.Value = table;
                command.Parameters.Add(parameter);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        present.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
            }
            if (present.Count == 0) return;
            foreach (string column in required)
                if (!present.Contains(column)) throw new InvalidOperationException("Incompatible " + provider + " " + table + " schema: missing column " + column + "; migration history was not advanced");
        }
    }
}
