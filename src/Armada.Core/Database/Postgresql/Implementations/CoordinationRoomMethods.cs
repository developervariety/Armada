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
                    CoordinationRoomColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_rooms"), room);
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
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationRoomColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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
                    StoredValueBinder.Value(cmd, "@key", key);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationRoomColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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
                    StoredValueBinder.Value(cmd, "@id", room.Id);
                    StoredValueBinder.Value(cmd, "@tenant_id", room.TenantId);
                    StoredValueBinder.Value(cmd, "@user_id", room.UserId);
                    StoredValueBinder.Value(cmd, "@key", room.Key);
                    StoredValueBinder.Value(cmd, "@name", room.Name);
                    StoredValueBinder.Value(cmd, "@description", room.Description);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_rooms").Utc("@last_update_utc", "last_update_utc", room.LastUpdateUtc);
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
                    StoredValueBinder.Value(cmd, "@id", id);
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
                            results.Add(CoordinationRoomColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        #endregion
    }
}
