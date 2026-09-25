namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of workflow-profile persistence.
    /// </summary>
    public class WorkflowProfileMethods : IWorkflowProfileMethods
    {
        private readonly string _ConnectionString;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public WorkflowProfileMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<WorkflowProfile> CreateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO workflow_profiles
                        (id, tenant_id, user_id, name, description, scope, fleet_id, vessel_id, is_default, active,
                         language_hints_json, lint_command, build_command, unit_test_command, containerless_unit_test_command, integration_test_command,
                         e2e_test_command, package_command, publish_artifact_command, release_versioning_command,
                         changelog_generation_command, migration_command, security_scan_command, performance_command,
                         deployment_verification_command, rollback_verification_command, required_secrets_json,
                         expected_artifacts_json, environments_json, environment_variables_json, created_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @name, @description, @scope, @fleet_id, @vessel_id, @is_default, @active,
                         @language_hints_json, @lint_command, @build_command, @unit_test_command, @containerless_unit_test_command, @integration_test_command,
                         @e2e_test_command, @package_command, @publish_artifact_command, @release_versioning_command,
                         @changelog_generation_command, @migration_command, @security_scan_command, @performance_command,
                         @deployment_verification_command, @rollback_verification_command, @required_secrets_json,
                         @expected_artifacts_json, @environments_json, @environment_variables_json, @created_utc, @last_update_utc);";
                    AddParameters(cmd, profile);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return profile;
        }

        /// <inheritdoc />
        public async Task<WorkflowProfile?> ReadAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<MySqlParameter> parameters = new List<MySqlParameter> { StoredValueBinder.Parameter(new MySqlParameter(), "@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "SELECT * FROM workflow_profiles WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return WorkflowProfileColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE workflow_profiles SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        name = @name,
                        description = @description,
                        scope = @scope,
                        fleet_id = @fleet_id,
                        vessel_id = @vessel_id,
                        is_default = @is_default,
                        active = @active,
                        language_hints_json = @language_hints_json,
                        lint_command = @lint_command,
                        build_command = @build_command,
                        unit_test_command = @unit_test_command,
                        containerless_unit_test_command = @containerless_unit_test_command,
                        integration_test_command = @integration_test_command,
                        e2e_test_command = @e2e_test_command,
                        package_command = @package_command,
                        publish_artifact_command = @publish_artifact_command,
                        release_versioning_command = @release_versioning_command,
                        changelog_generation_command = @changelog_generation_command,
                        migration_command = @migration_command,
                        security_scan_command = @security_scan_command,
                        performance_command = @performance_command,
                        deployment_verification_command = @deployment_verification_command,
                        rollback_verification_command = @rollback_verification_command,
                        required_secrets_json = @required_secrets_json,
                        expected_artifacts_json = @expected_artifacts_json,
                        environments_json = @environments_json,
                environment_variables_json = @environment_variables_json,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    AddParameters(cmd, profile);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return profile;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<MySqlParameter> parameters = new List<MySqlParameter> { StoredValueBinder.Parameter(new MySqlParameter(), "@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "DELETE FROM workflow_profiles WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<WorkflowProfile>> EnumerateAsync(WorkflowProfileQuery query, CancellationToken token = default)
        {
            query ??= new WorkflowProfileQuery();
            int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
            int offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * pageSize;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

                long totalCount;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM workflow_profiles" + whereClause + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<WorkflowProfile> results = new List<WorkflowProfile>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM workflow_profiles" + whereClause
                        + " ORDER BY is_default DESC, last_update_utc DESC, name ASC LIMIT " + pageSize + " OFFSET " + offset + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(WorkflowProfileColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return new EnumerationResult<WorkflowProfile>
                {
                    PageNumber = query.PageNumber,
                    PageSize = pageSize,
                    TotalRecords = totalCount,
                    TotalPages = pageSize > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0,
                    Objects = results
                };
            }
        }

        /// <inheritdoc />
        public async Task<List<WorkflowProfile>> EnumerateAllAsync(WorkflowProfileQuery query, CancellationToken token = default)
        {
            query ??= new WorkflowProfileQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string>();
                    List<MySqlParameter> parameters = new List<MySqlParameter>();
                    ApplyQueryFilters(query, conditions, parameters);
                    string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;
                    cmd.CommandText = "SELECT * FROM workflow_profiles" + whereClause + " ORDER BY is_default DESC, last_update_utc DESC, name ASC;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);

                    List<WorkflowProfile> results = new List<WorkflowProfile>();
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(WorkflowProfileColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }

                    return results;
                }
            }
        }

        private static void ApplyQueryFilters(WorkflowProfileQuery? query, List<string> conditions, List<MySqlParameter> parameters)
        {
            if (query == null) return;

            if (!String.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@tenant_id", query.TenantId));
            }
            if (!String.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@user_id", query.UserId));
            }
            if (query.Scope.HasValue)
            {
                conditions.Add("scope = @scope");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@scope", query.Scope.Value.ToString()));
            }
            if (!String.IsNullOrWhiteSpace(query.FleetId))
            {
                conditions.Add("fleet_id = @fleet_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@fleet_id", query.FleetId));
            }
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@vessel_id", query.VesselId));
            }
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search)");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@search", "%" + query.Search.ToLowerInvariant() + "%"));
            }
            if (query.Active.HasValue)
            {
                conditions.Add("active = @active");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Boolean(new MySqlParameter(), "@active", "workflow_profiles", "active", query.Active.Value));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@from_utc", "workflow_profiles", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@to_utc", "workflow_profiles", "created_utc", query.ToUtc.Value));
            }
        }

        private static void AddParameters(MySqlCommand cmd, WorkflowProfile profile)
        {
            WorkflowProfileColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "workflow_profiles"), profile);
            StoredValueBinder.Value(cmd, "@e2e_test_command", profile.E2ETestCommand);
        }

        private static MySqlParameter CloneParameter(MySqlParameter parameter)
        {
            return new MySqlParameter(parameter.ParameterName, parameter.Value);
        }
    }
}
