namespace Armada.Core.Database
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;

    /// <summary>
    /// Explicit execution corrections for invalid historical provider syntax.
    /// The original migration declaration, version and applied record are retained.
    /// </summary>
    internal static class HistoricalMigrationCorrections
    {
        internal static async Task RecordSqlServerAsync(SqlConnection connection, SqlTransaction transaction,
            int version, int ordinal, string original, string executed, CancellationToken token)
        {
            if (original == executed) return;
            using (SqlCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO schema_repairs(id,checksum) VALUES (@id,@checksum);";
                command.Parameters.AddWithValue("@id", "sqlserver-pending-" + version + "-" + ordinal + "-v1");
                command.Parameters.AddWithValue("@checksum", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original + "\n" + executed))));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        internal static async Task ExecuteSqlServerAsync(SqlCommand command, int version, string original, CancellationToken token)
        {
            string resolved = ResolveSqlServer(version, original);
            command.CommandText = resolved;
            if (resolved == original)
            {
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return;
            }
            if (version == 59)
            {
                await ServerSchemaPrerequisites.EnsureColumnAsync(command.Connection, command.Transaction,
                    DatabaseTypeEnum.SqlServer, "missions", "mission_mode", "NVARCHAR(MAX)", token).ConfigureAwait(false);
                return;
            }
            using (SqlCommand inspect = command.Connection.CreateCommand())
            {
                inspect.Transaction = command.Transaction;
                inspect.CommandText = "SELECT c.is_computed, c.max_length, c.is_nullable, TYPE_NAME(c.system_type_id) AS type_name, x.is_persisted, x.definition FROM sys.columns c LEFT JOIN sys.computed_columns x ON x.object_id=c.object_id AND x.column_id=c.column_id WHERE c.object_id=OBJECT_ID('token_usage') AND c.name='model_index_prefix';";
                bool found;
                using (SqlDataReader reader = await inspect.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    found = await reader.ReadAsync(token).ConfigureAwait(false);
                    if (found && (!Convert.ToBoolean(reader["is_computed"]) || Convert.ToInt32(reader["max_length"]) != 900
                        || !Convert.ToBoolean(reader["is_nullable"]) || (string)reader["type_name"] != "nvarchar"
                        || !Convert.ToBoolean(reader["is_persisted"])
                        || NormalizeComputed((string)reader["definition"]) != NormalizeComputed("CONVERT(NVARCHAR(450),model)")))
                        throw new InvalidOperationException("Incompatible SQL Server token model computed column");
                }
                if (!found)
                {
                    inspect.CommandText = "ALTER TABLE token_usage ADD model_index_prefix AS CONVERT(NVARCHAR(450), model) PERSISTED;";
                    await inspect.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                inspect.CommandText = "SELECT c.name, k.is_included_column, k.is_descending_key, i.is_unique, i.is_disabled, i.has_filter, i.is_hypothetical FROM sys.indexes i JOIN sys.index_columns k ON k.object_id=i.object_id AND k.index_id=i.index_id JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id WHERE i.object_id=OBJECT_ID('token_usage') AND i.name='idx_token_usage_model' ORDER BY k.is_included_column,k.key_ordinal;";
                int count = 0;
                using (SqlDataReader reader = await inspect.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (count > 1 || (string)reader["name"] != (count == 0 ? "model_index_prefix" : "model")
                            || Convert.ToBoolean(reader["is_included_column"]) != (count == 1)
                            || Convert.ToBoolean(reader["is_descending_key"]) || Convert.ToBoolean(reader["is_unique"])
                            || Convert.ToBoolean(reader["is_disabled"]) || Convert.ToBoolean(reader["has_filter"])
                            || Convert.ToBoolean(reader["is_hypothetical"]))
                            throw new InvalidOperationException("Incompatible SQL Server token model index");
                        count++;
                    }
                }
                if (count == 0)
                {
                    inspect.CommandText = "CREATE INDEX idx_token_usage_model ON token_usage(model_index_prefix) INCLUDE (model);";
                    await inspect.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                else if (count != 2) throw new InvalidOperationException("Incompatible SQL Server token model index columns");
            }
        }

        private static string NormalizeComputed(string value) => Regex.Replace(value, @"[\s\[\]]", "").Trim('(', ')').ToUpperInvariant();

        internal static string ResolveSqlServer(int version, string statement)
        {
            if (version == 59)
            {
                const string original = "ALTER TABLE missions ADD COLUMN mission_mode TEXT;";
                if (!String.Equals(statement.Trim(), original, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unrecognized SQL Server migration 59; its execution correction requires the reviewed historical statement");
                return "ALTER TABLE missions ADD mission_mode NVARCHAR(MAX);";
            }
            if (version == 68 && statement.Contains("idx_token_usage_model", StringComparison.Ordinal))
            {
                const string original = "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_token_usage_model') CREATE INDEX idx_token_usage_model ON token_usage(model);";
                if (!String.Equals(statement.Trim(), original, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unrecognized SQL Server token model index statement");
                // Preserve the complete model value. The nonunique access path uses a bounded
                // prefix and includes the full value; equality still compares the original column.
                return "ALTER TABLE token_usage ADD model_index_prefix AS CONVERT(NVARCHAR(450), model) PERSISTED; CREATE INDEX idx_token_usage_model ON token_usage(model_index_prefix) INCLUDE (model);";
            }
            return statement;
        }
    }
}
