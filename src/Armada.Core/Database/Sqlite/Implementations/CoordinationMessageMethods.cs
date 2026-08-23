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
    /// SQLite implementation of coordination message database operations.
    /// </summary>
    public class CoordinationMessageMethods : ICoordinationMessageMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationMessageMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<CoordinationMessage> CreateAsync(CoordinationMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO coordination_messages
                        (id, coordination_room_id, tenant_id, author_type, author_id, author_name, content,
                         voyage_id, mission_id, vessel_id, incident_id, to_participant_key, created_utc, last_update_utc)
                        VALUES
                        (@id, @coordination_room_id, @tenant_id, @author_type, @author_id, @author_name, @content,
                         @voyage_id, @mission_id, @vessel_id, @incident_id, @to_participant_key, @created_utc, @last_update_utc);";
                    cmd.Parameters.AddWithValue("@id", message.Id);
                    cmd.Parameters.AddWithValue("@coordination_room_id", message.CoordinationRoomId);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)message.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_type", message.AuthorType.ToString());
                    cmd.Parameters.AddWithValue("@author_id", (object?)message.AuthorId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_name", message.AuthorName);
                    cmd.Parameters.AddWithValue("@content", message.Content);
                    cmd.Parameters.AddWithValue("@voyage_id", (object?)message.VoyageId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mission_id", (object?)message.MissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@vessel_id", (object?)message.VesselId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@incident_id", (object?)message.IncidentId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@to_participant_key", (object?)message.ToParticipantKey ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_utc", SqliteDatabaseDriver.ToIso8601(message.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(message.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task<CoordinationMessage?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_messages WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CoordinationMessageFromReader(reader);
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

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
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
                    cmd.Parameters.AddWithValue("@id", message.Id);
                    cmd.Parameters.AddWithValue("@coordination_room_id", message.CoordinationRoomId);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)message.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_type", message.AuthorType.ToString());
                    cmd.Parameters.AddWithValue("@author_id", (object?)message.AuthorId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_name", message.AuthorName);
                    cmd.Parameters.AddWithValue("@content", message.Content);
                    cmd.Parameters.AddWithValue("@voyage_id", (object?)message.VoyageId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mission_id", (object?)message.MissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@vessel_id", (object?)message.VesselId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@incident_id", (object?)message.IncidentId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@to_participant_key", (object?)message.ToParticipantKey ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(message.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationMessage>> EnumerateByVoyageAsync(string voyageId, DateTime? afterUtc = null, int limit = 20, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            List<CoordinationMessage> results = new List<CoordinationMessage>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = afterUtc.HasValue
                        ? "SELECT * FROM coordination_messages WHERE voyage_id = @voyage_id AND created_utc > @after_utc ORDER BY created_utc DESC LIMIT @limit;"
                        : "SELECT * FROM coordination_messages WHERE voyage_id = @voyage_id ORDER BY created_utc DESC LIMIT @limit;";
                    cmd.Parameters.AddWithValue("@voyage_id", voyageId);
                    if (afterUtc.HasValue) cmd.Parameters.AddWithValue("@after_utc", SqliteDatabaseDriver.ToIso8601(afterUtc.Value));
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CoordinationMessageFromReader(reader));
                    }
                }
            }

            return results;
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
                    cmd.CommandText = "DELETE FROM coordination_messages WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task DeleteByRoomAsync(string coordinationRoomId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM coordination_messages WHERE coordination_room_id = @coordination_room_id;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<CoordinationMessage>> EnumerateByRoomAsync(string coordinationRoomId, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (limit < 1) limit = 1;
            if (limit > 1000) limit = 1000;

            List<CoordinationMessage> results = new List<CoordinationMessage>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    if (afterUtc.HasValue)
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id AND created_utc > @after_utc ORDER BY created_utc ASC LIMIT @limit;";
                        cmd.Parameters.AddWithValue("@after_utc", SqliteDatabaseDriver.ToIso8601(afterUtc.Value));
                    }
                    else
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id ORDER BY created_utc DESC LIMIT @limit;";
                    }

                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CoordinationMessageFromReader(reader));
                    }
                }
            }

            if (!afterUtc.HasValue) results.Reverse();
            return results;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationMessage>> EnumerateVisibleToAsync(string coordinationRoomId, string? participantKey, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (String.IsNullOrEmpty(participantKey)) return await EnumerateByRoomAsync(coordinationRoomId, afterUtc, limit, token).ConfigureAwait(false);
            if (limit < 1) limit = 1;
            if (limit > 1000) limit = 1000;

            List<CoordinationMessage> results = new List<CoordinationMessage>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    if (afterUtc.HasValue)
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id AND created_utc > @after_utc AND (to_participant_key IS NULL OR to_participant_key = @participant_key) ORDER BY created_utc ASC LIMIT @limit;";
                        cmd.Parameters.AddWithValue("@after_utc", SqliteDatabaseDriver.ToIso8601(afterUtc.Value));
                    }
                    else
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id AND (to_participant_key IS NULL OR to_participant_key = @participant_key) ORDER BY created_utc DESC LIMIT @limit;";
                    }

                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    cmd.Parameters.AddWithValue("@participant_key", participantKey!);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CoordinationMessageFromReader(reader));
                    }
                }
            }

            if (!afterUtc.HasValue) results.Reverse();
            return results;
        }
    }
}
