namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQLite implementation of native captain memory persistence, including the memory_tags child table.
    /// </summary>
    public class MemoryMethods : IMemoryMethods
    {
        #region Private-Members

        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public MemoryMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction tx = conn.BeginTransaction())
                {
                    using (SqliteCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO memories (" + MemoryRows.InsertColumns + ") VALUES (" + MemoryRows.InsertValues + ");";
                        Bind(cmd, memory, true);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    await WriteTagsAsync(conn, tx, memory, token).ConfigureAwait(false);
                    tx.Commit();
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

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction tx = conn.BeginTransaction())
                {
                    int rows;
                    using (SqliteCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = MemoryRows.UpdateSql;
                        Bind(cmd, memory, false);
                        cmd.Parameters.AddWithValue("@expected_version", expectedVersion);
                        rows = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    if (rows != 1)
                    {
                        tx.Rollback();
                        return false;
                    }

                    await WriteTagsAsync(conn, tx, memory, token).ConfigureAwait(false);
                    tx.Commit();
                    return true;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction tx = conn.BeginTransaction())
                {
                    int rows;
                    using (SqliteCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM memories WHERE tenant_id = @tenant_id AND id = @id;";
                        cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                        cmd.Parameters.AddWithValue("@id", id);
                        rows = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    if (rows == 1)
                    {
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "DELETE FROM memory_tags WHERE memory_id = @id;";
                            cmd.Parameters.AddWithValue("@id", id);
                            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }

                    tx.Commit();
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

        private async Task<List<Memory>> QueryAsync(string memorySql, string tagSql, Action<SqliteCommand>? parameterize, CancellationToken token)
        {
            List<Memory> results = new List<Memory>();
            Dictionary<string, List<string>> tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = memorySql;
                    parameterize?.Invoke(cmd);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FromReader(reader));
                    }
                }

                if (results.Count == 0) return results;

                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = tagSql;
                    parameterize?.Invoke(cmd);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
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

        private static async Task WriteTagsAsync(SqliteConnection conn, SqliteTransaction tx, Memory memory, CancellationToken token)
        {
            using (SqliteCommand del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM memory_tags WHERE memory_id = @memory_id;";
                del.Parameters.AddWithValue("@memory_id", memory.Id);
                await del.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            foreach (string tag in MemoryRows.TagsToWrite(memory))
            {
                using (SqliteCommand ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO memory_tags (memory_id, tag) VALUES (@memory_id, @tag);";
                    ins.Parameters.AddWithValue("@memory_id", memory.Id);
                    ins.Parameters.AddWithValue("@tag", tag);
                    await ins.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void Bind(SqliteCommand cmd, Memory memory, bool includeCreated)
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
            if (includeCreated) cmd.Parameters.AddWithValue("@created_utc", SqliteDatabaseDriver.ToIso8601(memory.CreatedUtc));
            cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(memory.LastUpdateUtc));
        }

        private static Memory FromReader(SqliteDataReader reader)
        {
            Memory memory = new Memory();
            memory.Id = reader["id"].ToString()!;
            memory.TenantId = SqliteDatabaseDriver.NullableString(reader["tenant_id"]);
            memory.UserId = SqliteDatabaseDriver.NullableString(reader["user_id"]);
            memory.Scope = MemoryRows.ParseEnum(reader["scope"], MemoryScopeEnum.TenantWide);
            memory.Type = MemoryRows.ParseEnum(reader["type"], MemoryTypeEnum.Semantic);
            memory.Topic = SqliteDatabaseDriver.NullableString(reader["topic"]);
            memory.Key = SqliteDatabaseDriver.NullableString(reader["memory_key"]);
            memory.Summary = SqliteDatabaseDriver.NullableString(reader["summary"]);
            memory.Content = reader["content"]?.ToString() ?? String.Empty;
            memory.Salience = Convert.ToDouble(reader["salience"], CultureInfo.InvariantCulture);
            memory.Version = Convert.ToInt32(reader["version"], CultureInfo.InvariantCulture);
            memory.SourceKind = MemoryRows.ParseEnum(reader["source_kind"], MemorySourceKindEnum.Manual);
            memory.SourceVoyageId = SqliteDatabaseDriver.NullableString(reader["source_voyage_id"]);
            memory.SourceMissionId = SqliteDatabaseDriver.NullableString(reader["source_mission_id"]);
            memory.SourceVesselId = SqliteDatabaseDriver.NullableString(reader["source_vessel_id"]);
            memory.SourceDetail = SqliteDatabaseDriver.NullableString(reader["source_detail"]);
            memory.VesselId = SqliteDatabaseDriver.NullableString(reader["vessel_id"]);
            memory.CreatedUtc = SqliteDatabaseDriver.FromIso8601(reader["created_utc"].ToString()!);
            memory.LastUpdateUtc = SqliteDatabaseDriver.FromIso8601(reader["last_update_utc"].ToString()!);
            return memory;
        }

        #endregion
    }
}
