namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Microsoft.Data.SqlClient;

    /// <summary>SQL Server persistence for durable Judge follow-ups.</summary>
    public sealed class JudgeFollowUpMethods : IJudgeFollowUpMethods
    {
        private readonly SqlServerDatabaseDriver _Driver;

        /// <summary>Instantiate.</summary>
        public JudgeFollowUpMethods(SqlServerDatabaseDriver driver)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        /// <inheritdoc />
        public async Task<JudgeFollowUp> UpsertAsync(JudgeFollowUp item, CancellationToken token = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            item.LastUpdateUtc = DateTime.UtcNow;
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"MERGE INTO judge_follow_ups WITH (HOLDLOCK) AS target
                        USING (SELECT @judge_mission_id AS judge_mission_id) AS source
                        ON target.judge_mission_id = source.judge_mission_id
                        WHEN MATCHED THEN UPDATE SET tenant_id=@tenant_id, user_id=@user_id,
                         reviewed_mission_id=@reviewed_mission_id, voyage_id=@voyage_id, vessel_id=@vessel_id,
                         merge_entry_id=COALESCE(@merge_entry_id, target.merge_entry_id), judge_verdict=@judge_verdict,
                         suggested_follow_ups=@suggested_follow_ups, last_update_utc=@last_update_utc
                        WHEN NOT MATCHED THEN INSERT
                        (id, tenant_id, user_id, judge_mission_id, reviewed_mission_id, voyage_id, vessel_id,
                         merge_entry_id, judge_verdict, suggested_follow_ups, audit_verdict, audit_notes,
                         audit_recommended_action, audit_completed_utc, created_utc, last_update_utc)
                        VALUES (@id, @tenant_id, @user_id, @judge_mission_id, @reviewed_mission_id, @voyage_id, @vessel_id,
                         @merge_entry_id, @judge_verdict, @suggested_follow_ups, @audit_verdict, @audit_notes,
                         @audit_recommended_action, @audit_completed_utc, @created_utc, @last_update_utc);";
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
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
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
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE judge_follow_ups
                        SET merge_entry_id = @merge_entry_id, last_update_utc = @last_update_utc
                        WHERE id = @id AND merge_entry_id IS NULL;";
                    cmd.Parameters.AddWithValue("@id", followUpId);
                    cmd.Parameters.AddWithValue("@merge_entry_id", mergeEntryId);
                    cmd.Parameters.AddWithValue("@last_update_utc", SqlServerDatabaseDriver.ToIso8601(DateTime.UtcNow));
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
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE judge_follow_ups SET audit_verdict = @audit_verdict,
                        audit_notes = @audit_notes, audit_recommended_action = @audit_recommended_action,
                        audit_completed_utc = @audit_completed_utc, last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@audit_verdict", verdict);
                    cmd.Parameters.AddWithValue("@audit_notes", notes); cmd.Parameters.AddWithValue("@audit_recommended_action", (object?)recommendedAction ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_completed_utc", SqlServerDatabaseDriver.ToIso8601(completedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqlServerDatabaseDriver.ToIso8601(DateTime.UtcNow));
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
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM judge_follow_ups WHERE " + column + " = @value;";
                    cmd.Parameters.AddWithValue("@value", value);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        return await reader.ReadAsync(token).ConfigureAwait(false) ? FromReader(reader) : null;
                    }
                }
            }
        }

        private async Task<List<JudgeFollowUp>> EnumerateAsync(string condition, string? value, CancellationToken token)
        {
            List<JudgeFollowUp> results = new List<JudgeFollowUp>();
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM judge_follow_ups WHERE " + condition + " ORDER BY created_utc ASC, id ASC;";
                    if (value != null) cmd.Parameters.AddWithValue("@value", value);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) results.Add(FromReader(reader));
                    }
                }
            }
            return results;
        }

        private static void AddParameters(SqlCommand cmd, JudgeFollowUp item)
        {
            cmd.Parameters.AddWithValue("@id", item.Id); cmd.Parameters.AddWithValue("@tenant_id", (object?)item.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)item.UserId ?? DBNull.Value); cmd.Parameters.AddWithValue("@judge_mission_id", item.JudgeMissionId);
            cmd.Parameters.AddWithValue("@reviewed_mission_id", item.ReviewedMissionId); cmd.Parameters.AddWithValue("@voyage_id", (object?)item.VoyageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vessel_id", (object?)item.VesselId ?? DBNull.Value); cmd.Parameters.AddWithValue("@merge_entry_id", (object?)item.MergeEntryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@judge_verdict", item.JudgeVerdict); cmd.Parameters.AddWithValue("@suggested_follow_ups", (object?)item.SuggestedFollowUps ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@audit_verdict", item.AuditVerdict); cmd.Parameters.AddWithValue("@audit_notes", (object?)item.AuditNotes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@audit_recommended_action", (object?)item.AuditRecommendedAction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@audit_completed_utc", item.AuditCompletedUtc.HasValue ? SqlServerDatabaseDriver.ToIso8601(item.AuditCompletedUtc.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("@created_utc", SqlServerDatabaseDriver.ToIso8601(item.CreatedUtc)); cmd.Parameters.AddWithValue("@last_update_utc", SqlServerDatabaseDriver.ToIso8601(item.LastUpdateUtc));
        }

        private static JudgeFollowUp FromReader(SqlDataReader reader)
        {
            JudgeFollowUp item = new JudgeFollowUp();
            item.Id = reader["id"].ToString()!; item.TenantId = SqlServerDatabaseDriver.NullableString(reader["tenant_id"]); item.UserId = SqlServerDatabaseDriver.NullableString(reader["user_id"]);
            item.JudgeMissionId = reader["judge_mission_id"].ToString()!; item.ReviewedMissionId = reader["reviewed_mission_id"].ToString()!;
            item.VoyageId = SqlServerDatabaseDriver.NullableString(reader["voyage_id"]); item.VesselId = SqlServerDatabaseDriver.NullableString(reader["vessel_id"]); item.MergeEntryId = SqlServerDatabaseDriver.NullableString(reader["merge_entry_id"]);
            item.JudgeVerdict = reader["judge_verdict"].ToString()!; item.SuggestedFollowUps = SqlServerDatabaseDriver.NullableString(reader["suggested_follow_ups"]); item.AuditVerdict = reader["audit_verdict"].ToString()!;
            item.AuditNotes = SqlServerDatabaseDriver.NullableString(reader["audit_notes"]); item.AuditRecommendedAction = SqlServerDatabaseDriver.NullableString(reader["audit_recommended_action"]);
            item.AuditCompletedUtc = reader["audit_completed_utc"] == DBNull.Value ? null : SqlServerDatabaseDriver.FromIso8601(reader["audit_completed_utc"].ToString()!);
            item.CreatedUtc = SqlServerDatabaseDriver.FromIso8601(reader["created_utc"].ToString()!); item.LastUpdateUtc = SqlServerDatabaseDriver.FromIso8601(reader["last_update_utc"].ToString()!);
            return item;
        }
    }
}
