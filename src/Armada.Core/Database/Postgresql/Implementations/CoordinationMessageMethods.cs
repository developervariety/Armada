namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// PostgreSQL implementation of coordination message database operations.
    /// </summary>
    public class CoordinationMessageMethods : ICoordinationMessageMethods
    {
        #region Private-Members

        private readonly NpgsqlDataSource _DataSource;
        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL coordination message methods.
        /// </summary>
        /// <param name="dataSource">NpgsqlDataSource instance.</param>
        public CoordinationMessageMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<CoordinationMessage> CreateAsync(CoordinationMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO coordination_messages
                        (id, coordination_room_id, tenant_id, author_type, author_id, author_name, content,
                         voyage_id, mission_id, vessel_id, incident_id, to_participant_key, created_utc, last_update_utc)
                        VALUES
                        (@id, @coordination_room_id, @tenant_id, @author_type, @author_id, @author_name, @content,
                         @voyage_id, @mission_id, @vessel_id, @incident_id, @to_participant_key, @created_utc, @last_update_utc);";
                    CoordinationMessageColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_messages"), message);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task<CoordinationMessage?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_messages WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationMessageColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<CoordinationMessage> UpdateAsync(CoordinationMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"UPDATE coordination_messages SET
                        coordination_room_id = @coordination_room_id,
                        tenant_id = @tenant_id,
                        author_type = @author_type,
                        author_id = @author_id,
                        author_name = @author_name,
                        content = @content,
                        voyage_id = @voyage_id,
                        mission_id = @mission_id,
                        vessel_id = @vessel_id,
                        incident_id = @incident_id,
                        to_participant_key = @to_participant_key,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    CoordinationMessageColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_messages"), message);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationMessage>> EnumerateByVoyageAsync(string voyageId, DateTime? afterUtc = null, int limit = 20, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            List<CoordinationMessage> results = new List<CoordinationMessage>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = afterUtc.HasValue
                        ? "SELECT * FROM coordination_messages WHERE voyage_id = @voyage_id AND created_utc > @after_utc ORDER BY created_utc DESC LIMIT @limit;"
                        : "SELECT * FROM coordination_messages WHERE voyage_id = @voyage_id ORDER BY created_utc DESC LIMIT @limit;";
                    StoredValueBinder.Value(cmd, "@voyage_id", voyageId);
                    if (afterUtc.HasValue) PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_messages").Utc("@after_utc", "created_utc", afterUtc.Value);
                    StoredValueBinder.Value(cmd, "@limit", limit);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CoordinationMessageColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
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
                    cmd.CommandText = "DELETE FROM coordination_messages WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task DeleteByRoomAsync(string coordinationRoomId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM coordination_messages WHERE coordination_room_id = @coordination_room_id;";
                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<CoordinationMessage>> EnumerateByRoomAsync(string coordinationRoomId, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (limit < 1) limit = 1;
            if (limit > 1000) limit = 1000;

            List<CoordinationMessage> results = new List<CoordinationMessage>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    if (afterUtc.HasValue)
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id AND created_utc > @after_utc ORDER BY created_utc ASC LIMIT @limit;";
                        PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_messages").Utc("@after_utc", "created_utc", afterUtc.Value);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id ORDER BY created_utc DESC LIMIT @limit;";
                    }

                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    StoredValueBinder.Value(cmd, "@limit", limit);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CoordinationMessageColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            if (!afterUtc.HasValue) results.Reverse();
            return results;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationMessage>> EnumerateVisibleToAsync(string coordinationRoomId, string? participantKey, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (string.IsNullOrEmpty(participantKey)) return await EnumerateByRoomAsync(coordinationRoomId, afterUtc, limit, token).ConfigureAwait(false);
            if (limit < 1) limit = 1;
            if (limit > 1000) limit = 1000;

            List<CoordinationMessage> results = new List<CoordinationMessage>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    if (afterUtc.HasValue)
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id AND created_utc > @after_utc AND (to_participant_key IS NULL OR to_participant_key = @participant_key) ORDER BY created_utc ASC LIMIT @limit;";
                        PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_messages").Utc("@after_utc", "created_utc", afterUtc.Value);
                    }
                    else
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id AND (to_participant_key IS NULL OR to_participant_key = @participant_key) ORDER BY created_utc DESC LIMIT @limit;";
                    }

                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    StoredValueBinder.Value(cmd, "@participant_key", participantKey!);
                    StoredValueBinder.Value(cmd, "@limit", limit);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CoordinationMessageColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            if (!afterUtc.HasValue) results.Reverse();
            return results;
        }

        #endregion
    }
}
