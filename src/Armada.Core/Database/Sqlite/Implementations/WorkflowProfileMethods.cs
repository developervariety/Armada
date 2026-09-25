namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQLite implementation of workflow-profile persistence.
    /// </summary>
    public class WorkflowProfileMethods : IWorkflowProfileMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public WorkflowProfileMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<WorkflowProfile> CreateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
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
            return profile;
        }

        /// <inheritdoc />
        public async Task<WorkflowProfile?> ReadAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();

            List<string> conditions = new List<string> { "id = @id" };
            List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
            ApplyQueryFilters(query, conditions, parameters, includePaging: false);
            cmd.CommandText = "SELECT * FROM workflow_profiles WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);

            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                return WorkflowProfileColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
            return null;
        }

        /// <inheritdoc />
        public async Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
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
            return profile;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
            List<string> conditions = new List<string> { "id = @id" };
            List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
            ApplyQueryFilters(query, conditions, parameters, includePaging: false);
            cmd.CommandText = "DELETE FROM workflow_profiles WHERE " + String.Join(" AND ", conditions) + ";";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<WorkflowProfile>> EnumerateAsync(WorkflowProfileQuery query, CancellationToken token = default)
        {
            query ??= new WorkflowProfileQuery();

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);

            List<string> conditions = new List<string>();
            List<SqliteParameter> parameters = new List<SqliteParameter>();
            ApplyQueryFilters(query, conditions, parameters, includePaging: true);
            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            long totalCount;
            using (SqliteCommand countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM workflow_profiles" + whereClause + ";";
                foreach (SqliteParameter parameter in parameters) countCmd.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
                totalCount = (long)(await countCmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }

            List<WorkflowProfile> results = new List<WorkflowProfile>();
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM workflow_profiles" + whereClause +
                    " ORDER BY is_default DESC, last_update_utc DESC, name ASC" +
                    " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
                using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    results.Add(WorkflowProfileColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
            }

            return new EnumerationResult<WorkflowProfile>
            {
                PageNumber = query.PageNumber,
                PageSize = query.PageSize,
                TotalRecords = totalCount,
                TotalPages = query.PageSize > 0 ? (int)Math.Ceiling((double)totalCount / query.PageSize) : 0,
                Objects = results
            };
        }

        /// <inheritdoc />
        public async Task<List<WorkflowProfile>> EnumerateAllAsync(WorkflowProfileQuery query, CancellationToken token = default)
        {
            query ??= new WorkflowProfileQuery();

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();

            List<string> conditions = new List<string>();
            List<SqliteParameter> parameters = new List<SqliteParameter>();
            ApplyQueryFilters(query, conditions, parameters, includePaging: false);
            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;
            cmd.CommandText = "SELECT * FROM workflow_profiles" + whereClause + " ORDER BY is_default DESC, last_update_utc DESC, name ASC;";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);

            List<WorkflowProfile> results = new List<WorkflowProfile>();
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                results.Add(WorkflowProfileColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
            return results;
        }

        private static void ApplyQueryFilters(
            WorkflowProfileQuery? query,
            List<string> conditions,
            List<SqliteParameter> parameters,
            bool includePaging)
        {
            if (query == null) return;

            if (!String.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@tenant_id", query.TenantId));
            }
            if (!String.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@user_id", query.UserId));
            }
            if (query.Scope.HasValue)
            {
                conditions.Add("scope = @scope");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@scope", query.Scope.Value.ToString()));
            }
            if (!String.IsNullOrWhiteSpace(query.FleetId))
            {
                conditions.Add("fleet_id = @fleet_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@fleet_id", query.FleetId));
            }
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@vessel_id", query.VesselId));
            }
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search)");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@search", "%" + query.Search.ToLowerInvariant() + "%"));
            }
            if (query.Active.HasValue)
            {
                conditions.Add("active = @active");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Boolean(new SqliteParameter(), "@active", "workflow_profiles", "active", query.Active.Value));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@from_utc", "workflow_profiles", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@to_utc", "workflow_profiles", "created_utc", query.ToUtc.Value));
            }
        }

        private static void AddParameters(SqliteCommand cmd, WorkflowProfile profile)
        {
            WorkflowProfileColumns.Write(SqliteDatabaseDriver.StoredBinder.For(cmd, "workflow_profiles"), profile);
            StoredValueBinder.Value(cmd, "@e2e_test_command", profile.E2ETestCommand);
        }
    }
}
