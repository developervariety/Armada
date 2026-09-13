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
                        cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                        cmd.Parameters.AddWithValue("@id", id);
                        rows = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    if (rows == 1)
                    {
                        using (NpgsqlCommand cmd = conn.CreateCommand())
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
                            results.Add(FromReader(reader));
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
                del.Parameters.AddWithValue("@memory_id", memory.Id);
                await del.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            foreach (string tag in MemoryRows.TagsToWrite(memory))
            {
                using (NpgsqlCommand ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO memory_tags (memory_id, tag) VALUES (@memory_id, @tag);";
                    ins.Parameters.AddWithValue("@memory_id", memory.Id);
                    ins.Parameters.AddWithValue("@tag", tag);
                    await ins.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void Bind(NpgsqlCommand cmd, Memory memory, bool includeCreated)
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

        private static Memory FromReader(NpgsqlDataReader reader)
        {
            Memory memory = new Memory();
            memory.Id = reader["id"].ToString()!;
            memory.TenantId = NullableString(reader["tenant_id"]);
            memory.UserId = NullableString(reader["user_id"]);
            memory.Scope = MemoryRows.ParseEnum(reader["scope"], MemoryScopeEnum.TenantWide);
            memory.Type = MemoryRows.ParseEnum(reader["type"], MemoryTypeEnum.Semantic);
            memory.Topic = NullableString(reader["topic"]);
            memory.Key = NullableString(reader["memory_key"]);
            memory.Summary = NullableString(reader["summary"]);
            memory.Content = reader["content"]?.ToString() ?? String.Empty;
            memory.Salience = Convert.ToDouble(reader["salience"], CultureInfo.InvariantCulture);
            memory.Version = Convert.ToInt32(reader["version"], CultureInfo.InvariantCulture);
            memory.SourceKind = MemoryRows.ParseEnum(reader["source_kind"], MemorySourceKindEnum.Manual);
            memory.SourceVoyageId = NullableString(reader["source_voyage_id"]);
            memory.SourceMissionId = NullableString(reader["source_mission_id"]);
            memory.SourceVesselId = NullableString(reader["source_vessel_id"]);
            memory.SourceDetail = NullableString(reader["source_detail"]);
            memory.VesselId = NullableString(reader["vessel_id"]);
            memory.CreatedUtc = PostgresqlDatabaseDriver.ReadUtc(reader["created_utc"]);
            memory.LastUpdateUtc = PostgresqlDatabaseDriver.ReadUtc(reader["last_update_utc"]);
            return memory;
        }

        private static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return value.ToString();
        }

        #endregion
    }
}
