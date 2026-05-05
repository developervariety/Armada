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
    /// MySQL implementation of structured check-run persistence.
    /// </summary>
    public class CheckRunMethods : ICheckRunMethods
    {
        private readonly string _ConnectionString;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CheckRunMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<CheckRun> CreateAsync(CheckRun checkRun, CancellationToken token = default)
        {
            if (checkRun == null) throw new ArgumentNullException(nameof(checkRun));
            checkRun.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO check_runs
                        (id, tenant_id, user_id, workflow_profile_id, vessel_id, mission_id, voyage_id, deployment_id, label, check_type, status,
                         source, provider_name, external_id, external_url, environment_name, command, working_directory, branch_name, commit_hash, exit_code, output, summary,
                         test_summary_json, coverage_summary_json, artifacts_json, duration_ms, started_utc, completed_utc, created_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @workflow_profile_id, @vessel_id, @mission_id, @voyage_id, @deployment_id, @label, @check_type, @status,
                         @source, @provider_name, @external_id, @external_url, @environment_name, @command, @working_directory, @branch_name, @commit_hash, @exit_code, @output, @summary,
                         @test_summary_json, @coverage_summary_json, @artifacts_json, @duration_ms, @started_utc, @completed_utc, @created_utc, @last_update_utc);";
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<MySqlParameter> parameters = new List<MySqlParameter> { new MySqlParameter("@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "SELECT * FROM check_runs WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return FromReader(reader);
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<MySqlParameter> parameters = new List<MySqlParameter> { new MySqlParameter("@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "DELETE FROM check_runs WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
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
                    cmd.CommandText = "SELECT COUNT(*) FROM check_runs" + whereClause + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<CheckRun> results = new List<CheckRun>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM check_runs" + whereClause
                        + " ORDER BY created_utc DESC LIMIT " + pageSize + " OFFSET " + offset + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FromReader(reader));
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

        private static void ApplyQueryFilters(CheckRunQuery? query, List<string> conditions, List<MySqlParameter> parameters)
        {
            if (query == null) return;

            if (!String.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(new MySqlParameter("@tenant_id", query.TenantId));
            }
            if (!String.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(new MySqlParameter("@user_id", query.UserId));
            }
            if (!String.IsNullOrWhiteSpace(query.WorkflowProfileId))
            {
                conditions.Add("workflow_profile_id = @workflow_profile_id");
                parameters.Add(new MySqlParameter("@workflow_profile_id", query.WorkflowProfileId));
            }
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(new MySqlParameter("@vessel_id", query.VesselId));
            }
            if (!String.IsNullOrWhiteSpace(query.MissionId))
            {
                conditions.Add("mission_id = @mission_id");
                parameters.Add(new MySqlParameter("@mission_id", query.MissionId));
            }
            if (!String.IsNullOrWhiteSpace(query.VoyageId))
            {
                conditions.Add("voyage_id = @voyage_id");
                parameters.Add(new MySqlParameter("@voyage_id", query.VoyageId));
            }
            if (!String.IsNullOrWhiteSpace(query.DeploymentId))
            {
                conditions.Add("deployment_id = @deployment_id");
                parameters.Add(new MySqlParameter("@deployment_id", query.DeploymentId));
            }
            if (query.Type.HasValue)
            {
                conditions.Add("check_type = @check_type");
                parameters.Add(new MySqlParameter("@check_type", query.Type.Value.ToString()));
            }
            if (query.Status.HasValue)
            {
                conditions.Add("status = @status");
                parameters.Add(new MySqlParameter("@status", query.Status.Value.ToString()));
            }
            if (query.Source.HasValue)
            {
                conditions.Add("source = @source");
                parameters.Add(new MySqlParameter("@source", query.Source.Value.ToString()));
            }
            if (!String.IsNullOrWhiteSpace(query.ProviderName))
            {
                conditions.Add("provider_name = @provider_name");
                parameters.Add(new MySqlParameter("@provider_name", query.ProviderName));
            }
            if (!String.IsNullOrWhiteSpace(query.EnvironmentName))
            {
                conditions.Add("environment_name = @environment_name");
                parameters.Add(new MySqlParameter("@environment_name", query.EnvironmentName));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(new MySqlParameter("@from_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(new MySqlParameter("@to_utc", query.ToUtc.Value));
            }
        }

        private static void AddParameters(MySqlCommand cmd, CheckRun checkRun)
        {
            cmd.Parameters.AddWithValue("@id", checkRun.Id);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)checkRun.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)checkRun.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@workflow_profile_id", (object?)checkRun.WorkflowProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vessel_id", (object?)checkRun.VesselId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mission_id", (object?)checkRun.MissionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@voyage_id", (object?)checkRun.VoyageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@deployment_id", (object?)checkRun.DeploymentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@label", (object?)checkRun.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@check_type", checkRun.Type.ToString());
            cmd.Parameters.AddWithValue("@status", checkRun.Status.ToString());
            cmd.Parameters.AddWithValue("@source", checkRun.Source.ToString());
            cmd.Parameters.AddWithValue("@provider_name", (object?)checkRun.ProviderName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@external_id", (object?)checkRun.ExternalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@external_url", (object?)checkRun.ExternalUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@environment_name", (object?)checkRun.EnvironmentName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@command", checkRun.Command);
            cmd.Parameters.AddWithValue("@working_directory", (object?)checkRun.WorkingDirectory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@branch_name", (object?)checkRun.BranchName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@commit_hash", (object?)checkRun.CommitHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@exit_code", (object?)checkRun.ExitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@output", (object?)checkRun.Output ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@summary", (object?)checkRun.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@test_summary_json", checkRun.TestSummary != null ? JsonSerializer.Serialize(checkRun.TestSummary, _Json) : DBNull.Value);
            cmd.Parameters.AddWithValue("@coverage_summary_json", checkRun.CoverageSummary != null ? JsonSerializer.Serialize(checkRun.CoverageSummary, _Json) : DBNull.Value);
            cmd.Parameters.AddWithValue("@artifacts_json", JsonSerializer.Serialize(checkRun.Artifacts ?? new List<CheckRunArtifact>(), _Json));
            cmd.Parameters.AddWithValue("@duration_ms", (object?)checkRun.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@started_utc", (object?)checkRun.StartedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@completed_utc", (object?)checkRun.CompletedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_utc", checkRun.CreatedUtc);
            cmd.Parameters.AddWithValue("@last_update_utc", checkRun.LastUpdateUtc);
        }

        private static CheckRun FromReader(MySqlDataReader reader)
        {
            CheckRun run = new CheckRun
            {
                Id = reader["id"].ToString() ?? String.Empty,
                TenantId = MysqlDatabaseDriver.NullableString(reader["tenant_id"]),
                UserId = MysqlDatabaseDriver.NullableString(reader["user_id"]),
                WorkflowProfileId = MysqlDatabaseDriver.NullableString(reader["workflow_profile_id"]),
                VesselId = MysqlDatabaseDriver.NullableString(reader["vessel_id"]),
                MissionId = MysqlDatabaseDriver.NullableString(reader["mission_id"]),
                VoyageId = MysqlDatabaseDriver.NullableString(reader["voyage_id"]),
                DeploymentId = MysqlDatabaseDriver.NullableString(reader["deployment_id"]),
                Label = MysqlDatabaseDriver.NullableString(reader["label"]),
                ProviderName = MysqlDatabaseDriver.NullableString(reader["provider_name"]),
                ExternalId = MysqlDatabaseDriver.NullableString(reader["external_id"]),
                ExternalUrl = MysqlDatabaseDriver.NullableString(reader["external_url"]),
                EnvironmentName = MysqlDatabaseDriver.NullableString(reader["environment_name"]),
                Command = reader["command"].ToString() ?? String.Empty,
                WorkingDirectory = MysqlDatabaseDriver.NullableString(reader["working_directory"]),
                BranchName = MysqlDatabaseDriver.NullableString(reader["branch_name"]),
                CommitHash = MysqlDatabaseDriver.NullableString(reader["commit_hash"]),
                ExitCode = MysqlDatabaseDriver.NullableInt(reader["exit_code"]),
                Output = MysqlDatabaseDriver.NullableString(reader["output"]),
                Summary = MysqlDatabaseDriver.NullableString(reader["summary"]),
                DurationMs = reader["duration_ms"] == DBNull.Value ? null : Convert.ToInt64(reader["duration_ms"]),
                StartedUtc = reader["started_utc"] == DBNull.Value ? null : Convert.ToDateTime(reader["started_utc"]).ToUniversalTime(),
                CompletedUtc = reader["completed_utc"] == DBNull.Value ? null : Convert.ToDateTime(reader["completed_utc"]).ToUniversalTime(),
                CreatedUtc = Convert.ToDateTime(reader["created_utc"]).ToUniversalTime(),
                LastUpdateUtc = Convert.ToDateTime(reader["last_update_utc"]).ToUniversalTime()
            };

            if (Enum.TryParse(reader["check_type"].ToString(), true, out CheckRunTypeEnum type))
                run.Type = type;
            if (Enum.TryParse(reader["status"].ToString(), true, out CheckRunStatusEnum status))
                run.Status = status;
            if (Enum.TryParse(reader["source"].ToString(), true, out CheckRunSourceEnum source))
                run.Source = source;

            string? testSummaryJson = MysqlDatabaseDriver.NullableString(reader["test_summary_json"]);
            if (!String.IsNullOrWhiteSpace(testSummaryJson))
            {
                try
                {
                    run.TestSummary = JsonSerializer.Deserialize<CheckRunTestSummary>(testSummaryJson, _Json);
                }
                catch
                {
                    run.TestSummary = null;
                }
            }

            string? coverageSummaryJson = MysqlDatabaseDriver.NullableString(reader["coverage_summary_json"]);
            if (!String.IsNullOrWhiteSpace(coverageSummaryJson))
            {
                try
                {
                    run.CoverageSummary = JsonSerializer.Deserialize<CheckRunCoverageSummary>(coverageSummaryJson, _Json);
                }
                catch
                {
                    run.CoverageSummary = null;
                }
            }

            string? artifactsJson = MysqlDatabaseDriver.NullableString(reader["artifacts_json"]);
            if (!String.IsNullOrWhiteSpace(artifactsJson))
            {
                try
                {
                    run.Artifacts = JsonSerializer.Deserialize<List<CheckRunArtifact>>(artifactsJson, _Json) ?? new List<CheckRunArtifact>();
                }
                catch
                {
                    run.Artifacts = new List<CheckRunArtifact>();
                }
            }

            return run;
        }

        private static MySqlParameter CloneParameter(MySqlParameter parameter)
        {
            return new MySqlParameter(parameter.ParameterName, parameter.Value);
        }
    }
}
