namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Globalization;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of landing job database operations.
    /// </summary>
    public class LandingJobMethods : ILandingJobMethods
    {
        #region Private-Members

        private string _ConnectionString;
        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the MySQL landing job methods.
        /// </summary>
        /// <param name="connectionString">MySQL connection string.</param>
        public LandingJobMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<LandingJob> CreateAsync(LandingJob job, CancellationToken token = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            job.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO landing_jobs (id, tenant_id, user_id, merge_entry_id, mission_id, vessel_id, branch_name, target_branch, state, retry_count, created_utc, last_update_utc, started_utc, completed_utc, last_error)
                        VALUES (@id, @tenant_id, @user_id, @merge_entry_id, @mission_id, @vessel_id, @branch_name, @target_branch, @state, @retry_count, @created_utc, @last_update_utc, @started_utc, @completed_utc, @last_error);";
                    AddParameters(cmd, job);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return job;
        }

        /// <inheritdoc />
        public async Task<LandingJob?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM landing_jobs WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return FromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<LandingJob?> ReadByMergeEntryAsync(string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(mergeEntryId)) throw new ArgumentNullException(nameof(mergeEntryId));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM landing_jobs WHERE merge_entry_id = @merge_entry_id;";
                    cmd.Parameters.AddWithValue("@merge_entry_id", mergeEntryId);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return FromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<LandingJob> UpdateAsync(LandingJob job, CancellationToken token = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            job.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE landing_jobs SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        merge_entry_id = @merge_entry_id,
                        mission_id = @mission_id,
                        vessel_id = @vessel_id,
                        branch_name = @branch_name,
                        target_branch = @target_branch,
                        state = @state,
                        retry_count = @retry_count,
                        last_update_utc = @last_update_utc,
                        started_utc = @started_utc,
                        completed_utc = @completed_utc,
                        last_error = @last_error
                        WHERE id = @id;";
                    AddParameters(cmd, job);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return job;
        }

        /// <inheritdoc />
        public async Task DeleteByMergeEntryAsync(string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(mergeEntryId)) throw new ArgumentNullException(nameof(mergeEntryId));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM landing_jobs WHERE merge_entry_id = @merge_entry_id;";
                    cmd.Parameters.AddWithValue("@merge_entry_id", mergeEntryId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<LandingJob>> EnumerateByStateAsync(LandingJobStateEnum state, CancellationToken token = default)
        {
            List<LandingJob> results = new List<LandingJob>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM landing_jobs WHERE state = @state ORDER BY created_utc ASC;";
                    cmd.Parameters.AddWithValue("@state", state.ToString());
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) results.Add(FromReader(reader));
                    }
                }
            }

            return results;
        }

        #endregion

        #region Private-Methods

        private static void AddParameters(MySqlCommand cmd, LandingJob job)
        {
            cmd.Parameters.AddWithValue("@id", job.Id);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)job.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)job.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@merge_entry_id", job.MergeEntryId);
            cmd.Parameters.AddWithValue("@mission_id", (object?)job.MissionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vessel_id", (object?)job.VesselId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@branch_name", job.BranchName);
            cmd.Parameters.AddWithValue("@target_branch", job.TargetBranch);
            cmd.Parameters.AddWithValue("@state", job.State.ToString());
            cmd.Parameters.AddWithValue("@retry_count", job.RetryCount);
            cmd.Parameters.AddWithValue("@created_utc", ToIso8601(job.CreatedUtc));
            cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(job.LastUpdateUtc));
            cmd.Parameters.AddWithValue("@started_utc", job.StartedUtc.HasValue ? (object)ToIso8601(job.StartedUtc.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("@completed_utc", job.CompletedUtc.HasValue ? (object)ToIso8601(job.CompletedUtc.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("@last_error", (object?)job.LastError ?? DBNull.Value);
        }

        private static LandingJob FromReader(MySqlDataReader reader)
        {
            LandingJob job = new LandingJob();
            job.Id = reader["id"].ToString()!;
            job.TenantId = reader["tenant_id"] as string;
            job.UserId = reader["user_id"] as string;
            job.MergeEntryId = reader["merge_entry_id"].ToString()!;
            job.MissionId = reader["mission_id"] as string;
            job.VesselId = reader["vessel_id"] as string;
            job.BranchName = reader["branch_name"].ToString()!;
            job.TargetBranch = reader["target_branch"].ToString()!;
            job.State = Enum.Parse<LandingJobStateEnum>(reader["state"].ToString()!);
            job.RetryCount = Convert.ToInt32(reader["retry_count"]);
            job.CreatedUtc = FromIso8601(reader["created_utc"].ToString()!);
            job.LastUpdateUtc = FromIso8601(reader["last_update_utc"].ToString()!);
            job.StartedUtc = FromIso8601Nullable(reader["started_utc"]);
            job.CompletedUtc = FromIso8601Nullable(reader["completed_utc"]);
            job.LastError = reader["last_error"] as string;
            return job;
        }

        private static string ToIso8601(DateTime dt)
        {
            return dt.ToUniversalTime().ToString(_Iso8601Format, CultureInfo.InvariantCulture);
        }

        private static DateTime FromIso8601(string value)
        {
            return DateTime.ParseExact(value, _Iso8601Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }

        private static DateTime? FromIso8601Nullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string? str = value.ToString();
            if (String.IsNullOrEmpty(str)) return null;
            return FromIso8601(str);
        }

        #endregion
    }
}
