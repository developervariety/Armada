namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Npgsql;

    /// <summary>PostgreSQL persistence for durable Judge follow-ups.</summary>
    public sealed class JudgeFollowUpMethods : IJudgeFollowUpMethods
    {
        private readonly DatabaseSettings _Settings;

        /// <summary>Instantiate.</summary>
        public JudgeFollowUpMethods(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <inheritdoc />
        public async Task<JudgeFollowUp> UpsertAsync(JudgeFollowUp item, CancellationToken token = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            item.LastUpdateUtc = DateTime.UtcNow;
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO judge_follow_ups
                        (id, tenant_id, user_id, judge_mission_id, reviewed_mission_id, voyage_id, vessel_id,
                         merge_entry_id, judge_verdict, suggested_follow_ups, audit_verdict, audit_notes,
                         audit_recommended_action, audit_completed_utc, created_utc, last_update_utc)
                        VALUES (@id, @tenant_id, @user_id, @judge_mission_id, @reviewed_mission_id, @voyage_id, @vessel_id,
                         @merge_entry_id, @judge_verdict, @suggested_follow_ups, @audit_verdict, @audit_notes,
                         @audit_recommended_action, @audit_completed_utc, @created_utc, @last_update_utc)
                        ON CONFLICT(judge_mission_id) DO UPDATE SET
                         tenant_id = excluded.tenant_id, user_id = excluded.user_id,
                         reviewed_mission_id = excluded.reviewed_mission_id, voyage_id = excluded.voyage_id,
                         vessel_id = excluded.vessel_id,
                         merge_entry_id = COALESCE(excluded.merge_entry_id, judge_follow_ups.merge_entry_id),
                         judge_verdict = excluded.judge_verdict,
                         suggested_follow_ups = excluded.suggested_follow_ups,
                         last_update_utc = excluded.last_update_utc;";
                    AddParameters(cmd, item);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            JudgeFollowUp? stored = await ReadSingleAsync("judge_mission_id", item.JudgeMissionId, token).ConfigureAwait(false);
            return stored ?? throw new InvalidOperationException("Judge follow-up upsert did not return a row.");
        }

        /// <inheritdoc />
        public Task<JudgeFollowUp?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return ReadSingleAsync("id", id, token);
        }

        /// <inheritdoc />
        public Task<JudgeFollowUp?> ReadByJudgeMissionAsync(string judgeMissionId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(judgeMissionId)) throw new ArgumentNullException(nameof(judgeMissionId));
            return ReadSingleAsync("judge_mission_id", judgeMissionId, token);
        }

        /// <inheritdoc />
        public async Task<JudgeFollowUp?> ReadByMergeEntryAsync(string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(mergeEntryId)) throw new ArgumentNullException(nameof(mergeEntryId));
            List<JudgeFollowUp> matches = await EnumerateAsync("merge_entry_id = @value", mergeEntryId, token).ConfigureAwait(false);
            return matches.Count == 0 ? null : matches[matches.Count - 1];
        }

        /// <inheritdoc />
        public async Task<JudgeFollowUp> UpdateAsync(JudgeFollowUp item, CancellationToken token = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            item.LastUpdateUtc = DateTime.UtcNow;
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE judge_follow_ups SET tenant_id=@tenant_id, user_id=@user_id,
                        judge_mission_id=@judge_mission_id, reviewed_mission_id=@reviewed_mission_id,
                        voyage_id=@voyage_id, vessel_id=@vessel_id, merge_entry_id=@merge_entry_id,
                        judge_verdict=@judge_verdict, suggested_follow_ups=@suggested_follow_ups,
                        audit_verdict=@audit_verdict, audit_notes=@audit_notes,
                        audit_recommended_action=@audit_recommended_action, audit_completed_utc=@audit_completed_utc,
                        last_update_utc=@last_update_utc WHERE id=@id;";
                    AddParameters(cmd, item);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return item;
        }

        /// <inheritdoc />
        public async Task<bool> TryAssociateAsync(string followUpId, string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(followUpId)) throw new ArgumentNullException(nameof(followUpId));
            if (String.IsNullOrWhiteSpace(mergeEntryId)) throw new ArgumentNullException(nameof(mergeEntryId));
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE judge_follow_ups
                        SET merge_entry_id = @merge_entry_id, last_update_utc = @last_update_utc
                        WHERE id = @id AND merge_entry_id IS NULL;";
                    cmd.Parameters.AddWithValue("@id", followUpId);
                    cmd.Parameters.AddWithValue("@merge_entry_id", mergeEntryId);
                    cmd.Parameters.AddWithValue("@last_update_utc", DateTime.UtcNow);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        /// <inheritdoc />
        public async Task<JudgeFollowUp> CompleteAuditAsync(string id, string verdict, string notes, string? recommendedAction, DateTime completedUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (String.IsNullOrWhiteSpace(verdict)) throw new ArgumentNullException(nameof(verdict));
            if (notes == null) throw new ArgumentNullException(nameof(notes));
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE judge_follow_ups SET audit_verdict = @audit_verdict,
                        audit_notes = @audit_notes, audit_recommended_action = @audit_recommended_action,
                        audit_completed_utc = @audit_completed_utc, last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@audit_verdict", verdict);
                    cmd.Parameters.AddWithValue("@audit_notes", notes); cmd.Parameters.AddWithValue("@audit_recommended_action", (object?)recommendedAction ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_completed_utc", completedUtc); cmd.Parameters.AddWithValue("@last_update_utc", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            JudgeFollowUp? stored = await ReadAsync(id, token).ConfigureAwait(false);
            return stored ?? throw new InvalidOperationException("Judge follow-up audit completion did not return a row.");
        }

        /// <inheritdoc />
        public Task<List<JudgeFollowUp>> EnumeratePendingAsync(string? vesselId = null, CancellationToken token = default)
        {
            return EnumerateAsync(vesselId == null
                ? "audit_verdict = 'Pending' AND audit_completed_utc IS NULL"
                : "audit_verdict = 'Pending' AND audit_completed_utc IS NULL AND vessel_id = @value", vesselId, token);
        }

        /// <inheritdoc />
        public Task<List<JudgeFollowUp>> EnumerateUnassociatedAsync(string? vesselId = null, CancellationToken token = default)
        {
            return EnumerateAsync(vesselId == null ? "merge_entry_id IS NULL" : "merge_entry_id IS NULL AND vessel_id = @value", vesselId, token);
        }

        /// <inheritdoc />
        public Task<List<JudgeFollowUp>> EnumerateUnassociatedByReviewedMissionAsync(string reviewedMissionId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(reviewedMissionId)) throw new ArgumentNullException(nameof(reviewedMissionId));
            return EnumerateAsync("merge_entry_id IS NULL AND reviewed_mission_id = @value", reviewedMissionId, token);
        }

        /// <inheritdoc />
        public Task<List<JudgeFollowUp>> EnumerateUnassociatedByJudgeMissionAsync(string judgeMissionId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(judgeMissionId)) throw new ArgumentNullException(nameof(judgeMissionId));
            return EnumerateAsync("merge_entry_id IS NULL AND judge_mission_id = @value", judgeMissionId, token);
        }

        private async Task<JudgeFollowUp?> ReadSingleAsync(string column, string value, CancellationToken token)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM judge_follow_ups WHERE " + column + " = @value;";
                    cmd.Parameters.AddWithValue("@value", value);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        return await reader.ReadAsync(token).ConfigureAwait(false) ? FromReader(reader) : null;
                    }
                }
            }
        }

        private async Task<List<JudgeFollowUp>> EnumerateAsync(string condition, string? value, CancellationToken token)
        {
            List<JudgeFollowUp> results = new List<JudgeFollowUp>();
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM judge_follow_ups WHERE " + condition + " ORDER BY created_utc ASC, id ASC;";
                    if (value != null) cmd.Parameters.AddWithValue("@value", value);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) results.Add(FromReader(reader));
                    }
                }
            }
            return results;
        }

        private static void AddParameters(NpgsqlCommand cmd, JudgeFollowUp item)
        {
            cmd.Parameters.AddWithValue("@id", item.Id);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)item.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)item.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@judge_mission_id", item.JudgeMissionId);
            cmd.Parameters.AddWithValue("@reviewed_mission_id", item.ReviewedMissionId);
            cmd.Parameters.AddWithValue("@voyage_id", (object?)item.VoyageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vessel_id", (object?)item.VesselId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@merge_entry_id", (object?)item.MergeEntryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@judge_verdict", item.JudgeVerdict);
            cmd.Parameters.AddWithValue("@suggested_follow_ups", (object?)item.SuggestedFollowUps ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@audit_verdict", item.AuditVerdict);
            cmd.Parameters.AddWithValue("@audit_notes", (object?)item.AuditNotes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@audit_recommended_action", (object?)item.AuditRecommendedAction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@audit_completed_utc", (object?)item.AuditCompletedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_utc", item.CreatedUtc);
            cmd.Parameters.AddWithValue("@last_update_utc", item.LastUpdateUtc);
        }

        private static JudgeFollowUp FromReader(NpgsqlDataReader reader)
        {
            JudgeFollowUp item = new JudgeFollowUp();
            item.Id = reader["id"].ToString()!;
            item.TenantId = NullableString(reader["tenant_id"]); item.UserId = NullableString(reader["user_id"]);
            item.JudgeMissionId = reader["judge_mission_id"].ToString()!; item.ReviewedMissionId = reader["reviewed_mission_id"].ToString()!;
            item.VoyageId = NullableString(reader["voyage_id"]); item.VesselId = NullableString(reader["vessel_id"]); item.MergeEntryId = NullableString(reader["merge_entry_id"]);
            item.JudgeVerdict = reader["judge_verdict"].ToString()!; item.SuggestedFollowUps = NullableString(reader["suggested_follow_ups"]);
            item.AuditVerdict = reader["audit_verdict"].ToString()!; item.AuditNotes = NullableString(reader["audit_notes"]); item.AuditRecommendedAction = NullableString(reader["audit_recommended_action"]);
            item.AuditCompletedUtc = PostgresqlDatabaseDriver.ReadUtcNullable(reader["audit_completed_utc"]);
            item.CreatedUtc = PostgresqlDatabaseDriver.ReadUtc(reader["created_utc"]); item.LastUpdateUtc = PostgresqlDatabaseDriver.ReadUtc(reader["last_update_utc"]);
            return item;
        }

        private static string? NullableString(object value) => value == DBNull.Value ? null : value.ToString();
    }
}
