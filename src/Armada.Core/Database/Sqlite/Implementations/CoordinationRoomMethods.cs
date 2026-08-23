namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQLite implementation of coordination room database operations.
    /// </summary>
    public class CoordinationRoomMethods : ICoordinationRoomMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationRoomMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<CoordinationRoom> CreateAsync(CoordinationRoom room, CancellationToken token = default)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            room.LastUpdateUtc = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO coordination_rooms
                        (id, tenant_id, user_id, key, name, description, created_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @key, @name, @description, @created_utc, @last_update_utc);";
                    cmd.Parameters.AddWithValue("@id", room.Id);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)room.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object?)room.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@key", room.Key);
                    cmd.Parameters.AddWithValue("@name", room.Name);
                    cmd.Parameters.AddWithValue("@description", (object?)room.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_utc", SqliteDatabaseDriver.ToIso8601(room.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(room.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return room;
        }

        /// <inheritdoc />
        public async Task<CoordinationRoom?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_rooms WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CoordinationRoomFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<CoordinationRoom?> ReadByKeyAsync(string key, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_rooms WHERE key = @key;";
                    cmd.Parameters.AddWithValue("@key", key);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CoordinationRoomFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<CoordinationRoom> UpdateAsync(CoordinationRoom room, CancellationToken token = default)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            room.LastUpdateUtc = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE coordination_rooms SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        key = @key,
                        name = @name,
                        description = @description,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", room.Id);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)room.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object?)room.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@key", room.Key);
                    cmd.Parameters.AddWithValue("@name", room.Name);
                    cmd.Parameters.AddWithValue("@description", (object?)room.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(room.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return room;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM coordination_rooms WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<CoordinationRoom>> EnumerateAsync(CancellationToken token = default)
        {
            List<CoordinationRoom> results = new List<CoordinationRoom>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_rooms ORDER BY last_update_utc DESC;";
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CoordinationRoomFromReader(reader));
                    }
                }
            }

            return results;
        }
    }
}
