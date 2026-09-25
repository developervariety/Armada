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
    /// PostgreSQL implementation of structured check-run persistence.
    /// </summary>
    public class CheckRunMethods : ICheckRunMethods
    {
        private readonly PostgresqlDatabaseDriver _Driver;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CheckRunMethods(PostgresqlDatabaseDriver driver)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        /// <inheritdoc />
        public async Task<CheckRun> CreateAsync(CheckRun checkRun, CancellationToken token = default)
        {
            if (checkRun == null) throw new ArgumentNullException(nameof(checkRun));
            checkRun.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO check_runs
                        (id, tenant_id, user_id, workflow_profile_id, vessel_id, mission_id, voyage_id, deployment_id, label, check_type, status,
                         source, provider_name, external_id, external_url, environment_name, command, working_directory, branch_name, commit_hash, exit_code, output, summary,
                         test_summary_json, coverage_summary_json, artifacts_json, duration_ms, started_utc, completed_utc, created_utc, last_update_utc, regression_purpose, regression_objective_id, regression_landed_commit, slot_requested_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @workflow_profile_id, @vessel_id, @mission_id, @voyage_id, @deployment_id, @label, @check_type, @status,
                         @source, @provider_name, @external_id, @external_url, @environment_name, @command, @working_directory, @branch_name, @commit_hash, @exit_code, @output, @summary,
                         @test_summary_json, @coverage_summary_json, @artifacts_json, @duration_ms, @started_utc, @completed_utc, @created_utc, @last_update_utc, @regression_purpose, @regression_objective_id, @regression_landed_commit, @slot_requested_utc);";
                    AddParameters(cmd, checkRun);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return checkRun;
        }

        /// <inheritdoc />
        public async Task<CheckRun?> ReadAsync(string id, CheckRunQuery? query = null, CancellationToken token = default)
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
                    cmd.CommandText = "SELECT * FROM check_runs WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CheckRunColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<CheckRun> UpdateAsync(CheckRun checkRun, CancellationToken token = default)
        {
            if (checkRun == null) throw new ArgumentNullException(nameof(checkRun));
            checkRun.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE check_runs SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        workflow_profile_id = @workflow_profile_id,
                        vessel_id = @vessel_id,
                        mission_id = @mission_id,
                        voyage_id = @voyage_id,
                        deployment_id = @deployment_id,
                        label = @label,
                        check_type = @check_type,
                        status = @status,
                        source = @source,
                        provider_name = @provider_name,
                        external_id = @external_id,
                        external_url = @external_url,
                        environment_name = @environment_name,
                        command = @command,
                        working_directory = @working_directory,
                        branch_name = @branch_name,
                        commit_hash = @commit_hash,
                        exit_code = @exit_code,
                        output = @output,
                        summary = @summary,
                        test_summary_json = @test_summary_json,
                        coverage_summary_json = @coverage_summary_json,
                        artifacts_json = @artifacts_json,
                        duration_ms = @duration_ms,
                        started_utc = @started_utc,
                        completed_utc = @completed_utc,
                        regression_purpose = @regression_purpose,
                        regression_objective_id = @regression_objective_id,
                        regression_landed_commit = @regression_landed_commit,
                        slot_requested_utc = @slot_requested_utc,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    AddParameters(cmd, checkRun);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return checkRun;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CheckRunQuery? query = null, CancellationToken token = default)
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
                    cmd.CommandText = "DELETE FROM check_runs WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<CheckRun>> EnumerateAsync(CheckRunQuery query, CancellationToken token = default)
        {
            query ??= new CheckRunQuery();
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
                    countCmd.CommandText = "SELECT COUNT(*) FROM check_runs" + whereClause + ";";
                    foreach (NpgsqlParameter parameter in parameters) countCmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<CheckRun> results = new List<CheckRun>();
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM check_runs" + whereClause
                        + " ORDER BY created_utc DESC, id DESC LIMIT @page_size OFFSET @offset;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    StoredValueBinder.Value(cmd, "@page_size", pageSize);
                    StoredValueBinder.Value(cmd, "@offset", offset);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CheckRunColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return new EnumerationResult<CheckRun>
                {
                    PageNumber = query.PageNumber,
                    PageSize = pageSize,
                    TotalRecords = totalCount,
                    TotalPages = pageSize > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0,
                    Objects = results
                };
            }
        }

        private static void ApplyQueryFilters(CheckRunQuery? query, List<string> conditions, List<NpgsqlParameter> parameters)
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
            if (!String.IsNullOrWhiteSpace(query.WorkflowProfileId))
            {
                conditions.Add("workflow_profile_id = @workflow_profile_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@workflow_profile_id", query.WorkflowProfileId));
            }
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@vessel_id", query.VesselId));
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
            if (!String.IsNullOrWhiteSpace(query.DeploymentId))
            {
                conditions.Add("deployment_id = @deployment_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@deployment_id", query.DeploymentId));
            }
            if (query.Type.HasValue)
            {
                conditions.Add("check_type = @check_type");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@check_type", query.Type.Value.ToString()));
            }
            if (query.Status.HasValue)
            {
                conditions.Add("status = @status");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@status", query.Status.Value.ToString()));
            }
            if (query.Source.HasValue)
            {
                conditions.Add("source = @source");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@source", query.Source.Value.ToString()));
            }
            if (!String.IsNullOrWhiteSpace(query.ProviderName))
            {
                conditions.Add("provider_name = @provider_name");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@provider_name", query.ProviderName));
            }
            if (!String.IsNullOrWhiteSpace(query.ExternalId))
            {
                conditions.Add("external_id = @external_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@external_id", query.ExternalId));
            }
            if (!String.IsNullOrWhiteSpace(query.EnvironmentName))
            {
                conditions.Add("environment_name = @environment_name");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@environment_name", query.EnvironmentName));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@from_utc", "check_runs", "created_utc", query.FromUtc.Value.ToUniversalTime()));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@to_utc", "check_runs", "created_utc", query.ToUtc.Value.ToUniversalTime()));
            }
        }

        private static void AddParameters(NpgsqlCommand cmd, CheckRun checkRun)
        {
            CheckRunColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "check_runs"), checkRun);
        }

        private static NpgsqlParameter CloneParameter(NpgsqlParameter parameter)
        {
            return new NpgsqlParameter(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        }
    }
}
