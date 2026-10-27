namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// PostgreSQL implementation of battery run persistence.
    /// </summary>
    public class BatteryRunMethods : IBatteryRunMethods
    {
        #region Private-Members

        private static readonly string _Insert = @"INSERT INTO battery_runs
            (id, tenant_id, user_id, captain_id, captain_name, packs, battery_version, run_count, status, is_evaluation,
             pipeline, seed, sampling_json, configuration_revision, configuration_json, runtime, model, model_endpoint_id,
             inference_host, total_tasks, passed_count, failed_count, could_not_run_count, pending_evaluation_count,
             failure_reason, created_utc, started_utc, completed_utc, last_update_utc)
            VALUES
            (@id, @tenant_id, @user_id, @captain_id, @captain_name, @packs, @battery_version, @run_count, @status, @is_evaluation,
             @pipeline, @seed, @sampling_json, @configuration_revision, @configuration_json, @runtime, @model, @model_endpoint_id,
             @inference_host, @total_tasks, @passed_count, @failed_count, @could_not_run_count, @pending_evaluation_count,
             @failure_reason, @created_utc, @started_utc, @completed_utc, @last_update_utc);";

        private static readonly string _Update = @"UPDATE battery_runs SET
            captain_id = @captain_id, captain_name = @captain_name, packs = @packs, battery_version = @battery_version,
            run_count = @run_count, status = @status, is_evaluation = @is_evaluation, pipeline = @pipeline, seed = @seed,
            sampling_json = @sampling_json, configuration_revision = @configuration_revision,
            configuration_json = @configuration_json, runtime = @runtime, model = @model, model_endpoint_id = @model_endpoint_id,
            inference_host = @inference_host, total_tasks = @total_tasks, passed_count = @passed_count,
            failed_count = @failed_count, could_not_run_count = @could_not_run_count,
            pending_evaluation_count = @pending_evaluation_count, failure_reason = @failure_reason,
            started_utc = @started_utc, completed_utc = @completed_utc, last_update_utc = @last_update_utc
            WHERE id = @id;";

        private readonly string _ConnectionString;
        private readonly SemaphoreSlim? _WriteLock;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">PostgreSQL database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        public BatteryRunMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            if (driver == null) throw new ArgumentNullException(nameof(driver));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            _ConnectionString = driver.ConnectionString;
            _WriteLock = null;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<BatteryRun> CreateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            run.LastUpdateUtc = DateTime.UtcNow;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                await PostgresqlCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, run, true), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return run;
        }

        /// <inheritdoc />
        public async Task<BatteryRun?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            List<BatteryRun> rows = await PostgresqlCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM battery_runs WHERE id = @id;",
                cmd => PostgresqlCommandHelper.Add(cmd, "@id", id), FromReader, token).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

        /// <inheritdoc />
        public async Task<BatteryRun> UpdateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            run.LastUpdateUtc = DateTime.UtcNow;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                await PostgresqlCommandHelper.ExecuteAsync(conn, tx, _Update, cmd => Bind(cmd, run, false), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return run;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            int deleted = 0;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                await PostgresqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM battery_task_results WHERE run_id = @id;",
                    cmd => PostgresqlCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
                deleted = await PostgresqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM battery_runs WHERE id = @id;",
                    cmd => PostgresqlCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return deleted > 0;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<BatteryRun>> EnumerateAsync(BatteryRunQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            int pageSize = query.PageSize < 1 ? 1 : (query.PageSize > 1000 ? 1000 : query.PageSize);
            int pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
            int offset = (pageNumber - 1) * pageSize;

            List<string> conditions = new List<string>();
            if (query.TenantId != null) conditions.Add("tenant_id = @tenant_id");
            if (query.CaptainId != null) conditions.Add("captain_id = @captain_id");
            if (query.Status.HasValue) conditions.Add("status = @status");

            Action<NpgsqlCommand> bind = cmd =>
            {
                if (query.TenantId != null) PostgresqlCommandHelper.Add(cmd, "@tenant_id", query.TenantId);
                if (query.CaptainId != null) PostgresqlCommandHelper.Add(cmd, "@captain_id", query.CaptainId);
                if (query.Status.HasValue) PostgresqlCommandHelper.Add(cmd, "@status", query.Status.Value.ToString());
            };

            string where = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                long total = await PostgresqlCommandHelper.CountAsync(conn, "SELECT COUNT(*) FROM battery_runs" + where + ";", bind, token).ConfigureAwait(false);
                List<BatteryRun> rows = await PostgresqlCommandHelper.QueryAsync(conn,
                    "SELECT * FROM battery_runs" + where + " ORDER BY created_utc DESC, id DESC" + PostgresqlCommandHelper.Page(offset, pageSize) + ";",
                    bind, FromReader, token).ConfigureAwait(false);

                EnumerationResult<BatteryRun> result = new EnumerationResult<BatteryRun>();
                result.PageNumber = pageNumber;
                result.PageSize = pageSize;
                result.TotalRecords = total;
                result.TotalPages = (int)Math.Ceiling((double)total / pageSize);
                result.Objects = rows;
                return result;
            }
        }

        #endregion

        #region Private-Methods

        private static void Bind(NpgsqlCommand cmd, BatteryRun run, bool includeCreateColumns)
        {
            PostgresqlCommandHelper.Add(cmd, "@id", run.Id);
            if (includeCreateColumns)
            {
                PostgresqlCommandHelper.Add(cmd, "@tenant_id", run.TenantId);
                PostgresqlCommandHelper.Add(cmd, "@user_id", run.UserId);
                PostgresqlCommandHelper.AddDate(cmd, "@created_utc", run.CreatedUtc);
            }

            PostgresqlCommandHelper.Add(cmd, "@captain_id", run.CaptainId);
            PostgresqlCommandHelper.Add(cmd, "@captain_name", run.CaptainName);
            PostgresqlCommandHelper.Add(cmd, "@packs", String.Join(",", run.Packs));
            PostgresqlCommandHelper.Add(cmd, "@battery_version", run.BatteryVersion ?? String.Empty);
            PostgresqlCommandHelper.Add(cmd, "@run_count", run.RunCount);
            PostgresqlCommandHelper.Add(cmd, "@status", run.Status.ToString());
            PostgresqlCommandHelper.Add(cmd, "@is_evaluation", run.IsEvaluation);
            PostgresqlCommandHelper.Add(cmd, "@pipeline", run.Pipeline ?? String.Empty);
            PostgresqlCommandHelper.Add(cmd, "@seed", run.Seed);
            PostgresqlCommandHelper.Add(cmd, "@sampling_json", run.SamplingJson);
            PostgresqlCommandHelper.Add(cmd, "@configuration_revision", run.ConfigurationRevision);
            PostgresqlCommandHelper.Add(cmd, "@configuration_json", run.ConfigurationJson);
            PostgresqlCommandHelper.Add(cmd, "@runtime", run.Runtime);
            PostgresqlCommandHelper.Add(cmd, "@model", run.Model);
            PostgresqlCommandHelper.Add(cmd, "@model_endpoint_id", run.ModelEndpointId);
            PostgresqlCommandHelper.Add(cmd, "@inference_host", run.InferenceHost);
            PostgresqlCommandHelper.Add(cmd, "@total_tasks", run.TotalTasks);
            PostgresqlCommandHelper.Add(cmd, "@passed_count", run.PassedCount);
            PostgresqlCommandHelper.Add(cmd, "@failed_count", run.FailedCount);
            PostgresqlCommandHelper.Add(cmd, "@could_not_run_count", run.CouldNotRunCount);
            PostgresqlCommandHelper.Add(cmd, "@pending_evaluation_count", run.PendingEvaluationCount);
            PostgresqlCommandHelper.Add(cmd, "@failure_reason", run.FailureReason);
            PostgresqlCommandHelper.AddDate(cmd, "@started_utc", run.StartedUtc);
            PostgresqlCommandHelper.AddDate(cmd, "@completed_utc", run.CompletedUtc);
            PostgresqlCommandHelper.AddDate(cmd, "@last_update_utc", run.LastUpdateUtc);
        }

        private static BatteryRun FromReader(NpgsqlDataReader reader)
        {
            BatteryRun run = new BatteryRun();
            run.Id = reader["id"].ToString()!;
            run.TenantId = PostgresqlCommandHelper.ReadString(reader["tenant_id"]);
            run.UserId = PostgresqlCommandHelper.ReadString(reader["user_id"]);
            run.CaptainId = PostgresqlCommandHelper.ReadString(reader["captain_id"]);
            run.CaptainName = PostgresqlCommandHelper.ReadString(reader["captain_name"]);
            run.Packs = SplitPacks(PostgresqlCommandHelper.ReadString(reader["packs"]));
            run.BatteryVersion = PostgresqlCommandHelper.ReadString(reader["battery_version"]) ?? String.Empty;
            run.RunCount = PostgresqlCommandHelper.ReadInt(reader["run_count"], 3);
            run.Status = PostgresqlCommandHelper.ReadEnum(reader["status"], BatteryRunStatusEnum.Queued);
            run.IsEvaluation = PostgresqlCommandHelper.ReadBool(reader["is_evaluation"], true);
            run.Pipeline = PostgresqlCommandHelper.ReadString(reader["pipeline"]) ?? String.Empty;
            run.Seed = PostgresqlCommandHelper.ReadNullableInt(reader["seed"]);
            run.SamplingJson = PostgresqlCommandHelper.ReadString(reader["sampling_json"]);
            run.ConfigurationRevision = PostgresqlCommandHelper.ReadString(reader["configuration_revision"]);
            run.ConfigurationJson = PostgresqlCommandHelper.ReadString(reader["configuration_json"]);
            run.Runtime = PostgresqlCommandHelper.ReadString(reader["runtime"]);
            run.Model = PostgresqlCommandHelper.ReadString(reader["model"]);
            run.ModelEndpointId = PostgresqlCommandHelper.ReadString(reader["model_endpoint_id"]);
            run.InferenceHost = PostgresqlCommandHelper.ReadString(reader["inference_host"]);
            run.TotalTasks = PostgresqlCommandHelper.ReadInt(reader["total_tasks"], 0);
            run.PassedCount = PostgresqlCommandHelper.ReadInt(reader["passed_count"], 0);
            run.FailedCount = PostgresqlCommandHelper.ReadInt(reader["failed_count"], 0);
            run.CouldNotRunCount = PostgresqlCommandHelper.ReadInt(reader["could_not_run_count"], 0);
            run.PendingEvaluationCount = PostgresqlCommandHelper.ReadInt(reader["pending_evaluation_count"], 0);
            run.FailureReason = PostgresqlCommandHelper.ReadString(reader["failure_reason"]);
            run.CreatedUtc = PostgresqlCommandHelper.ReadDate(reader["created_utc"]);
            run.StartedUtc = PostgresqlCommandHelper.ReadNullableDate(reader["started_utc"]);
            run.CompletedUtc = PostgresqlCommandHelper.ReadNullableDate(reader["completed_utc"]);
            run.LastUpdateUtc = PostgresqlCommandHelper.ReadDate(reader["last_update_utc"]);
            return run;
        }

        private static List<string> SplitPacks(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return new List<string>();
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        #endregion
    }
}
