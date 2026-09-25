namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// PostgreSQL implementation of native captain memory persistence, including the memory_tags child table.
    /// </summary>
    public class MemoryMethods : IMemoryMethods
    {
        #region Private-Members

        private readonly PostgresqlDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MemoryMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Memory> CreateAsync(Memory memory, CancellationToken token = default)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (NpgsqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO memories (" + MemoryRows.InsertColumns + ") VALUES (" + MemoryRows.InsertValues + ");";
                        Bind(cmd, memory);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    await WriteTagsAsync(conn, tx, memory, token).ConfigureAwait(false);
                    await tx.CommitAsync(token).ConfigureAwait(false);
                }
            }

            return memory;
        }

        /// <inheritdoc />
        public async Task<Memory?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            List<Memory> found = await QueryAsync(
                "SELECT * FROM memories WHERE id = @id;",
                "SELECT memory_id, tag FROM memory_tags WHERE memory_id = @id;",
                cmd => StoredValueBinder.Value(cmd, "@id", id),
                token).ConfigureAwait(false);
            return found.Count > 0 ? found[0] : null;
        }

        /// <inheritdoc />
        public async Task<Memory?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            List<Memory> found = await QueryAsync(
                "SELECT * FROM memories WHERE tenant_id = @tenant_id AND id = @id;",
                "SELECT memory_id, tag FROM memory_tags WHERE memory_id = @id;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                },
                token).ConfigureAwait(false);
            return found.Count > 0 ? found[0] : null;
        }

        /// <inheritdoc />
        public async Task<Memory?> ReadByKeyAsync(string tenantId, string key, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            List<Memory> found = await QueryAsync(
                "SELECT * FROM memories WHERE tenant_id = @tenant_id AND memory_key = @memory_key;",
                "SELECT t.memory_id, t.tag FROM memory_tags t INNER JOIN memories m ON m.id = t.memory_id WHERE m.tenant_id = @tenant_id AND m.memory_key = @memory_key;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@memory_key", key);
                },
                token).ConfigureAwait(false);
            return found.Count > 0 ? found[0] : null;
        }

        /// <inheritdoc />
        public async Task<bool> UpdateAsync(Memory memory, int expectedVersion, CancellationToken token = default)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            if (String.IsNullOrEmpty(memory.TenantId)) throw new ArgumentException("A memory update requires the owning tenant.", nameof(memory));

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    int rows;
                    using (NpgsqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = MemoryRows.UpdateSql;
                        Bind(cmd, memory);
                        StoredValueBinder.Value(cmd, "@expected_version", expectedVersion);
                        rows = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    if (rows != 1)
                    {
                        await tx.RollbackAsync(token).ConfigureAwait(false);
                        return false;
                    }

                    await WriteTagsAsync(conn, tx, memory, token).ConfigureAwait(false);
                    await tx.CommitAsync(token).ConfigureAwait(false);
                    return true;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    int rows;
                    using (NpgsqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM memories WHERE tenant_id = @tenant_id AND id = @id;";
                        StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                        StoredValueBinder.Value(cmd, "@id", id);
                        rows = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    if (rows == 1)
                    {
                        using (NpgsqlCommand cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "DELETE FROM memory_tags WHERE memory_id = @id;";
                            StoredValueBinder.Value(cmd, "@id", id);
                            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }

                    await tx.CommitAsync(token).ConfigureAwait(false);
                    return rows == 1;
                }
            }
        }

        /// <inheritdoc />
        public Task<List<Memory>> EnumerateAsync(CancellationToken token = default)
        {
            return QueryAsync(
                "SELECT * FROM memories ORDER BY created_utc DESC;",
                "SELECT memory_id, tag FROM memory_tags;",
                null,
                token);
        }

        /// <inheritdoc />
        public Task<List<Memory>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return QueryAsync(
                "SELECT * FROM memories WHERE tenant_id = @tenant_id ORDER BY created_utc DESC;",
                "SELECT t.memory_id, t.tag FROM memory_tags t INNER JOIN memories m ON m.id = t.memory_id WHERE m.tenant_id = @tenant_id;",
                cmd => StoredValueBinder.Value(cmd, "@tenant_id", tenantId),
                token);
        }

        #endregion

        #region Private-Methods

        private async Task<List<Memory>> QueryAsync(string memorySql, string tagSql, Action<NpgsqlCommand>? parameterize, CancellationToken token)
        {
            List<Memory> results = new List<Memory>();
            Dictionary<string, List<string>> tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = memorySql;
                    parameterize?.Invoke(cmd);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MemoryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                if (results.Count == 0) return results;

                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = tagSql;
                    parameterize?.Invoke(cmd);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string memoryId = reader["memory_id"].ToString()!;
                            if (!tags.TryGetValue(memoryId, out List<string>? list))
                            {
                                list = new List<string>();
                                tags[memoryId] = list;
                            }
                            list.Add(reader["tag"].ToString()!);
                        }
                    }
                }
            }

            MemoryRows.AttachTags(results, tags);
            return results;
        }

        private static async Task WriteTagsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Memory memory, CancellationToken token)
        {
            using (NpgsqlCommand del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM memory_tags WHERE memory_id = @memory_id;";
                StoredValueBinder.Value(del, "@memory_id", memory.Id);
                await del.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            foreach (string tag in MemoryRows.TagsToWrite(memory))
            {
                using (NpgsqlCommand ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO memory_tags (memory_id, tag) VALUES (@memory_id, @tag);";
                    StoredValueBinder.Value(ins, "@memory_id", memory.Id);
                    StoredValueBinder.Value(ins, "@tag", tag);
                    await ins.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void Bind(NpgsqlCommand cmd, Memory memory)
        {
            MemoryColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "memories"), memory);
        }

        #endregion
    }
}
