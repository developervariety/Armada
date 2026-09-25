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
                    MergeEntryColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "merge_entries"), entry);
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
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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
                    MergeEntryColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "merge_entries"), entry);
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
                    StoredValueBinder.Value(cmd, "@id", id);
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
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_after", "merge_entries", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_before", "merge_entries", "created_utc", query.CreatedBefore.Value));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("status = @status");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@status", query.Status));
                }
                if (!string.IsNullOrEmpty(query.VesselId))
                {
                    conditions.Add("vessel_id = @vessel_id");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@vessel_id", query.VesselId));
                }
                if (!string.IsNullOrEmpty(query.MissionId))
                {
                    conditions.Add("mission_id = @mission_id");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@mission_id", query.MissionId));
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
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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
                    StoredValueBinder.Value(cmd, "@status", status.ToString());
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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
                    StoredValueBinder.Value(cmd, "@id", id);
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_after", "merge_entries", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_before", "merge_entries", "created_utc", query.CreatedBefore.Value));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("status = @status");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@status", query.Status));
                }
                if (!string.IsNullOrEmpty(query.VesselId))
                {
                    conditions.Add("vessel_id = @vessel_id");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@vessel_id", query.VesselId));
                }
                if (!string.IsNullOrEmpty(query.MissionId))
                {
                    conditions.Add("mission_id = @mission_id");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@mission_id", query.MissionId));
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
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@status", status.ToString());
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@user_id", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@user_id", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
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
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@user_id", userId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_after", "merge_entries", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_before", "merge_entries", "created_utc", query.CreatedBefore.Value));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("status = @status");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@status", query.Status));
                }
                if (!string.IsNullOrEmpty(query.VesselId))
                {
                    conditions.Add("vessel_id = @vessel_id");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@vessel_id", query.VesselId));
                }
                if (!string.IsNullOrEmpty(query.MissionId))
                {
                    conditions.Add("mission_id = @mission_id");
                    parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@mission_id", query.MissionId));
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
                            results.Add(MergeEntryColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<MergeEntry>.Create(query, results, totalCount);
            }
        }

        #endregion

    }
}
