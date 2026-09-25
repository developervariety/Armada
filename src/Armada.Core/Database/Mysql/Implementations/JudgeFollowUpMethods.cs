namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using MySqlConnector;

    /// <summary>MySQL persistence for durable Judge follow-ups.</summary>
    public sealed class JudgeFollowUpMethods : IJudgeFollowUpMethods
    {
        private readonly string _ConnectionString;

        /// <summary>Instantiate.</summary>
        public JudgeFollowUpMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<JudgeFollowUp> UpsertAsync(JudgeFollowUp item, CancellationToken token = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            item.LastUpdateUtc = DateTime.UtcNow;
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO judge_follow_ups
                        (id, tenant_id, user_id, judge_mission_id, reviewed_mission_id, voyage_id, vessel_id,
                         merge_entry_id, judge_verdict, suggested_follow_ups, audit_verdict, audit_notes,
                         audit_recommended_action, audit_completed_utc, created_utc, last_update_utc)
                        VALUES (@id, @tenant_id, @user_id, @judge_mission_id, @reviewed_mission_id, @voyage_id, @vessel_id,
                         @merge_entry_id, @judge_verdict, @suggested_follow_ups, @audit_verdict, @audit_notes,
                         @audit_recommended_action, @audit_completed_utc, @created_utc, @last_update_utc)
                        ON DUPLICATE KEY UPDATE tenant_id=VALUES(tenant_id), user_id=VALUES(user_id),
                         reviewed_mission_id=VALUES(reviewed_mission_id), voyage_id=VALUES(voyage_id),
                         vessel_id=VALUES(vessel_id), merge_entry_id=COALESCE(VALUES(merge_entry_id), merge_entry_id),
                         judge_verdict=VALUES(judge_verdict), suggested_follow_ups=VALUES(suggested_follow_ups),
                         last_update_utc=VALUES(last_update_utc);";
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE judge_follow_ups
                        SET merge_entry_id = @merge_entry_id, last_update_utc = @last_update_utc
                        WHERE id = @id AND merge_entry_id IS NULL;";
                    StoredValueBinder.Value(cmd, "@id", followUpId);
                    StoredValueBinder.Value(cmd, "@merge_entry_id", mergeEntryId);
                    MysqlDatabaseDriver.StoredBinder.For(cmd, "judge_follow_ups").Utc("@last_update_utc", "last_update_utc", DateTime.UtcNow);
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE judge_follow_ups SET audit_verdict = @audit_verdict,
                        audit_notes = @audit_notes, audit_recommended_action = @audit_recommended_action,
                        audit_completed_utc = @audit_completed_utc, last_update_utc = @last_update_utc WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id); StoredValueBinder.Value(cmd, "@audit_verdict", verdict);
                    StoredValueBinder.Value(cmd, "@audit_notes", notes); StoredValueBinder.Value(cmd, "@audit_recommended_action", recommendedAction);
                    MysqlDatabaseDriver.StoredBinder.For(cmd, "judge_follow_ups").Utc("@audit_completed_utc", "audit_completed_utc", completedUtc); MysqlDatabaseDriver.StoredBinder.For(cmd, "judge_follow_ups").Utc("@last_update_utc", "last_update_utc", DateTime.UtcNow);
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM judge_follow_ups WHERE " + column + " = @value;";
                    StoredValueBinder.Value(cmd, "@value", value);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        return await reader.ReadAsync(token).ConfigureAwait(false) ? JudgeFollowUpColumns.Read(reader, MysqlDatabaseDriver.StoredValues) : null;
                    }
                }
            }
        }

        private async Task<List<JudgeFollowUp>> EnumerateAsync(string condition, string? value, CancellationToken token)
        {
            List<JudgeFollowUp> results = new List<JudgeFollowUp>();
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM judge_follow_ups WHERE " + condition + " ORDER BY created_utc ASC, id ASC;";
                    if (value != null) StoredValueBinder.Value(cmd, "@value", value);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) results.Add(JudgeFollowUpColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }
            return results;
        }

        private static void AddParameters(MySqlCommand cmd, JudgeFollowUp item)
        {
            JudgeFollowUpColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "judge_follow_ups"), item);
        }

    }
}
