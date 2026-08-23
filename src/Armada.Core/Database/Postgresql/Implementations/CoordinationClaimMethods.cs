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
        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

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
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return ClaimFromReader(reader);
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
                    cmd.Parameters.AddWithValue("@now", ToIso8601(DateTime.UtcNow));
                    if (subjectType.HasValue) cmd.Parameters.AddWithValue("@subject_type", subjectType.Value.ToString());
                    if (subjectType.HasValue && !string.IsNullOrEmpty(subjectId)) cmd.Parameters.AddWithValue("@subject_id", subjectId!);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(ClaimFromReader(reader));
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
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    cmd.Parameters.AddWithValue("@participant_key", participantKey);
                    cmd.Parameters.AddWithValue("@new_expires_utc", ToIso8601(newExpiresUtc));
                    cmd.Parameters.AddWithValue("@now", ToIso8601(DateTime.UtcNow));
                    object result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static void BindClaim(NpgsqlCommand cmd, CoordinationClaim claim)
        {
            cmd.Parameters.AddWithValue("@id", claim.Id);
            cmd.Parameters.AddWithValue("@coordination_room_id", claim.CoordinationRoomId);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)claim.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@participant_key", claim.ParticipantKey);
            cmd.Parameters.AddWithValue("@display_name", claim.DisplayName);
            cmd.Parameters.AddWithValue("@subject_type", claim.SubjectType.ToString());
            cmd.Parameters.AddWithValue("@subject_id", claim.SubjectId);
            cmd.Parameters.AddWithValue("@note", (object?)claim.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", claim.Status.ToString());
            cmd.Parameters.AddWithValue("@expires_utc", ToIso8601(claim.ExpiresUtc));
            cmd.Parameters.AddWithValue("@created_utc", ToIso8601(claim.CreatedUtc));
            cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(claim.LastUpdateUtc));
        }

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

        private static CoordinationClaim ClaimFromReader(NpgsqlDataReader reader)
        {
            CoordinationClaim claim = new CoordinationClaim();
            claim.Id = reader["id"].ToString()!;
            claim.CoordinationRoomId = reader["coordination_room_id"].ToString()!;
            claim.TenantId = NullableString(reader["tenant_id"]);
            claim.ParticipantKey = reader["participant_key"].ToString()!;
            claim.DisplayName = reader["display_name"].ToString()!;
            claim.SubjectType = Enum.Parse<CoordinationClaimSubjectEnum>(reader["subject_type"].ToString()!);
            claim.SubjectId = reader["subject_id"].ToString()!;
            claim.Note = NullableString(reader["note"]);
            claim.Status = Enum.Parse<CoordinationClaimStatusEnum>(reader["status"].ToString()!);
            claim.ExpiresUtc = FromIso8601(reader["expires_utc"].ToString()!);
            claim.CreatedUtc = FromIso8601(reader["created_utc"].ToString()!);
            claim.LastUpdateUtc = FromIso8601(reader["last_update_utc"].ToString()!);
            return claim;
        }

        #endregion
    }
}
