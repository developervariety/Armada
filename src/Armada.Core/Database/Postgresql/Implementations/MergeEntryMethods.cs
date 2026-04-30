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
    /// PostgreSQL implementation of merge entry database operations.
    /// </summary>
    public class MergeEntryMethods : IMergeEntryMethods
    {
        #region Private-Members

        private NpgsqlDataSource _DataSource;
        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL merge entry methods.
        /// </summary>
        /// <param name="dataSource">NpgsqlDataSource instance.</param>
        public MergeEntryMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create a merge entry.
        /// </summary>
        /// <param name="entry">Merge entry to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Created merge entry.</returns>
        public async Task<MergeEntry> CreateAsync(MergeEntry entry, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            entry.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO merge_entries (id, tenant_id, user_id, mission_id, vessel_id, branch_name, target_branch, status, priority, batch_id, test_command, test_output, test_exit_code, created_utc, last_update_utc, test_started_utc, completed_utc, audit_lane, audit_convention_passed, audit_convention_notes, audit_critical_trigger, audit_deep_picked, audit_deep_completed_utc, audit_deep_verdict, audit_deep_notes, audit_deep_recommended_action, pr_url, pr_base_branch, merge_failure_class, conflicted_files, merge_failure_summary, diff_line_count)
                        VALUES (@id, @tenant_id, @user_id, @mission_id, @vessel_id, @branch_name, @target_branch, @status, @priority, @batch_id, @test_command, @test_output, @test_exit_code, @created_utc, @last_update_utc, @test_started_utc, @completed_utc, @audit_lane, @audit_convention_passed, @audit_convention_notes, @audit_critical_trigger, @audit_deep_picked, @audit_deep_completed_utc, @audit_deep_verdict, @audit_deep_notes, @audit_deep_recommended_action, @pr_url, @pr_base_branch, @merge_failure_class, @conflicted_files, @merge_failure_summary, @diff_line_count);";
                    cmd.Parameters.AddWithValue("@id", entry.Id);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)entry.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object?)entry.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mission_id", (object?)entry.MissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@vessel_id", (object?)entry.VesselId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@branch_name", entry.BranchName);
                    cmd.Parameters.AddWithValue("@target_branch", entry.TargetBranch);
                    cmd.Parameters.AddWithValue("@status", entry.Status.ToString());
                    cmd.Parameters.AddWithValue("@priority", entry.Priority);
                    cmd.Parameters.AddWithValue("@batch_id", (object?)entry.BatchId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@test_command", (object?)entry.TestCommand ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@test_output", (object?)entry.TestOutput ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@test_exit_code", entry.TestExitCode.HasValue ? (object)entry.TestExitCode.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_utc", ToIso8601(entry.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(entry.LastUpdateUtc));
                    cmd.Parameters.AddWithValue("@test_started_utc", entry.TestStartedUtc.HasValue ? (object)ToIso8601(entry.TestStartedUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@completed_utc", entry.CompletedUtc.HasValue ? (object)ToIso8601(entry.CompletedUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_lane", (object?)entry.AuditLane ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_convention_passed", entry.AuditConventionPassed.HasValue ? (object)entry.AuditConventionPassed.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_convention_notes", (object?)entry.AuditConventionNotes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_critical_trigger", (object?)entry.AuditCriticalTrigger ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_picked", entry.AuditDeepPicked.HasValue ? (object)entry.AuditDeepPicked.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_completed_utc", entry.AuditDeepCompletedUtc.HasValue ? (object)ToIso8601(entry.AuditDeepCompletedUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_verdict", (object?)entry.AuditDeepVerdict ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_notes", (object?)entry.AuditDeepNotes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_recommended_action", (object?)entry.AuditDeepRecommendedAction ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@pr_url", (object?)entry.PrUrl ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@pr_base_branch", (object?)entry.PrBaseBranch ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@merge_failure_class", entry.MergeFailureClass.HasValue ? (object)entry.MergeFailureClass.Value.ToString() : DBNull.Value);
                    cmd.Parameters.AddWithValue("@conflicted_files", (object?)entry.ConflictedFiles ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@merge_failure_summary", (object?)entry.MergeFailureSummary ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@diff_line_count", entry.DiffLineCount);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return entry;
        }

        /// <summary>
        /// Read a merge entry by identifier.
        /// </summary>
        /// <param name="id">Merge entry identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Merge entry if found, null otherwise.</returns>
        public async Task<MergeEntry?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MergeEntryFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Update a merge entry.
        /// </summary>
        /// <param name="entry">Merge entry with updated values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Updated merge entry.</returns>
        public async Task<MergeEntry> UpdateAsync(MergeEntry entry, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            entry.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"UPDATE merge_entries SET
                        tenant_id = @tenant_id,
                            user_id = @user_id,
                        mission_id = @mission_id,
                        vessel_id = @vessel_id,
                        branch_name = @branch_name,
                        target_branch = @target_branch,
                        status = @status,
                        priority = @priority,
                        batch_id = @batch_id,
                        test_command = @test_command,
                        test_output = @test_output,
                        test_exit_code = @test_exit_code,
                        last_update_utc = @last_update_utc,
                        test_started_utc = @test_started_utc,
                        completed_utc = @completed_utc,
                        audit_lane = @audit_lane,
                        audit_convention_passed = @audit_convention_passed,
                        audit_convention_notes = @audit_convention_notes,
                        audit_critical_trigger = @audit_critical_trigger,
                        audit_deep_picked = @audit_deep_picked,
                        audit_deep_completed_utc = @audit_deep_completed_utc,
                        audit_deep_verdict = @audit_deep_verdict,
                        audit_deep_notes = @audit_deep_notes,
                        audit_deep_recommended_action = @audit_deep_recommended_action,
                        pr_url = @pr_url,
                        pr_base_branch = @pr_base_branch,
                        merge_failure_class = @merge_failure_class,
                        conflicted_files = @conflicted_files,
                        merge_failure_summary = @merge_failure_summary,
                        diff_line_count = @diff_line_count
                        WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", entry.Id);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)entry.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object?)entry.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mission_id", (object?)entry.MissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@vessel_id", (object?)entry.VesselId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@branch_name", entry.BranchName);
                    cmd.Parameters.AddWithValue("@target_branch", entry.TargetBranch);
                    cmd.Parameters.AddWithValue("@status", entry.Status.ToString());
                    cmd.Parameters.AddWithValue("@priority", entry.Priority);
                    cmd.Parameters.AddWithValue("@batch_id", (object?)entry.BatchId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@test_command", (object?)entry.TestCommand ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@test_output", (object?)entry.TestOutput ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@test_exit_code", entry.TestExitCode.HasValue ? (object)entry.TestExitCode.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(entry.LastUpdateUtc));
                    cmd.Parameters.AddWithValue("@test_started_utc", entry.TestStartedUtc.HasValue ? (object)ToIso8601(entry.TestStartedUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@completed_utc", entry.CompletedUtc.HasValue ? (object)ToIso8601(entry.CompletedUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_lane", (object?)entry.AuditLane ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_convention_passed", entry.AuditConventionPassed.HasValue ? (object)entry.AuditConventionPassed.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_convention_notes", (object?)entry.AuditConventionNotes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_critical_trigger", (object?)entry.AuditCriticalTrigger ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_picked", entry.AuditDeepPicked.HasValue ? (object)entry.AuditDeepPicked.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_completed_utc", entry.AuditDeepCompletedUtc.HasValue ? (object)ToIso8601(entry.AuditDeepCompletedUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_verdict", (object?)entry.AuditDeepVerdict ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_notes", (object?)entry.AuditDeepNotes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@audit_deep_recommended_action", (object?)entry.AuditDeepRecommendedAction ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@pr_url", (object?)entry.PrUrl ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@pr_base_branch", (object?)entry.PrBaseBranch ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@merge_failure_class", entry.MergeFailureClass.HasValue ? (object)entry.MergeFailureClass.Value.ToString() : DBNull.Value);
                    cmd.Parameters.AddWithValue("@conflicted_files", (object?)entry.ConflictedFiles ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@merge_failure_summary", (object?)entry.MergeFailureSummary ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@diff_line_count", entry.DiffLineCount);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return entry;
        }

        /// <summary>
        /// Delete a merge entry by identifier.
        /// </summary>
        /// <param name="id">Merge entry identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM merge_entries WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all merge entries.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of all merge entries ordered by priority and creation date.</returns>
        public async Task<List<MergeEntry>> EnumerateAsync(CancellationToken token = default)
        {
            List<MergeEntry> results = new List<MergeEntry>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries ORDER BY priority ASC, created_utc ASC;";
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate merge entries with pagination and filtering.
        /// </summary>
        /// <param name="query">Enumeration query with pagination and filter parameters.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Paginated enumeration result of merge entries.</returns>
        public async Task<EnumerationResult<MergeEntry>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new NpgsqlParameter("@created_after", ToIso8601(query.CreatedAfter.Value)));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new NpgsqlParameter("@created_before", ToIso8601(query.CreatedBefore.Value)));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("status = @status");
                    parameters.Add(new NpgsqlParameter("@status", query.Status));
                }
                if (!string.IsNullOrEmpty(query.VesselId))
                {
                    conditions.Add("vessel_id = @vessel_id");
                    parameters.Add(new NpgsqlParameter("@vessel_id", query.VesselId));
                }
                if (!string.IsNullOrEmpty(query.MissionId))
                {
                    conditions.Add("mission_id = @mission_id");
                    parameters.Add(new NpgsqlParameter("@mission_id", query.MissionId));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM merge_entries" + whereClause + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value });
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                // Query
                List<MergeEntry> results = new List<MergeEntry>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value });
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }

                return EnumerationResult<MergeEntry>.Create(query, results, totalCount);
            }
        }

        /// <summary>
        /// Enumerate merge entries by status.
        /// </summary>
        /// <param name="status">Merge status to filter by.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of merge entries with the specified status.</returns>
        public async Task<List<MergeEntry>> EnumerateByStatusAsync(MergeStatusEnum status, CancellationToken token = default)
        {
            List<MergeEntry> results = new List<MergeEntry>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries WHERE status = @status ORDER BY priority ASC, created_utc ASC;";
                    cmd.Parameters.AddWithValue("@status", status.ToString());
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Check if a merge entry exists by identifier.
        /// </summary>
        /// <param name="id">Merge entry identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the merge entry exists.</returns>
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM merge_entries WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <summary>
        /// Read a merge entry by tenant and identifier (tenant-scoped).
        /// </summary>
        public async Task<MergeEntry?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries WHERE tenant_id = @tenant_id AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MergeEntryFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Delete a merge entry by tenant and identifier (tenant-scoped).
        /// </summary>
        public async Task DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM merge_entries WHERE tenant_id = @tenant_id AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all merge entries in a tenant (tenant-scoped).
        /// </summary>
        public async Task<List<MergeEntry>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<MergeEntry> results = new List<MergeEntry>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries WHERE tenant_id = @tenant_id ORDER BY priority ASC, created_utc ASC;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate merge entries with pagination and filtering (tenant-scoped).
        /// </summary>
        public async Task<EnumerationResult<MergeEntry>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (query == null) query = new EnumerationQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string> { "tenant_id = @tenant_id" };
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter> { new NpgsqlParameter("@tenant_id", tenantId) };

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new NpgsqlParameter("@created_after", ToIso8601(query.CreatedAfter.Value)));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new NpgsqlParameter("@created_before", ToIso8601(query.CreatedBefore.Value)));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("status = @status");
                    parameters.Add(new NpgsqlParameter("@status", query.Status));
                }
                if (!string.IsNullOrEmpty(query.VesselId))
                {
                    conditions.Add("vessel_id = @vessel_id");
                    parameters.Add(new NpgsqlParameter("@vessel_id", query.VesselId));
                }
                if (!string.IsNullOrEmpty(query.MissionId))
                {
                    conditions.Add("mission_id = @mission_id");
                    parameters.Add(new NpgsqlParameter("@mission_id", query.MissionId));
                }

                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM merge_entries" + whereClause + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value });
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                // Query
                List<MergeEntry> results = new List<MergeEntry>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value });
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }

                return EnumerationResult<MergeEntry>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<List<MergeEntry>> EnumerateByStatusAsync(string tenantId, MergeStatusEnum status, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<MergeEntry> results = new List<MergeEntry>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries WHERE tenant_id = @tenant_id AND status = @status ORDER BY priority ASC, created_utc ASC;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@status", status.ToString());
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM merge_entries WHERE tenant_id = @tenant_id AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<MergeEntry?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MergeEntryFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM merge_entries WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<MergeEntry>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            List<MergeEntry> results = new List<MergeEntry>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries WHERE tenant_id = @tenant_id AND user_id = @user_id ORDER BY priority ASC, created_utc ASC;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<MergeEntry>> EnumerateAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (query == null) query = new EnumerationQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string> { "tenant_id = @tenant_id", "user_id = @user_id" };
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter> { new NpgsqlParameter("@tenant_id", tenantId), new NpgsqlParameter("@user_id", userId) };

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new NpgsqlParameter("@created_after", ToIso8601(query.CreatedAfter.Value)));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new NpgsqlParameter("@created_before", ToIso8601(query.CreatedBefore.Value)));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("status = @status");
                    parameters.Add(new NpgsqlParameter("@status", query.Status));
                }
                if (!string.IsNullOrEmpty(query.VesselId))
                {
                    conditions.Add("vessel_id = @vessel_id");
                    parameters.Add(new NpgsqlParameter("@vessel_id", query.VesselId));
                }
                if (!string.IsNullOrEmpty(query.MissionId))
                {
                    conditions.Add("mission_id = @mission_id");
                    parameters.Add(new NpgsqlParameter("@mission_id", query.MissionId));
                }

                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM merge_entries" + whereClause + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value });
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                // Query
                List<MergeEntry> results = new List<MergeEntry>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM merge_entries" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value });
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryFromReader(reader));
                    }
                }

                return EnumerationResult<MergeEntry>.Create(query, results, totalCount);
            }
        }

        #endregion

        #region Private-Methods

        private static string ToIso8601(DateTime dt)
        {
            return dt.ToUniversalTime().ToString(_Iso8601Format, CultureInfo.InvariantCulture);
        }

        private static DateTime FromIso8601(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        private static DateTime? FromIso8601Nullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            if (string.IsNullOrEmpty(str)) return null;
            return FromIso8601(str);
        }

        private static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            return string.IsNullOrEmpty(str) ? null : str;
        }

        private static int? NullableInt(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            return Convert.ToInt32(value);
        }

        private static MergeEntry MergeEntryFromReader(NpgsqlDataReader reader)
        {
            MergeEntry entry = new MergeEntry();
            entry.Id = reader["id"].ToString()!;
            entry.TenantId = NullableString(reader["tenant_id"]);
            entry.MissionId = NullableString(reader["mission_id"]);
            entry.VesselId = NullableString(reader["vessel_id"]);
            entry.BranchName = reader["branch_name"].ToString()!;
            entry.TargetBranch = reader["target_branch"].ToString()!;
            entry.Status = Enum.Parse<MergeStatusEnum>(reader["status"].ToString()!);
            entry.Priority = Convert.ToInt32(reader["priority"]);
            entry.BatchId = NullableString(reader["batch_id"]);
            entry.TestCommand = NullableString(reader["test_command"]);
            entry.TestOutput = NullableString(reader["test_output"]);
            entry.TestExitCode = NullableInt(reader["test_exit_code"]);
            entry.CreatedUtc = FromIso8601(reader["created_utc"].ToString()!);
            entry.LastUpdateUtc = FromIso8601(reader["last_update_utc"].ToString()!);
            entry.TestStartedUtc = FromIso8601Nullable(reader["test_started_utc"]);
            entry.CompletedUtc = FromIso8601Nullable(reader["completed_utc"]);
            try { entry.AuditLane = reader["audit_lane"] as string; } catch { }
            try { object av = reader["audit_convention_passed"]; entry.AuditConventionPassed = (av == null || av == DBNull.Value) ? (bool?)null : (bool)av; } catch { }
            try { entry.AuditConventionNotes = reader["audit_convention_notes"] as string; } catch { }
            try { entry.AuditCriticalTrigger = reader["audit_critical_trigger"] as string; } catch { }
            try { object dv = reader["audit_deep_picked"]; entry.AuditDeepPicked = (dv == null || dv == DBNull.Value) ? (bool?)null : (bool)dv; } catch { }
            try { entry.AuditDeepCompletedUtc = FromIso8601Nullable(reader["audit_deep_completed_utc"]); } catch { }
            try { entry.AuditDeepVerdict = reader["audit_deep_verdict"] as string; } catch { }
            try { entry.AuditDeepNotes = reader["audit_deep_notes"] as string; } catch { }
            try { entry.AuditDeepRecommendedAction = reader["audit_deep_recommended_action"] as string; } catch { }
            try { entry.PrUrl = reader["pr_url"] as string; } catch { }
            try { entry.PrBaseBranch = reader["pr_base_branch"] as string; } catch { }
            try
            {
                string? mfc = reader["merge_failure_class"] as string;
                if (!string.IsNullOrEmpty(mfc) && Enum.TryParse<MergeFailureClassEnum>(mfc, out MergeFailureClassEnum parsed))
                    entry.MergeFailureClass = parsed;
            }
            catch { }
            try { entry.ConflictedFiles = reader["conflicted_files"] as string; } catch { }
            try { entry.MergeFailureSummary = reader["merge_failure_summary"] as string; } catch { }
            try { object dlc = reader["diff_line_count"]; entry.DiffLineCount = (dlc == null || dlc == DBNull.Value) ? 0 : Convert.ToInt32(dlc); } catch { }
            return entry;
        }

        #endregion
    }
}

