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
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return LandingJobColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
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
                    StoredValueBinder.Value(cmd, "@merge_entry_id", mergeEntryId);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return LandingJobColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
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
                    StoredValueBinder.Value(cmd, "@merge_entry_id", mergeEntryId);
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
                    StoredValueBinder.Value(cmd, "@state", state.ToString());
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) results.Add(LandingJobColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        #endregion

        #region Private-Methods

        private static void AddParameters(MySqlCommand cmd, LandingJob job)
        {
            LandingJobColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "landing_jobs"), job);
        }

        #endregion
    }
}
