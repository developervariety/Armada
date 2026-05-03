namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// SQL Server implementation of structured check-run persistence.
    /// </summary>
    public class CheckRunMethods : ICheckRunMethods
    {
        private readonly SqlServerDatabaseDriver _Driver;
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CheckRunMethods(SqlServerDatabaseDriver driver)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        /// <inheritdoc />
        public async Task<CheckRun> CreateAsync(CheckRun checkRun, CancellationToken token = default)
        {
            if (checkRun == null) throw new ArgumentNullException(nameof(checkRun));
            checkRun.LastUpdateUtc = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO check_runs
                        (id, tenant_id, user_id, workflow_profile_id, vessel_id, mission_id, voyage_id, label, check_type, status,
                         environment_name, command, working_directory, branch_name, commit_hash, exit_code, output, summary,
                         artifacts_json, duration_ms, started_utc, completed_utc, created_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @workflow_profile_id, @vessel_id, @mission_id, @voyage_id, @label, @check_type, @status,
                         @environment_name, @command, @working_directory, @branch_name, @commit_hash, @exit_code, @output, @summary,
                         @artifacts_json, @duration_ms, @started_utc, @completed_utc, @created_utc, @last_update_utc);";
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

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<SqlParameter> parameters = new List<SqlParameter> { new SqlParameter("@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "SELECT TOP 1 * FROM check_runs WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
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

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE check_runs SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        workflow_profile_id = @workflow_profile_id,
                        vessel_id = @vessel_id,
                        mission_id = @mission_id,
                        voyage_id = @voyage_id,
                        label = @label,
                        check_type = @check_type,
                        status = @status,
                        environment_name = @environment_name,
                        command = @command,
                        working_directory = @working_directory,
                        branch_name = @branch_name,
                        commit_hash = @commit_hash,
                        exit_code = @exit_code,
                        output = @output,
                        summary = @summary,
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

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<SqlParameter> parameters = new List<SqlParameter> { new SqlParameter("@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "DELETE FROM check_runs WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<CheckRun>> EnumerateAsync(CheckRunQuery query, CancellationToken token = default)
        {
            query ??= new CheckRunQuery();
            int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqlParameter> parameters = new List<SqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

                long totalCount;
                using (SqlCommand countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM check_runs" + whereClause + ";";
                    foreach (SqlParameter parameter in parameters) countCmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<CheckRun> results = new List<CheckRun>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM check_runs" + whereClause
                        + " ORDER BY created_utc DESC OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    cmd.Parameters.AddWithValue("@offset", query.Offset);
                    cmd.Parameters.AddWithValue("@page_size", pageSize);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
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

        private static void ApplyQueryFilters(CheckRunQuery? query, List<string> conditions, List<SqlParameter> parameters)
        {
            if (query == null) return;

            if (!String.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(new SqlParameter("@tenant_id", query.TenantId));
            }
            if (!String.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(new SqlParameter("@user_id", query.UserId));
            }
            if (!String.IsNullOrWhiteSpace(query.WorkflowProfileId))
            {
                conditions.Add("workflow_profile_id = @workflow_profile_id");
                parameters.Add(new SqlParameter("@workflow_profile_id", query.WorkflowProfileId));
            }
            if (!String.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(new SqlParameter("@vessel_id", query.VesselId));
            }
            if (!String.IsNullOrWhiteSpace(query.MissionId))
            {
                conditions.Add("mission_id = @mission_id");
                parameters.Add(new SqlParameter("@mission_id", query.MissionId));
            }
            if (!String.IsNullOrWhiteSpace(query.VoyageId))
            {
                conditions.Add("voyage_id = @voyage_id");
                parameters.Add(new SqlParameter("@voyage_id", query.VoyageId));
            }
            if (query.Type.HasValue)
            {
                conditions.Add("check_type = @check_type");
                parameters.Add(new SqlParameter("@check_type", query.Type.Value.ToString()));
            }
            if (query.Status.HasValue)
            {
                conditions.Add("status = @status");
                parameters.Add(new SqlParameter("@status", query.Status.Value.ToString()));
            }
            if (!String.IsNullOrWhiteSpace(query.EnvironmentName))
            {
                conditions.Add("environment_name = @environment_name");
                parameters.Add(new SqlParameter("@environment_name", query.EnvironmentName));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(new SqlParameter("@from_utc", SqlServerDatabaseDriver.ToIso8601(query.FromUtc.Value)));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(new SqlParameter("@to_utc", SqlServerDatabaseDriver.ToIso8601(query.ToUtc.Value)));
            }
        }

        private static void AddParameters(SqlCommand cmd, CheckRun checkRun)
        {
            cmd.Parameters.AddWithValue("@id", checkRun.Id);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)checkRun.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)checkRun.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@workflow_profile_id", (object?)checkRun.WorkflowProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vessel_id", (object?)checkRun.VesselId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mission_id", (object?)checkRun.MissionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@voyage_id", (object?)checkRun.VoyageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@label", (object?)checkRun.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@check_type", checkRun.Type.ToString());
            cmd.Parameters.AddWithValue("@status", checkRun.Status.ToString());
            cmd.Parameters.AddWithValue("@environment_name", (object?)checkRun.EnvironmentName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@command", checkRun.Command);
            cmd.Parameters.AddWithValue("@working_directory", (object?)checkRun.WorkingDirectory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@branch_name", (object?)checkRun.BranchName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@commit_hash", (object?)checkRun.CommitHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@exit_code", (object?)checkRun.ExitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@output", (object?)checkRun.Output ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@summary", (object?)checkRun.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@artifacts_json", JsonSerializer.Serialize(checkRun.Artifacts ?? new List<CheckRunArtifact>(), _Json));
            cmd.Parameters.AddWithValue("@duration_ms", (object?)checkRun.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@started_utc",
                checkRun.StartedUtc.HasValue
                    ? SqlServerDatabaseDriver.ToIso8601(checkRun.StartedUtc.Value)
                    : DBNull.Value);
            cmd.Parameters.AddWithValue("@completed_utc",
                checkRun.CompletedUtc.HasValue
                    ? SqlServerDatabaseDriver.ToIso8601(checkRun.CompletedUtc.Value)
                    : DBNull.Value);
            cmd.Parameters.AddWithValue("@created_utc", SqlServerDatabaseDriver.ToIso8601(checkRun.CreatedUtc));
            cmd.Parameters.AddWithValue("@last_update_utc", SqlServerDatabaseDriver.ToIso8601(checkRun.LastUpdateUtc));
        }

        private static CheckRun FromReader(SqlDataReader reader)
        {
            CheckRun run = new CheckRun
            {
                Id = reader["id"].ToString() ?? String.Empty,
                TenantId = SqlServerDatabaseDriver.NullableString(reader["tenant_id"]),
                UserId = SqlServerDatabaseDriver.NullableString(reader["user_id"]),
                WorkflowProfileId = SqlServerDatabaseDriver.NullableString(reader["workflow_profile_id"]),
                VesselId = SqlServerDatabaseDriver.NullableString(reader["vessel_id"]),
                MissionId = SqlServerDatabaseDriver.NullableString(reader["mission_id"]),
                VoyageId = SqlServerDatabaseDriver.NullableString(reader["voyage_id"]),
                Label = SqlServerDatabaseDriver.NullableString(reader["label"]),
                EnvironmentName = SqlServerDatabaseDriver.NullableString(reader["environment_name"]),
                Command = reader["command"].ToString() ?? String.Empty,
                WorkingDirectory = SqlServerDatabaseDriver.NullableString(reader["working_directory"]),
                BranchName = SqlServerDatabaseDriver.NullableString(reader["branch_name"]),
                CommitHash = SqlServerDatabaseDriver.NullableString(reader["commit_hash"]),
                ExitCode = SqlServerDatabaseDriver.NullableInt(reader["exit_code"]),
                Output = SqlServerDatabaseDriver.NullableString(reader["output"]),
                Summary = SqlServerDatabaseDriver.NullableString(reader["summary"]),
                DurationMs = reader["duration_ms"] == DBNull.Value ? null : Convert.ToInt64(reader["duration_ms"]),
                StartedUtc = SqlServerDatabaseDriver.FromIso8601Nullable(reader["started_utc"]),
                CompletedUtc = SqlServerDatabaseDriver.FromIso8601Nullable(reader["completed_utc"]),
                CreatedUtc = SqlServerDatabaseDriver.FromIso8601(reader["created_utc"].ToString()!),
                LastUpdateUtc = SqlServerDatabaseDriver.FromIso8601(reader["last_update_utc"].ToString()!)
            };

            if (Enum.TryParse(reader["check_type"].ToString(), true, out CheckRunTypeEnum type))
                run.Type = type;
            if (Enum.TryParse(reader["status"].ToString(), true, out CheckRunStatusEnum status))
                run.Status = status;

            string? artifactsJson = SqlServerDatabaseDriver.NullableString(reader["artifacts_json"]);
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

        private static SqlParameter CloneParameter(SqlParameter parameter)
        {
            return new SqlParameter(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        }
    }
}
