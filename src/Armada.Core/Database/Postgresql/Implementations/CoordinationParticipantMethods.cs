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
    /// PostgreSQL implementation of coordination participant (presence) database operations.
    /// </summary>
    public class CoordinationParticipantMethods : ICoordinationParticipantMethods
    {
        #region Private-Members

        private readonly NpgsqlDataSource _DataSource;
        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL coordination participant methods.
        /// </summary>
        /// <param name="dataSource">NpgsqlDataSource instance.</param>
        public CoordinationParticipantMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<CoordinationParticipant> UpsertAsync(CoordinationParticipant participant, CancellationToken token = default)
        {
            if (participant == null) throw new ArgumentNullException(nameof(participant));
            if (string.IsNullOrEmpty(participant.CoordinationRoomId)) throw new ArgumentException("CoordinationRoomId is required.");
            if (string.IsNullOrEmpty(participant.ParticipantKey)) throw new ArgumentException("ParticipantKey is required.");

            participant.LastSeenUtc = DateTime.UtcNow;
            participant.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO coordination_participants
                        (id, coordination_room_id, tenant_id, participant_key, display_name, last_seen_utc, created_utc, last_update_utc)
                        VALUES
                        (@id, @coordination_room_id, @tenant_id, @participant_key, @display_name, @last_seen_utc, @created_utc, @last_update_utc)
                        ON CONFLICT (coordination_room_id, participant_key) DO UPDATE SET
                            display_name = EXCLUDED.display_name,
                            last_seen_utc = EXCLUDED.last_seen_utc,
                            last_update_utc = EXCLUDED.last_update_utc;";
                    CoordinationParticipantColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_participants"), participant);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return participant;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationParticipant>> EnumerateByRoomAsync(string coordinationRoomId, int activeWithinMinutes = 15, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (activeWithinMinutes < 1) activeWithinMinutes = 1;

            DateTime cutoff = DateTime.UtcNow.AddMinutes(-activeWithinMinutes);
            List<CoordinationParticipant> results = new List<CoordinationParticipant>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_participants WHERE coordination_room_id = @coordination_room_id AND last_seen_utc >= @cutoff ORDER BY last_seen_utc DESC;";
                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_participants").Utc("@cutoff", "last_seen_utc", cutoff);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CoordinationParticipantColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationParticipant>> EnumerateAllInRoomAsync(string coordinationRoomId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));

            List<CoordinationParticipant> results = new List<CoordinationParticipant>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_participants WHERE coordination_room_id = @coordination_room_id ORDER BY last_seen_utc DESC;";
                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CoordinationParticipantColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<CoordinationParticipant?> ReadLatestByKeyAsync(string participantKey, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(participantKey)) throw new ArgumentNullException(nameof(participantKey));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_participants WHERE participant_key = @participant_key ORDER BY last_seen_utc DESC LIMIT 1;";
                    StoredValueBinder.Value(cmd, "@participant_key", participantKey);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationParticipantColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task PruneAsync(string coordinationRoomId, DateTime olderThanUtc, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM coordination_participants WHERE coordination_room_id = @coordination_room_id AND last_seen_utc < @older_than_utc;";
                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_participants").Utc("@older_than_utc", "last_seen_utc", olderThanUtc);
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
                    cmd.CommandText = "DELETE FROM coordination_participants WHERE coordination_room_id = @coordination_room_id;";
                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

    }
}
