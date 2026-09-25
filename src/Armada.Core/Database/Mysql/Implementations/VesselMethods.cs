namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of vessel database operations.
    /// </summary>
    public class VesselMethods : IVesselMethods
    {
        #region Private-Members

        private string _ConnectionString;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with a MySQL connection string.
        /// </summary>
        /// <param name="connectionString">MySQL connection string.</param>
        public VesselMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create a vessel.
        /// </summary>
        /// <param name="vessel">Vessel to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Created vessel.</returns>
        public async Task<Vessel> CreateAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            vessel.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO vessels (id, tenant_id, user_id, fleet_id, name, repo_url, local_path, working_directory, github_token_override, project_context, style_guide, enable_model_context, model_context, landing_mode, branch_cleanup_policy, allow_concurrent_missions, require_passing_checks_to_land, protected_branch_patterns, release_branch_prefix, hotfix_branch_prefix, require_pull_request_for_protected_branches, require_merge_queue_for_release_branches, default_pipeline_id, protected_paths, auto_land_predicate, auto_land_calibration_landed_count, default_playbooks, sibling_repos, architect_max_missions_per_voyage, default_branch, active, created_utc, last_update_utc, secret_scan_enabled, protected_path_patterns_json, private_identifier_denylist_json)
                        VALUES (@id, @tenant_id, @user_id, @fleet_id, @name, @repo_url, @local_path, @working_directory, @github_token_override, @project_context, @style_guide, @enable_model_context, @model_context, @landing_mode, @branch_cleanup_policy, @allow_concurrent_missions, @require_passing_checks_to_land, @protected_branch_patterns, @release_branch_prefix, @hotfix_branch_prefix, @require_pull_request_for_protected_branches, @require_merge_queue_for_release_branches, @default_pipeline_id, @protected_paths, @auto_land_predicate, @auto_land_calibration_landed_count, @default_playbooks, @sibling_repos, @architect_max_missions_per_voyage, @default_branch, @active, @created_utc, @last_update_utc, @secret_scan_enabled, @protected_path_patterns_json, @private_identifier_denylist_json);";
                    VesselColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "vessels"), vessel);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return vessel;
        }

        /// <summary>
        /// Read a vessel by identifier.
        /// </summary>
        /// <param name="id">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Vessel or null if not found.</returns>
        public async Task<Vessel?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Read a vessel by name.
        /// </summary>
        /// <param name="name">Vessel name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Vessel or null if not found.</returns>
        public async Task<Vessel?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Update a vessel.
        /// </summary>
        /// <param name="vessel">Vessel with updated values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Updated vessel.</returns>
        public async Task<Vessel> UpdateAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            vessel.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE vessels SET secret_scan_enabled = @secret_scan_enabled, protected_path_patterns_json = @protected_path_patterns_json, private_identifier_denylist_json = @private_identifier_denylist_json,
                        tenant_id = @tenant_id,
                            user_id = @user_id,
                        fleet_id = @fleet_id,
                        name = @name,
                        repo_url = @repo_url,
                        local_path = @local_path,
                        working_directory = @working_directory,
                        github_token_override = @github_token_override,
                        project_context = @project_context,
                        style_guide = @style_guide,
                        enable_model_context = @enable_model_context,
                        model_context = @model_context,
                        landing_mode = @landing_mode,
                        branch_cleanup_policy = @branch_cleanup_policy,
                        allow_concurrent_missions = @allow_concurrent_missions,
                            require_passing_checks_to_land = @require_passing_checks_to_land,
                            protected_branch_patterns = @protected_branch_patterns,
                            release_branch_prefix = @release_branch_prefix,
                            hotfix_branch_prefix = @hotfix_branch_prefix,
                            require_pull_request_for_protected_branches = @require_pull_request_for_protected_branches,
                            require_merge_queue_for_release_branches = @require_merge_queue_for_release_branches,
                        default_pipeline_id = @default_pipeline_id,
                        protected_paths = @protected_paths,
                        auto_land_predicate = @auto_land_predicate,
                        auto_land_calibration_landed_count = @auto_land_calibration_landed_count,
                        default_playbooks = @default_playbooks,
                        sibling_repos = @sibling_repos,
                        architect_max_missions_per_voyage = @architect_max_missions_per_voyage,
                        default_branch = @default_branch,
                        active = @active,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    VesselColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "vessels"), vessel);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return vessel;
        }

        /// <summary>
        /// Delete a vessel by identifier.
        /// </summary>
        /// <param name="id">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM vessels WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all vessels.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of all vessels.</returns>
        public async Task<List<Vessel>> EnumerateAsync(CancellationToken token = default)
        {
            List<Vessel> results = new List<Vessel>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels ORDER BY name;";
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate vessels with pagination and filtering.
        /// </summary>
        /// <param name="query">Enumeration query parameters.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Paginated enumeration result.</returns>
        public async Task<EnumerationResult<Vessel>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_after", "vessels", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_before", "vessels", "created_utc", query.CreatedBefore.Value));
                }
                if (!string.IsNullOrEmpty(query.FleetId))
                {
                    conditions.Add("fleet_id = @fleet_id");
                    parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@fleet_id", query.FleetId));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM vessels" + whereClause + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                // Query
                List<Vessel> results = new List<Vessel>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Vessel>.Create(query, results, totalCount);
            }
        }

        /// <summary>
        /// Enumerate vessels by fleet identifier.
        /// </summary>
        /// <param name="fleetId">Fleet identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of vessels in the fleet.</returns>
        public async Task<List<Vessel>> EnumerateByFleetAsync(string fleetId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(fleetId)) throw new ArgumentNullException(nameof(fleetId));
            List<Vessel> results = new List<Vessel>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE fleet_id = @fleet_id ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@fleet_id", fleetId);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Check if a vessel exists by identifier.
        /// </summary>
        /// <param name="id">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the vessel exists.</returns>
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM vessels WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        /// <summary>
        /// Read a vessel by tenant and identifier (tenant-scoped).
        /// </summary>
        public async Task<Vessel?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Delete a vessel by tenant and identifier (tenant-scoped).
        /// </summary>
        public async Task DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM vessels WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all vessels in a tenant (tenant-scoped).
        /// </summary>
        public async Task<List<Vessel>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<Vessel> results = new List<Vessel>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE tenant_id = @tenantId ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate vessels with pagination and filtering (tenant-scoped).
        /// </summary>
        public async Task<EnumerationResult<Vessel>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (query == null) query = new EnumerationQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "tenant_id = @tenantId" };
                List<MySqlParameter> parameters = new List<MySqlParameter> { new MySqlParameter("@tenantId", tenantId) };

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_after", "vessels", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_before", "vessels", "created_utc", query.CreatedBefore.Value));
                }
                if (!string.IsNullOrEmpty(query.FleetId))
                {
                    conditions.Add("fleet_id = @fleet_id");
                    parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@fleet_id", query.FleetId));
                }

                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                long totalCount = 0;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM vessels" + whereClause + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Vessel> results = new List<Vessel>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Vessel>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<Vessel?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE tenant_id = @tenantId AND name = @name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<List<Vessel>> EnumerateByFleetAsync(string tenantId, string fleetId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(fleetId)) throw new ArgumentNullException(nameof(fleetId));
            List<Vessel> results = new List<Vessel>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE tenant_id = @tenantId AND fleet_id = @fleet_id ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@fleet_id", fleetId);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM vessels WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<Vessel?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE tenant_id = @tenantId AND user_id = @userId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM vessels WHERE tenant_id = @tenantId AND user_id = @userId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Vessel>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            List<Vessel> results = new List<Vessel>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels WHERE tenant_id = @tenantId AND user_id = @userId ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Vessel>> EnumerateAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (query == null) query = new EnumerationQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "tenant_id = @tenantId", "user_id = @userId" };
                List<MySqlParameter> parameters = new List<MySqlParameter>
                {
                    new MySqlParameter("@tenantId", tenantId),
                    new MySqlParameter("@userId", userId)
                };

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_after", "vessels", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_before", "vessels", "created_utc", query.CreatedBefore.Value));
                }
                if (!string.IsNullOrEmpty(query.FleetId))
                {
                    conditions.Add("fleet_id = @fleet_id");
                    parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@fleet_id", query.FleetId));
                }

                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                long totalCount = 0;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM vessels" + whereClause + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Vessel> results = new List<Vessel>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessels" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(VesselColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Vessel>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task IncrementCalibrationCounterAsync(string vesselId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE vessels SET auto_land_calibration_landed_count = auto_land_calibration_landed_count + 1 WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", vesselId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static DateTime FromIso8601(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        #endregion
    }
}
