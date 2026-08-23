namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQLite implementation of coordination claim database operations.
    /// </summary>
    public class CoordinationClaimMethods : ICoordinationClaimMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationClaimMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<CoordinationClaim> CreateAsync(CoordinationClaim claim, CancellationToken token = default)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            claim.LastUpdateUtc = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO coordination_claims
                        (id, coordination_room_id, tenant_id, participant_key, display_name, subject_type, subject_id, note, status, expires_utc, created_utc, last_update_utc)
                        VALUES
                        (@id, @coordination_room_id, @tenant_id, @participant_key, @display_name, @subject_type, @subject_id, @note, @status, @expires_utc, @created_utc, @last_update_utc);";
                    cmd.Parameters.AddWithValue("@id", claim.Id);
                    cmd.Parameters.AddWithValue("@coordination_room_id", claim.CoordinationRoomId);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)claim.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@participant_key", claim.ParticipantKey);
                    cmd.Parameters.AddWithValue("@display_name", claim.DisplayName);
                    cmd.Parameters.AddWithValue("@subject_type", claim.SubjectType.ToString());
                    cmd.Parameters.AddWithValue("@subject_id", claim.SubjectId);
                    cmd.Parameters.AddWithValue("@note", (object?)claim.Note ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@status", claim.Status.ToString());
                    cmd.Parameters.AddWithValue("@expires_utc", SqliteDatabaseDriver.ToIso8601(claim.ExpiresUtc));
                    cmd.Parameters.AddWithValue("@created_utc", SqliteDatabaseDriver.ToIso8601(claim.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(claim.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return claim;
        }

        /// <inheritdoc />
        public async Task<CoordinationClaim?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM coordination_claims WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CoordinationClaimFromReader(reader);
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

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
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
                    cmd.Parameters.AddWithValue("@id", claim.Id);
                    cmd.Parameters.AddWithValue("@coordination_room_id", claim.CoordinationRoomId);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)claim.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@participant_key", claim.ParticipantKey);
                    cmd.Parameters.AddWithValue("@display_name", claim.DisplayName);
                    cmd.Parameters.AddWithValue("@subject_type", claim.SubjectType.ToString());
                    cmd.Parameters.AddWithValue("@subject_id", claim.SubjectId);
                    cmd.Parameters.AddWithValue("@note", (object?)claim.Note ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@status", claim.Status.ToString());
                    cmd.Parameters.AddWithValue("@expires_utc", SqliteDatabaseDriver.ToIso8601(claim.ExpiresUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(claim.LastUpdateUtc));
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
                if (!String.IsNullOrEmpty(subjectId)) sql += " AND subject_id = @subject_id";
            }

            sql += " ORDER BY created_utc ASC;";

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@now", SqliteDatabaseDriver.ToIso8601(DateTime.UtcNow));
                    if (subjectType.HasValue) cmd.Parameters.AddWithValue("@subject_type", subjectType.Value.ToString());
                    if (subjectType.HasValue && !String.IsNullOrEmpty(subjectId)) cmd.Parameters.AddWithValue("@subject_id", subjectId!);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CoordinationClaimFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<int> ExtendActiveForParticipantAsync(string coordinationRoomId, string participantKey, DateTime newExpiresUtc, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (String.IsNullOrEmpty(participantKey)) throw new ArgumentNullException(nameof(participantKey));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE coordination_claims SET
                        expires_utc = @new_expires_utc,
                        last_update_utc = @now
                        WHERE coordination_room_id = @coordination_room_id
                          AND participant_key = @participant_key
                          AND status = 'Active'
                          AND expires_utc > @now;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    cmd.Parameters.AddWithValue("@participant_key", participantKey);
                    cmd.Parameters.AddWithValue("@new_expires_utc", SqliteDatabaseDriver.ToIso8601(newExpiresUtc));
                    cmd.Parameters.AddWithValue("@now", SqliteDatabaseDriver.ToIso8601(DateTime.UtcNow));
                    object result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
                }
            }
        }
    }
}
