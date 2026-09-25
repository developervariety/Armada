namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQLite implementation of deployment-environment persistence.
    /// </summary>
    public class DeploymentEnvironmentMethods : IDeploymentEnvironmentMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public DeploymentEnvironmentMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _ = settings ?? throw new ArgumentNullException(nameof(settings));
            _ = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<DeploymentEnvironment> CreateAsync(DeploymentEnvironment environment, CancellationToken token = default)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            environment.LastUpdateUtc = DateTime.UtcNow;

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO environments
                (id, tenant_id, user_id, vessel_id, name, description, kind, configuration_source, base_url, health_endpoint,
                 access_notes, deployment_rules, verification_definitions_json, rollout_monitoring_window_minutes, rollout_monitoring_interval_seconds,
                 alert_on_regression, requires_approval, is_default, active, created_utc, last_update_utc)
                VALUES
                (@id, @tenant_id, @user_id, @vessel_id, @name, @description, @kind, @configuration_source, @base_url, @health_endpoint,
                 @access_notes, @deployment_rules, @verification_definitions_json, @rollout_monitoring_window_minutes, @rollout_monitoring_interval_seconds,
                 @alert_on_regression, @requires_approval, @is_default, @active, @created_utc, @last_update_utc);";
            AddParameters(cmd, environment);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return environment;
        }

        /// <inheritdoc />
        public async Task<DeploymentEnvironment?> ReadAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();

            List<string> conditions = new List<string> { "id = @id" };
            List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
            ApplyQueryFilters(query, conditions, parameters);
            cmd.CommandText = "SELECT * FROM environments WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);

            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                return DeploymentEnvironmentColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
            return null;
        }

        /// <inheritdoc />
        public async Task<DeploymentEnvironment> UpdateAsync(DeploymentEnvironment environment, CancellationToken token = default)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            environment.LastUpdateUtc = DateTime.UtcNow;

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE environments SET
                tenant_id = @tenant_id,
                user_id = @user_id,
                vessel_id = @vessel_id,
                name = @name,
                description = @description,
                kind = @kind,
                configuration_source = @configuration_source,
                base_url = @base_url,
                health_endpoint = @health_endpoint,
                access_notes = @access_notes,
                deployment_rules = @deployment_rules,
                verification_definitions_json = @verification_definitions_json,
                rollout_monitoring_window_minutes = @rollout_monitoring_window_minutes,
                rollout_monitoring_interval_seconds = @rollout_monitoring_interval_seconds,
                alert_on_regression = @alert_on_regression,
                requires_approval = @requires_approval,
                is_default = @is_default,
                active = @active,
                last_update_utc = @last_update_utc
                WHERE id = @id;";
            AddParameters(cmd, environment);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return environment;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
            List<string> conditions = new List<string> { "id = @id" };
            List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
            ApplyQueryFilters(query, conditions, parameters);
            cmd.CommandText = "DELETE FROM environments WHERE " + String.Join(" AND ", conditions) + ";";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<DeploymentEnvironment>> EnumerateAsync(DeploymentEnvironmentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentEnvironmentQuery();

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);

            List<string> conditions = new List<string>();
            List<SqliteParameter> parameters = new List<SqliteParameter>();
            ApplyQueryFilters(query, conditions, parameters);
            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            long totalCount;
            using (SqliteCommand countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM environments" + whereClause + ";";
                foreach (SqliteParameter parameter in parameters) countCmd.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
                totalCount = (long)(await countCmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }

            List<DeploymentEnvironment> results = new List<DeploymentEnvironment>();
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM environments" + whereClause
                    + " ORDER BY is_default DESC, active DESC, name ASC, created_utc DESC"
                    + " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
                using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    results.Add(DeploymentEnvironmentColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
            }

            return new EnumerationResult<DeploymentEnvironment>
            {
                PageNumber = query.PageNumber,
                PageSize = query.PageSize,
                TotalRecords = totalCount,
                TotalPages = query.PageSize > 0 ? (int)Math.Ceiling((double)totalCount / query.PageSize) : 0,
                Objects = results
            };
        }

        /// <inheritdoc />
        public async Task<List<DeploymentEnvironment>> EnumerateAllAsync(DeploymentEnvironmentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentEnvironmentQuery();

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();

            List<string> conditions = new List<string>();
            List<SqliteParameter> parameters = new List<SqliteParameter>();
            ApplyQueryFilters(query, conditions, parameters);
            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;
            cmd.CommandText = "SELECT * FROM environments" + whereClause + " ORDER BY is_default DESC, active DESC, name ASC, created_utc DESC;";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);

            List<DeploymentEnvironment> results = new List<DeploymentEnvironment>();
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                results.Add(DeploymentEnvironmentColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
            return results;
        }

        private static void ApplyQueryFilters(
            DeploymentEnvironmentQuery? query,
            List<string> conditions,
            List<SqliteParameter> parameters)
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
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@vessel_id", query.VesselId));
            }
            if (query.Kind.HasValue)
            {
                conditions.Add("kind = @kind");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@kind", query.Kind.Value.ToString()));
            }
            if (query.IsDefault.HasValue)
            {
                conditions.Add("is_default = @is_default_filter");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Boolean(new SqliteParameter(), "@is_default_filter", "environments", "is_default", query.IsDefault.Value));
            }
            if (query.Active.HasValue)
            {
                conditions.Add("active = @active_filter");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Boolean(new SqliteParameter(), "@active_filter", "environments", "active", query.Active.Value));
            }
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search OR LOWER(COALESCE(configuration_source, '')) LIKE @search OR LOWER(COALESCE(base_url, '')) LIKE @search OR LOWER(COALESCE(health_endpoint, '')) LIKE @search)");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@search", "%" + query.Search.ToLowerInvariant() + "%"));
            }
        }

        private static void AddParameters(SqliteCommand cmd, DeploymentEnvironment environment)
        {
            DeploymentEnvironmentColumns.Write(SqliteDatabaseDriver.StoredBinder.For(cmd, "environments"), environment);
        }
    }
}
