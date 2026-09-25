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
    /// MySQL implementation of deployment-environment persistence.
    /// </summary>
    public class DeploymentEnvironmentMethods : IDeploymentEnvironmentMethods
    {
        private readonly string _ConnectionString;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public DeploymentEnvironmentMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<DeploymentEnvironment> CreateAsync(DeploymentEnvironment environment, CancellationToken token = default)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            environment.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
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
                }
            }

            return environment;
        }

        /// <inheritdoc />
        public async Task<DeploymentEnvironment?> ReadAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default)
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
                    cmd.CommandText = "SELECT * FROM environments WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return DeploymentEnvironmentColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<DeploymentEnvironment> UpdateAsync(DeploymentEnvironment environment, CancellationToken token = default)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            environment.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
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
                }
            }

            return environment;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default)
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
                    cmd.CommandText = "DELETE FROM environments WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<DeploymentEnvironment>> EnumerateAsync(DeploymentEnvironmentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentEnvironmentQuery();
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
                    cmd.CommandText = "SELECT COUNT(*) FROM environments" + whereClause + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<DeploymentEnvironment> results = new List<DeploymentEnvironment>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM environments" + whereClause
                        + " ORDER BY is_default DESC, active DESC, name ASC, created_utc DESC LIMIT " + pageSize + " OFFSET " + offset + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(DeploymentEnvironmentColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return new EnumerationResult<DeploymentEnvironment>
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
        public async Task<List<DeploymentEnvironment>> EnumerateAllAsync(DeploymentEnvironmentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentEnvironmentQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

                List<DeploymentEnvironment> results = new List<DeploymentEnvironment>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM environments" + whereClause + " ORDER BY is_default DESC, active DESC, name ASC, created_utc DESC;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(DeploymentEnvironmentColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return results;
            }
        }

        private static void ApplyQueryFilters(DeploymentEnvironmentQuery? query, List<string> conditions, List<MySqlParameter> parameters)
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
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@vessel_id", query.VesselId));
            }
            if (query.Kind.HasValue)
            {
                conditions.Add("kind = @kind");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@kind", query.Kind.Value.ToString()));
            }
            if (query.IsDefault.HasValue)
            {
                conditions.Add("is_default = @is_default_filter");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Boolean(new MySqlParameter(), "@is_default_filter", "environments", "is_default", query.IsDefault.Value));
            }
            if (query.Active.HasValue)
            {
                conditions.Add("active = @active_filter");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Boolean(new MySqlParameter(), "@active_filter", "environments", "active", query.Active.Value));
            }
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search OR LOWER(COALESCE(configuration_source, '')) LIKE @search OR LOWER(COALESCE(base_url, '')) LIKE @search OR LOWER(COALESCE(health_endpoint, '')) LIKE @search)");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@search", "%" + query.Search.ToLowerInvariant() + "%"));
            }
        }

        private static void AddParameters(MySqlCommand cmd, DeploymentEnvironment environment)
        {
            DeploymentEnvironmentColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "environments"), environment);
        }

        private static MySqlParameter CloneParameter(MySqlParameter parameter)
        {
            return new MySqlParameter(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        }
    }
}
