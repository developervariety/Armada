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
    /// SQLite implementation of coordination participant (presence) database operations.
    /// </summary>
    public class CoordinationParticipantMethods : ICoordinationParticipantMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationParticipantMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<CoordinationParticipant> UpsertAsync(CoordinationParticipant participant, CancellationToken token = default)
        {
            if (participant == null) throw new ArgumentNullException(nameof(participant));
            if (String.IsNullOrEmpty(participant.CoordinationRoomId)) throw new ArgumentException("CoordinationRoomId is required.");
            if (String.IsNullOrEmpty(participant.ParticipantKey)) throw new ArgumentException("ParticipantKey is required.");

            participant.LastSeenUtc = DateTime.UtcNow;
            participant.LastUpdateUtc = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE coordination_participants SET
                        display_name = @display_name,
                        last_seen_utc = @last_seen_utc,
                        last_update_utc = @last_update_utc
                        WHERE coordination_room_id = @coordination_room_id AND participant_key = @participant_key;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", participant.CoordinationRoomId);
                    cmd.Parameters.AddWithValue("@participant_key", participant.ParticipantKey);
                    cmd.Parameters.AddWithValue("@display_name", participant.DisplayName);
                    cmd.Parameters.AddWithValue("@last_seen_utc", SqliteDatabaseDriver.ToIso8601(participant.LastSeenUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(participant.LastUpdateUtc));
                    int updated = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                    if (updated < 1)
                    {
                        cmd.CommandText = @"INSERT INTO coordination_participants
                            (id, coordination_room_id, tenant_id, participant_key, display_name, last_seen_utc, created_utc, last_update_utc)
                            VALUES
                            (@id, @coordination_room_id, @tenant_id, @participant_key, @display_name, @last_seen_utc, @created_utc, @last_update_utc);";
                        cmd.Parameters.AddWithValue("@id", participant.Id);
                        cmd.Parameters.AddWithValue("@tenant_id", (object?)participant.TenantId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@created_utc", SqliteDatabaseDriver.ToIso8601(participant.CreatedUtc));
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }

            return participant;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationParticipant>> EnumerateByRoomAsync(string coordinationRoomId, int activeWithinMinutes = 15, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (activeWithinMinutes < 1) activeWithinMinutes = 1;

            DateTime cutoff = DateTime.UtcNow.AddMinutes(-activeWithinMinutes);
            List<CoordinationParticipant> results = new List<CoordinationParticipant>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_participants WHERE coordination_room_id = @coordination_room_id AND last_seen_utc >= @cutoff ORDER BY last_seen_utc DESC;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    cmd.Parameters.AddWithValue("@cutoff", SqliteDatabaseDriver.ToIso8601(cutoff));
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CoordinationParticipantFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationParticipant>> EnumerateAllInRoomAsync(string coordinationRoomId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));

            List<CoordinationParticipant> results = new List<CoordinationParticipant>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_participants WHERE coordination_room_id = @coordination_room_id ORDER BY last_seen_utc DESC;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CoordinationParticipantFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<CoordinationParticipant?> ReadLatestByKeyAsync(string participantKey, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(participantKey)) throw new ArgumentNullException(nameof(participantKey));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_participants WHERE participant_key = @participant_key ORDER BY last_seen_utc DESC LIMIT 1;";
                    cmd.Parameters.AddWithValue("@participant_key", participantKey);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CoordinationParticipantFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task PruneAsync(string coordinationRoomId, DateTime olderThanUtc, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM coordination_participants WHERE coordination_room_id = @coordination_room_id AND last_seen_utc < @older_than_utc;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    cmd.Parameters.AddWithValue("@older_than_utc", SqliteDatabaseDriver.ToIso8601(olderThanUtc));
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
                    cmd.CommandText = "DELETE FROM coordination_participants WHERE coordination_room_id = @coordination_room_id;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }
    }
}
