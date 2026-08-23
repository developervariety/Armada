namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// PostgreSQL implementation of coordination room database operations.
    /// </summary>
    public class CoordinationRoomMethods : ICoordinationRoomMethods
    {
        #region Private-Members

        private readonly NpgsqlDataSource _DataSource;
        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL coordination room methods.
        /// </summary>
        /// <param name="dataSource">NpgsqlDataSource instance.</param>
        public CoordinationRoomMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<CoordinationRoom> CreateAsync(CoordinationRoom room, CancellationToken token = default)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            room.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO coordination_rooms (id, tenant_id, user_id, key, name, description, created_utc, last_update_utc)
                        VALUES (@id, @tenant_id, @user_id, @key, @name, @description, @created_utc, @last_update_utc)
                        ON CONFLICT (key) DO NOTHING;";
                    cmd.Parameters.AddWithValue("@id", room.Id);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)room.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object?)room.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@key", room.Key);
                    cmd.Parameters.AddWithValue("@name", room.Name);
                    cmd.Parameters.AddWithValue("@description", (object?)room.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_utc", ToIso8601(room.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(room.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return room;
        }

        /// <inheritdoc />
        public async Task<CoordinationRoom?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_rooms WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return RoomFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<CoordinationRoom?> ReadByKeyAsync(string key, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_rooms WHERE key = @key;";
                    cmd.Parameters.AddWithValue("@key", key);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return RoomFromReader(reader);
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

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
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
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(room.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return room;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
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

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_rooms ORDER BY last_update_utc DESC;";
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(RoomFromReader(reader));
                    }
                }
            }

            return results;
        }

        #endregion

        #region Private-Methods

        private static string ToIso8601(DateTime dt)
        {
            return dt.ToUniversalTime().ToString(_Iso8601Format, CultureInfo.InvariantCulture);
        }

        private static DateTime FromIso8601(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        private static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            return string.IsNullOrEmpty(str) ? null : str;
        }

        private static CoordinationRoom RoomFromReader(NpgsqlDataReader reader)
        {
            CoordinationRoom room = new CoordinationRoom();
            room.Id = reader["id"].ToString()!;
            room.TenantId = NullableString(reader["tenant_id"]);
            room.UserId = NullableString(reader["user_id"]);
            room.Key = reader["key"].ToString()!;
            room.Name = reader["name"].ToString()!;
            room.Description = NullableString(reader["description"]);
            room.CreatedUtc = FromIso8601(reader["created_utc"].ToString()!);
            room.LastUpdateUtc = FromIso8601(reader["last_update_utc"].ToString()!);
            return room;
        }

        #endregion
    }
}
