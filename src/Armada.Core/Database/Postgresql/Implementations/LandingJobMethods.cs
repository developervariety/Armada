namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Globalization;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// PostgreSQL implementation of landing job database operations.
    /// </summary>
    public class LandingJobMethods : ILandingJobMethods
    {
        #region Private-Members

        private NpgsqlDataSource _DataSource;
        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL landing job methods.
        /// </summary>
        /// <param name="dataSource">NpgsqlDataSource instance.</param>
        public LandingJobMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<LandingJob> CreateAsync(LandingJob job, CancellationToken token = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            job.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
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

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM landing_jobs WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return LandingJobColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<LandingJob?> ReadByMergeEntryAsync(string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(mergeEntryId)) throw new ArgumentNullException(nameof(mergeEntryId));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM landing_jobs WHERE merge_entry_id = @merge_entry_id;";
                    cmd.Parameters.AddWithValue("@merge_entry_id", mergeEntryId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return LandingJobColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
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

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
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

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM landing_jobs WHERE state = @state ORDER BY created_utc ASC;";
                    cmd.Parameters.AddWithValue("@state", state.ToString());
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) results.Add(LandingJobColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        #endregion

        #region Private-Methods

        private static void AddParameters(NpgsqlCommand cmd, LandingJob job)
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

        private static string ToIso8601(DateTime dt)
        {
            return dt.ToUniversalTime().ToString(_Iso8601Format, CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
