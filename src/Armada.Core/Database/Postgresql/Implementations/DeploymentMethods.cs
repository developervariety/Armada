namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// PostgreSQL implementation of deployment persistence.
    /// </summary>
    public class DeploymentMethods : IDeploymentMethods
    {
        private readonly PostgresqlDatabaseDriver _Driver;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public DeploymentMethods(PostgresqlDatabaseDriver driver)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        /// <inheritdoc />
        public async Task<Deployment> CreateAsync(Deployment deployment, CancellationToken token = default)
        {
            if (deployment == null) throw new ArgumentNullException(nameof(deployment));
            deployment.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO deployments
                        (id, tenant_id, user_id, vessel_id, workflow_profile_id, environment_id, environment_name, release_id, mission_id, voyage_id,
                         title, source_ref, summary, notes, status, verification_status, approval_required, approved_by_user_id, approved_utc,
                         approval_comment, deploy_check_run_id, smoke_test_check_run_id, health_check_run_id, deployment_verification_check_run_id,
                         rollback_check_run_id, rollback_verification_check_run_id, check_run_ids_json, request_history_summary_json,
                         created_utc, started_utc, completed_utc, verified_utc, rolled_back_utc, monitoring_window_ends_utc,
                         last_monitored_utc, last_regression_alert_utc, latest_monitoring_summary, monitoring_failure_count, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @vessel_id, @workflow_profile_id, @environment_id, @environment_name, @release_id, @mission_id, @voyage_id,
                         @title, @source_ref, @summary, @notes, @status, @verification_status, @approval_required, @approved_by_user_id, @approved_utc,
                         @approval_comment, @deploy_check_run_id, @smoke_test_check_run_id, @health_check_run_id, @deployment_verification_check_run_id,
                         @rollback_check_run_id, @rollback_verification_check_run_id, @check_run_ids_json, @request_history_summary_json,
                         @created_utc, @started_utc, @completed_utc, @verified_utc, @rolled_back_utc, @monitoring_window_ends_utc,
                         @last_monitored_utc, @last_regression_alert_utc, @latest_monitoring_summary, @monitoring_failure_count, @last_update_utc);";
                    AddParameters(cmd, deployment);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return deployment;
        }

        /// <inheritdoc />
        public async Task<Deployment?> ReadAsync(string id, DeploymentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<NpgsqlParameter> parameters = new List<NpgsqlParameter> { StoredValueBinder.Parameter(new NpgsqlParameter(), "@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "SELECT * FROM deployments WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return DeploymentColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Deployment> UpdateAsync(Deployment deployment, CancellationToken token = default)
        {
            if (deployment == null) throw new ArgumentNullException(nameof(deployment));
            deployment.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE deployments SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        vessel_id = @vessel_id,
                        workflow_profile_id = @workflow_profile_id,
                        environment_id = @environment_id,
                        environment_name = @environment_name,
                        release_id = @release_id,
                        mission_id = @mission_id,
                        voyage_id = @voyage_id,
                        title = @title,
                        source_ref = @source_ref,
                        summary = @summary,
                        notes = @notes,
                        status = @status,
                        verification_status = @verification_status,
                        approval_required = @approval_required,
                        approved_by_user_id = @approved_by_user_id,
                        approved_utc = @approved_utc,
                        approval_comment = @approval_comment,
                        deploy_check_run_id = @deploy_check_run_id,
                        smoke_test_check_run_id = @smoke_test_check_run_id,
                        health_check_run_id = @health_check_run_id,
                        deployment_verification_check_run_id = @deployment_verification_check_run_id,
                        rollback_check_run_id = @rollback_check_run_id,
                        rollback_verification_check_run_id = @rollback_verification_check_run_id,
                        check_run_ids_json = @check_run_ids_json,
                        request_history_summary_json = @request_history_summary_json,
                        started_utc = @started_utc,
                        completed_utc = @completed_utc,
                        verified_utc = @verified_utc,
                        rolled_back_utc = @rolled_back_utc,
                        monitoring_window_ends_utc = @monitoring_window_ends_utc,
                        last_monitored_utc = @last_monitored_utc,
                        last_regression_alert_utc = @last_regression_alert_utc,
                        latest_monitoring_summary = @latest_monitoring_summary,
                        monitoring_failure_count = @monitoring_failure_count,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    AddParameters(cmd, deployment);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return deployment;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, DeploymentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<NpgsqlParameter> parameters = new List<NpgsqlParameter> { StoredValueBinder.Parameter(new NpgsqlParameter(), "@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "DELETE FROM deployments WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Deployment>> EnumerateAsync(DeploymentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentQuery();
            int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
            int offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * pageSize;

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

                long totalCount;
                using (NpgsqlCommand countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM deployments" + whereClause + ";";
                    foreach (NpgsqlParameter parameter in parameters) countCmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Deployment> results = new List<Deployment>();
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM deployments" + whereClause
                        + " ORDER BY COALESCE(completed_utc, started_utc, last_update_utc) DESC, created_utc DESC LIMIT @page_size OFFSET @offset;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    StoredValueBinder.Value(cmd, "@page_size", pageSize);
                    StoredValueBinder.Value(cmd, "@offset", offset);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(DeploymentColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return new EnumerationResult<Deployment>
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
        public async Task<List<Deployment>> EnumerateAllAsync(DeploymentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentQuery();

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

                List<Deployment> results = new List<Deployment>();
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM deployments" + whereClause + " ORDER BY COALESCE(completed_utc, started_utc, last_update_utc) DESC, created_utc DESC;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(DeploymentColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return results;
            }
        }

        private static void ApplyQueryFilters(DeploymentQuery? query, List<string> conditions, List<NpgsqlParameter> parameters)
        {
            if (query == null) return;

            if (!String.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@tenant_id", query.TenantId));
            }
            if (!String.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@user_id", query.UserId));
            }
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@vessel_id", query.VesselId));
            }
            if (!String.IsNullOrWhiteSpace(query.WorkflowProfileId))
            {
                conditions.Add("workflow_profile_id = @workflow_profile_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@workflow_profile_id", query.WorkflowProfileId));
            }
            if (!String.IsNullOrWhiteSpace(query.EnvironmentId))
            {
                conditions.Add("environment_id = @environment_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@environment_id", query.EnvironmentId));
            }
            if (!String.IsNullOrWhiteSpace(query.EnvironmentName))
            {
                conditions.Add("environment_name = @environment_name");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@environment_name", query.EnvironmentName));
            }
            if (!String.IsNullOrWhiteSpace(query.ReleaseId))
            {
                conditions.Add("release_id = @release_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@release_id", query.ReleaseId));
            }
            if (!String.IsNullOrWhiteSpace(query.MissionId))
            {
                conditions.Add("mission_id = @mission_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@mission_id", query.MissionId));
            }
            if (!String.IsNullOrWhiteSpace(query.VoyageId))
            {
                conditions.Add("voyage_id = @voyage_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@voyage_id", query.VoyageId));
            }
            if (!String.IsNullOrWhiteSpace(query.CheckRunId))
            {
                conditions.Add("check_run_ids_json LIKE @check_run_like");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@check_run_like", "%\"" + query.CheckRunId + "\"%"));
            }
            if (query.Status.HasValue)
            {
                conditions.Add("status = @status");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@status", query.Status.Value.ToString()));
            }
            if (query.VerificationStatus.HasValue)
            {
                conditions.Add("verification_status = @verification_status");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@verification_status", query.VerificationStatus.Value.ToString()));
            }
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("(LOWER(title) LIKE @search OR LOWER(COALESCE(source_ref, '')) LIKE @search OR LOWER(COALESCE(summary, '')) LIKE @search OR LOWER(COALESCE(notes, '')) LIKE @search OR LOWER(COALESCE(environment_name, '')) LIKE @search)");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@search", "%" + query.Search.ToLowerInvariant() + "%"));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@from_utc", "deployments", "created_utc", query.FromUtc.Value.ToUniversalTime()));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@to_utc", "deployments", "created_utc", query.ToUtc.Value.ToUniversalTime()));
            }
        }

        private static void AddParameters(NpgsqlCommand cmd, Deployment deployment)
        {
            DeploymentColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "deployments"), deployment);
        }

        private static NpgsqlParameter CloneParameter(NpgsqlParameter parameter)
        {
            return new NpgsqlParameter(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        }
    }
}
