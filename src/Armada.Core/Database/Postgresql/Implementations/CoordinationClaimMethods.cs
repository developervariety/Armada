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
    /// PostgreSQL implementation of coordination claim database operations.
    /// </summary>
    public class CoordinationClaimMethods : ICoordinationClaimMethods
    {
        #region Private-Members

        private readonly NpgsqlDataSource _DataSource;
        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL coordination claim methods.
        /// </summary>
        /// <param name="dataSource">NpgsqlDataSource instance.</param>
        public CoordinationClaimMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<CoordinationClaim> CreateAsync(CoordinationClaim claim, CancellationToken token = default)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            claim.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO coordination_claims
                        (id, coordination_room_id, tenant_id, participant_key, display_name, subject_type, subject_id, note, status, expires_utc, created_utc, last_update_utc)
                        VALUES
                        (@id, @coordination_room_id, @tenant_id, @participant_key, @display_name, @subject_type, @subject_id, @note, @status, @expires_utc, @created_utc, @last_update_utc);";
                    BindClaim(cmd, claim);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return claim;
        }

        /// <inheritdoc />
        public async Task<CoordinationClaim?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_claims WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CoordinationClaimColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<CoordinationClaim> UpdateAsync(CoordinationClaim claim, CancellationToken token = default)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            claim.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"UPDATE coordination_claims SET
                        coordination_room_id = @coordination_room_id,
                        tenant_id = @tenant_id,
                        participant_key = @participant_key,
                        display_name = @display_name,
                        subject_type = @subject_type,
                        subject_id = @subject_id,
                        note = @note,
                        status = @status,
                        expires_utc = @expires_utc,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    BindClaim(cmd, claim);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return claim;
        }

        /// <inheritdoc />
        public async Task<List<CoordinationClaim>> EnumerateActiveAsync(CoordinationClaimSubjectEnum? subjectType = null, string? subjectId = null, CancellationToken token = default)
        {
            List<CoordinationClaim> results = new List<CoordinationClaim>();

            string sql = "SELECT * FROM coordination_claims WHERE status = 'Active' AND expires_utc > @now";
            if (subjectType.HasValue)
            {
                sql += " AND subject_type = @subject_type";
                if (!string.IsNullOrEmpty(subjectId)) sql += " AND subject_id = @subject_id";
            }

            sql += " ORDER BY created_utc ASC;";

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = sql;
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_claims").Utc("@now", "expires_utc", DateTime.UtcNow);
                    if (subjectType.HasValue) StoredValueBinder.Value(cmd, "@subject_type", subjectType.Value.ToString());
                    if (subjectType.HasValue && !string.IsNullOrEmpty(subjectId)) StoredValueBinder.Value(cmd, "@subject_id", subjectId!);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CoordinationClaimColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<int> ExtendActiveForParticipantAsync(string coordinationRoomId, string participantKey, DateTime newExpiresUtc, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (string.IsNullOrEmpty(participantKey)) throw new ArgumentNullException(nameof(participantKey));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"UPDATE coordination_claims SET
                        expires_utc = @new_expires_utc,
                        last_update_utc = @now
                        WHERE coordination_room_id = @coordination_room_id
                          AND participant_key = @participant_key
                          AND status = 'Active'
                          AND expires_utc > @now;";
                    StoredValueBinder.Value(cmd, "@coordination_room_id", coordinationRoomId);
                    StoredValueBinder.Value(cmd, "@participant_key", participantKey);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_claims").Utc("@new_expires_utc", "expires_utc", newExpiresUtc);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_claims").Utc("@now", "expires_utc", DateTime.UtcNow);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static void BindClaim(NpgsqlCommand cmd, CoordinationClaim claim)
        {
            CoordinationClaimColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "coordination_claims"), claim);
        }

        #endregion
    }
}
