namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of battery run persistence.
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
        /// <param name="connectionString">MySQL connection string.</param>
        /// <exception cref="ArgumentNullException">Thrown when connectionString is null.</exception>
        public BatteryRunMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _WriteLock = null;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<BatteryRun> CreateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            run.LastUpdateUtc = DateTime.UtcNow;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                await MysqlCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, run, true), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return run;
        }

        /// <inheritdoc />
        public async Task<BatteryRun?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            List<BatteryRun> rows = await MysqlCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM battery_runs WHERE id = @id;",
                cmd => MysqlCommandHelper.Add(cmd, "@id", id), FromReader, token).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

        /// <inheritdoc />
        public async Task<BatteryRun> UpdateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            run.LastUpdateUtc = DateTime.UtcNow;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                await MysqlCommandHelper.ExecuteAsync(conn, tx, _Update, cmd => Bind(cmd, run, false), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return run;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            int deleted = 0;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                await MysqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM battery_task_results WHERE run_id = @id;",
                    cmd => MysqlCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
                deleted = await MysqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM battery_runs WHERE id = @id;",
                    cmd => MysqlCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
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

            Action<MySqlCommand> bind = cmd =>
            {
                if (query.TenantId != null) MysqlCommandHelper.Add(cmd, "@tenant_id", query.TenantId);
                if (query.CaptainId != null) MysqlCommandHelper.Add(cmd, "@captain_id", query.CaptainId);
                if (query.Status.HasValue) MysqlCommandHelper.Add(cmd, "@status", query.Status.Value.ToString());
            };

            string where = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                long total = await MysqlCommandHelper.CountAsync(conn, "SELECT COUNT(*) FROM battery_runs" + where + ";", bind, token).ConfigureAwait(false);
                List<BatteryRun> rows = await MysqlCommandHelper.QueryAsync(conn,
                    "SELECT * FROM battery_runs" + where + " ORDER BY created_utc DESC, id DESC" + MysqlCommandHelper.Page(offset, pageSize) + ";",
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

        private static void Bind(MySqlCommand cmd, BatteryRun run, bool includeCreateColumns)
        {
            MysqlCommandHelper.Add(cmd, "@id", run.Id);
            if (includeCreateColumns)
            {
                MysqlCommandHelper.Add(cmd, "@tenant_id", run.TenantId);
                MysqlCommandHelper.Add(cmd, "@user_id", run.UserId);
                MysqlCommandHelper.AddDate(cmd, "@created_utc", run.CreatedUtc);
            }

            MysqlCommandHelper.Add(cmd, "@captain_id", run.CaptainId);
            MysqlCommandHelper.Add(cmd, "@captain_name", run.CaptainName);
            MysqlCommandHelper.Add(cmd, "@packs", String.Join(",", run.Packs));
            MysqlCommandHelper.Add(cmd, "@battery_version", run.BatteryVersion ?? String.Empty);
            MysqlCommandHelper.Add(cmd, "@run_count", run.RunCount);
            MysqlCommandHelper.Add(cmd, "@status", run.Status.ToString());
            MysqlCommandHelper.Add(cmd, "@is_evaluation", run.IsEvaluation);
            MysqlCommandHelper.Add(cmd, "@pipeline", run.Pipeline ?? String.Empty);
            MysqlCommandHelper.Add(cmd, "@seed", run.Seed);
            MysqlCommandHelper.Add(cmd, "@sampling_json", run.SamplingJson);
            MysqlCommandHelper.Add(cmd, "@configuration_revision", run.ConfigurationRevision);
            MysqlCommandHelper.Add(cmd, "@configuration_json", run.ConfigurationJson);
            MysqlCommandHelper.Add(cmd, "@runtime", run.Runtime);
            MysqlCommandHelper.Add(cmd, "@model", run.Model);
            MysqlCommandHelper.Add(cmd, "@model_endpoint_id", run.ModelEndpointId);
            MysqlCommandHelper.Add(cmd, "@inference_host", run.InferenceHost);
            MysqlCommandHelper.Add(cmd, "@total_tasks", run.TotalTasks);
            MysqlCommandHelper.Add(cmd, "@passed_count", run.PassedCount);
            MysqlCommandHelper.Add(cmd, "@failed_count", run.FailedCount);
            MysqlCommandHelper.Add(cmd, "@could_not_run_count", run.CouldNotRunCount);
            MysqlCommandHelper.Add(cmd, "@pending_evaluation_count", run.PendingEvaluationCount);
            MysqlCommandHelper.Add(cmd, "@failure_reason", run.FailureReason);
            MysqlCommandHelper.AddDate(cmd, "@started_utc", run.StartedUtc);
            MysqlCommandHelper.AddDate(cmd, "@completed_utc", run.CompletedUtc);
            MysqlCommandHelper.AddDate(cmd, "@last_update_utc", run.LastUpdateUtc);
        }

        private static BatteryRun FromReader(MySqlDataReader reader)
        {
            BatteryRun run = new BatteryRun();
            run.Id = reader["id"].ToString()!;
            run.TenantId = MysqlCommandHelper.ReadString(reader["tenant_id"]);
            run.UserId = MysqlCommandHelper.ReadString(reader["user_id"]);
            run.CaptainId = MysqlCommandHelper.ReadString(reader["captain_id"]);
            run.CaptainName = MysqlCommandHelper.ReadString(reader["captain_name"]);
            run.Packs = SplitPacks(MysqlCommandHelper.ReadString(reader["packs"]));
            run.BatteryVersion = MysqlCommandHelper.ReadString(reader["battery_version"]) ?? String.Empty;
            run.RunCount = MysqlCommandHelper.ReadInt(reader["run_count"], 3);
            run.Status = MysqlCommandHelper.ReadEnum(reader["status"], BatteryRunStatusEnum.Queued);
            run.IsEvaluation = MysqlCommandHelper.ReadBool(reader["is_evaluation"], true);
            run.Pipeline = MysqlCommandHelper.ReadString(reader["pipeline"]) ?? String.Empty;
            run.Seed = MysqlCommandHelper.ReadNullableInt(reader["seed"]);
            run.SamplingJson = MysqlCommandHelper.ReadString(reader["sampling_json"]);
            run.ConfigurationRevision = MysqlCommandHelper.ReadString(reader["configuration_revision"]);
            run.ConfigurationJson = MysqlCommandHelper.ReadString(reader["configuration_json"]);
            run.Runtime = MysqlCommandHelper.ReadString(reader["runtime"]);
            run.Model = MysqlCommandHelper.ReadString(reader["model"]);
            run.ModelEndpointId = MysqlCommandHelper.ReadString(reader["model_endpoint_id"]);
            run.InferenceHost = MysqlCommandHelper.ReadString(reader["inference_host"]);
            run.TotalTasks = MysqlCommandHelper.ReadInt(reader["total_tasks"], 0);
            run.PassedCount = MysqlCommandHelper.ReadInt(reader["passed_count"], 0);
            run.FailedCount = MysqlCommandHelper.ReadInt(reader["failed_count"], 0);
            run.CouldNotRunCount = MysqlCommandHelper.ReadInt(reader["could_not_run_count"], 0);
            run.PendingEvaluationCount = MysqlCommandHelper.ReadInt(reader["pending_evaluation_count"], 0);
            run.FailureReason = MysqlCommandHelper.ReadString(reader["failure_reason"]);
            run.CreatedUtc = MysqlCommandHelper.ReadDate(reader["created_utc"]);
            run.StartedUtc = MysqlCommandHelper.ReadNullableDate(reader["started_utc"]);
            run.CompletedUtc = MysqlCommandHelper.ReadNullableDate(reader["completed_utc"]);
            run.LastUpdateUtc = MysqlCommandHelper.ReadDate(reader["last_update_utc"]);
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
