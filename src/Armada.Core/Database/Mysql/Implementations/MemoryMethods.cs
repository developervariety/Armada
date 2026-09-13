namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of native captain memory persistence, including the memory_tags child table.
    /// </summary>
    public class MemoryMethods : IMemoryMethods
    {
        #region Private-Members

        private readonly string _ConnectionString;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MemoryMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Memory> CreateAsync(Memory memory, CancellationToken token = default)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO memories (" + MemoryRows.InsertColumns + ") VALUES (" + MemoryRows.InsertValues + ");";
                        Bind(cmd, memory, true);
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
                cmd => cmd.Parameters.AddWithValue("@id", id),
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
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
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
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@memory_key", key);
                },
                token).ConfigureAwait(false);
            return found.Count > 0 ? found[0] : null;
        }

        /// <inheritdoc />
        public async Task<bool> UpdateAsync(Memory memory, int expectedVersion, CancellationToken token = default)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            if (String.IsNullOrEmpty(memory.TenantId)) throw new ArgumentException("A memory update requires the owning tenant.", nameof(memory));

            // MySQL reports matched rows only with the found-rows client flag, so the guarded update
            // counts through ROW_COUNT semantics that stay correct when the values are unchanged:
            // the version always increases, so a matched row is always a changed row.
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    int rows;
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = MemoryRows.UpdateSql;
                        Bind(cmd, memory, false);
                        cmd.Parameters.AddWithValue("@expected_version", expectedVersion);
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    int rows;
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM memories WHERE tenant_id = @tenant_id AND id = @id;";
                        cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                        cmd.Parameters.AddWithValue("@id", id);
                        rows = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    if (rows == 1)
                    {
                        using (MySqlCommand cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "DELETE FROM memory_tags WHERE memory_id = @id;";
                            cmd.Parameters.AddWithValue("@id", id);
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
                cmd => cmd.Parameters.AddWithValue("@tenant_id", tenantId),
                token);
        }

        #endregion

        #region Private-Methods

        private async Task<List<Memory>> QueryAsync(string memorySql, string tagSql, Action<MySqlCommand>? parameterize, CancellationToken token)
        {
            List<Memory> results = new List<Memory>();
            Dictionary<string, List<string>> tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = memorySql;
                    parameterize?.Invoke(cmd);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FromReader(reader));
                    }
                }

                if (results.Count == 0) return results;

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = tagSql;
                    parameterize?.Invoke(cmd);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
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

        private static async Task WriteTagsAsync(MySqlConnection conn, MySqlTransaction tx, Memory memory, CancellationToken token)
        {
            using (MySqlCommand del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM memory_tags WHERE memory_id = @memory_id;";
                del.Parameters.AddWithValue("@memory_id", memory.Id);
                await del.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            foreach (string tag in MemoryRows.TagsToWrite(memory))
            {
                using (MySqlCommand ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO memory_tags (memory_id, tag) VALUES (@memory_id, @tag);";
                    ins.Parameters.AddWithValue("@memory_id", memory.Id);
                    ins.Parameters.AddWithValue("@tag", tag);
                    await ins.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void Bind(MySqlCommand cmd, Memory memory, bool includeCreated)
        {
            cmd.Parameters.AddWithValue("@id", memory.Id);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)memory.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)memory.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@scope", memory.Scope.ToString());
            cmd.Parameters.AddWithValue("@type", memory.Type.ToString());
            cmd.Parameters.AddWithValue("@topic", (object?)memory.Topic ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@memory_key", (object?)memory.Key ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@summary", (object?)memory.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@content", memory.Content);
            cmd.Parameters.AddWithValue("@salience", memory.Salience);
            cmd.Parameters.AddWithValue("@version", memory.Version);
            cmd.Parameters.AddWithValue("@source_kind", memory.SourceKind.ToString());
            cmd.Parameters.AddWithValue("@source_voyage_id", (object?)memory.SourceVoyageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_mission_id", (object?)memory.SourceMissionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_vessel_id", (object?)memory.SourceVesselId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_detail", (object?)memory.SourceDetail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vessel_id", (object?)memory.VesselId ?? DBNull.Value);
            if (includeCreated) cmd.Parameters.AddWithValue("@created_utc", MemoryRows.AsUtc(memory.CreatedUtc));
            cmd.Parameters.AddWithValue("@last_update_utc", MemoryRows.AsUtc(memory.LastUpdateUtc));
        }

        private static Memory FromReader(MySqlDataReader reader)
        {
            Memory memory = new Memory();
            memory.Id = reader["id"].ToString()!;
            memory.TenantId = MysqlDatabaseDriver.NullableString(reader["tenant_id"]);
            memory.UserId = MysqlDatabaseDriver.NullableString(reader["user_id"]);
            memory.Scope = MemoryRows.ParseEnum(reader["scope"], MemoryScopeEnum.TenantWide);
            memory.Type = MemoryRows.ParseEnum(reader["type"], MemoryTypeEnum.Semantic);
            memory.Topic = MysqlDatabaseDriver.NullableString(reader["topic"]);
            memory.Key = MysqlDatabaseDriver.NullableString(reader["memory_key"]);
            memory.Summary = MysqlDatabaseDriver.NullableString(reader["summary"]);
            memory.Content = reader["content"]?.ToString() ?? String.Empty;
            memory.Salience = Convert.ToDouble(reader["salience"], CultureInfo.InvariantCulture);
            memory.Version = Convert.ToInt32(reader["version"], CultureInfo.InvariantCulture);
            memory.SourceKind = MemoryRows.ParseEnum(reader["source_kind"], MemorySourceKindEnum.Manual);
            memory.SourceVoyageId = MysqlDatabaseDriver.NullableString(reader["source_voyage_id"]);
            memory.SourceMissionId = MysqlDatabaseDriver.NullableString(reader["source_mission_id"]);
            memory.SourceVesselId = MysqlDatabaseDriver.NullableString(reader["source_vessel_id"]);
            memory.SourceDetail = MysqlDatabaseDriver.NullableString(reader["source_detail"]);
            memory.VesselId = MysqlDatabaseDriver.NullableString(reader["vessel_id"]);
            memory.CreatedUtc = MysqlDatabaseDriver.ReadUtc(reader["created_utc"]);
            memory.LastUpdateUtc = MysqlDatabaseDriver.ReadUtc(reader["last_update_utc"]);
            return memory;
        }

        #endregion
    }
}
