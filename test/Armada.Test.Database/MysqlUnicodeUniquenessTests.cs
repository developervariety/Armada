namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Settings;
    using MySqlConnector;

    internal sealed class MysqlUnicodeUniquenessTests
    {
        private readonly DatabaseSettings _Settings;
        internal MysqlUnicodeUniquenessTests(DatabaseSettings settings) { _Settings = settings; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N");
            string tenant = String.Concat(Enumerable.Repeat("🚢", 418)) + suffix;
            string value = String.Concat(Enumerable.Repeat("雪", 449)) + "a";
            string distinct = String.Concat(Enumerable.Repeat("雪", 449)) + "b";
            Dictionary<string, string> tables = new Dictionary<string, string>
            {
                { "users", "email" }, { "prompt_templates", "name" }, { "personas", "name" },
                { "pipelines", "name" }, { "playbooks", "file_name" }
            };
            using (MySqlConnection connection = new MySqlConnection(_Settings.GetConnectionString()))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                await ExecuteAsync(connection, null, "INSERT INTO tenants(id,name,created_utc,last_update_utc) VALUES(@tenant,'Unicode fixture',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));", tenant, null, null, token).ConfigureAwait(false);
                try
                {
                    foreach (KeyValuePair<string, string> table in tables)
                    {
                        string first = "unicode-" + table.Key + "-" + suffix;
                        string second = first + "-second";
                        string sql = InsertSql(table.Key, table.Value);
                        await ExecuteAsync(connection, null, sql, tenant, value, first, token).ConfigureAwait(false);
                        await ExecuteAsync(connection, null, sql, tenant, distinct, second, token).ConfigureAwait(false);
                        await DuplicateAsync(() => ExecuteAsync(connection, null, sql, tenant, value, first + "-duplicate", token)).ConfigureAwait(false);
                        // The tenant lookup and unique comparison retain the database collation.
                        await DuplicateAsync(() => ExecuteAsync(connection, null, sql, tenant, value.Substring(0, 449) + "Á", first + "-accent", token)).ConfigureAwait(false);
                        await DuplicateAsync(() => ExecuteAsync(connection, null, "UPDATE " + table.Key + " SET " + table.Value + "=@value WHERE id=@id;", tenant, value, second, token)).ConfigureAwait(false);
                        await ExecuteAsync(connection, null, "UPDATE " + table.Key + " SET armada_internal_tenant_key=0 WHERE id=@id;", tenant, null, first, token).ConfigureAwait(false);
                        using (MySqlCommand check = connection.CreateCommand())
                        {
                            check.CommandText = "SELECT COUNT(*) FROM " + table.Key + " c JOIN tenants t ON c.tenant_id=t.id WHERE c.id=@id AND c.armada_internal_tenant_key=t.armada_internal_tenant_key AND CHAR_LENGTH(c.tenant_id)=450 AND CHAR_LENGTH(c." + table.Value + ")=450;";
                            check.Parameters.AddWithValue("@id", first);
                            DatabaseAssert.Equal(1L, Convert.ToInt64(await check.ExecuteScalarAsync(token).ConfigureAwait(false)), "Full Unicode length and trigger-owned mapping " + table.Key);
                        }
                        if (table.Key != "users")
                        {
                            await ExecuteAsync(connection, null, sql, null, value, first + "-null1", token).ConfigureAwait(false);
                            await ExecuteAsync(connection, null, sql, null, value, first + "-null2", token).ConfigureAwait(false);
                        }
                    }
                    await VerifyConcurrentAsync(connection, tenant, suffix, false, token).ConfigureAwait(false);
                    await VerifyConcurrentAsync(connection, tenant, suffix, true, token).ConfigureAwait(false);
                }
                finally
                {
                    foreach (string table in tables.Keys)
                        await ExecuteAsync(connection, null, "DELETE FROM " + table + " WHERE id LIKE @id;", tenant, null, "unicode-" + table + "-" + suffix + "%", token).ConfigureAwait(false);
                    await ExecuteAsync(connection, null, "DELETE FROM users WHERE tenant_id=@tenant;", tenant, null, null, token).ConfigureAwait(false);
                    await ExecuteAsync(connection, null, "DELETE FROM tenants WHERE id=@tenant;", tenant, null, null, token).ConfigureAwait(false);
                }
            }
        }

        private async Task VerifyConcurrentAsync(MySqlConnection first, string tenant, string suffix, bool rollback, CancellationToken token)
        {
            using (MySqlConnection second = new MySqlConnection(_Settings.GetConnectionString()))
            {
                await second.OpenAsync(token).ConfigureAwait(false);
                using (MySqlTransaction transaction = await first.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    string value = "concurrent-" + rollback + "-" + suffix;
                    string sql = InsertSql("users", "email");
                    await ExecuteAsync(first, transaction, sql, tenant, value, "first-" + value, token).ConfigureAwait(false);
                    Task<int> waiting = ExecuteAsync(second, null, sql, tenant, value, "second-" + value, token);
                    // Native uniqueness must wait for the competing transaction, then enforce
                    // the outcome. A rollback frees the value; a commit rejects the duplicate.
                    Task completed = await Task.WhenAny(waiting, Task.Delay(150, token)).ConfigureAwait(false);
                    DatabaseAssert.True(completed != waiting, "Competing unique insert waits for transaction");
                    if (rollback) await transaction.RollbackAsync(token).ConfigureAwait(false);
                    else await transaction.CommitAsync(token).ConfigureAwait(false);
                    if (rollback) DatabaseAssert.Equal(1, await waiting.ConfigureAwait(false), "Rolled back value can be inserted");
                    else await DuplicateAsync(() => waiting).ConfigureAwait(false);
                }
            }
        }

        private static string InsertSql(string table, string column)
        {
            string extraColumn = table switch { "users" => ",password_sha256", "prompt_templates" or "playbooks" => ",content", "personas" => ",prompt_template_name", _ => "" };
            string extraValue = extraColumn.Length == 0 ? "" : ",'Unicode content 雪 🚢'";
            return "INSERT INTO " + table + "(id,tenant_id," + column + ",created_utc,last_update_utc" + extraColumn
                + ") VALUES(@id,@tenant,@value,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)" + extraValue + ");";
        }

        private static async Task DuplicateAsync(Func<Task<int>> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (MySqlException exception) when (exception.Number == 1062) { return; }
            throw new Exception("Expected full-value unique constraint violation 1062");
        }

        private static async Task<int> ExecuteAsync(MySqlConnection connection, MySqlTransaction transaction, string sql, string tenant, string value, string id, CancellationToken token)
        {
            using (MySqlCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("@tenant", (object)tenant ?? DBNull.Value);
                command.Parameters.AddWithValue("@value", (object)value ?? DBNull.Value);
                command.Parameters.AddWithValue("@id", (object)id ?? DBNull.Value);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
