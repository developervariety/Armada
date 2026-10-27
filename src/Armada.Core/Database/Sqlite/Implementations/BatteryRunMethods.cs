namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQLite implementation of battery run persistence.
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
        private readonly SqliteWriteGate _WriteGate;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">SQLite database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        public BatteryRunMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            if (driver == null) throw new ArgumentNullException(nameof(driver));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            _ConnectionString = driver.ConnectionString;
            _WriteGate = driver.WriteGate;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<BatteryRun> CreateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            run.LastUpdateUtc = DateTime.UtcNow;
            await SqliteCommandHelper.WriteAsync(_ConnectionString, _WriteGate, async (SqliteConnection conn, SqliteTransaction tx) =>
            {
                await SqliteCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, run, true), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return run;
        }

        /// <inheritdoc />
        public async Task<BatteryRun?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            List<BatteryRun> rows = await SqliteCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM battery_runs WHERE id = @id;",
                cmd => SqliteCommandHelper.Add(cmd, "@id", id), FromReader, token).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

        /// <inheritdoc />
        public async Task<BatteryRun> UpdateAsync(BatteryRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            run.LastUpdateUtc = DateTime.UtcNow;
            await SqliteCommandHelper.WriteAsync(_ConnectionString, _WriteGate, async (SqliteConnection conn, SqliteTransaction tx) =>
            {
                await SqliteCommandHelper.ExecuteAsync(conn, tx, _Update, cmd => Bind(cmd, run, false), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return run;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            int deleted = 0;
            await SqliteCommandHelper.WriteAsync(_ConnectionString, _WriteGate, async (SqliteConnection conn, SqliteTransaction tx) =>
            {
                await SqliteCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM battery_task_results WHERE run_id = @id;",
                    cmd => SqliteCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
                deleted = await SqliteCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM battery_runs WHERE id = @id;",
                    cmd => SqliteCommandHelper.Add(cmd, "@id", id), token).ConfigureAwait(false);
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

            Action<SqliteCommand> bind = cmd =>
            {
                if (query.TenantId != null) SqliteCommandHelper.Add(cmd, "@tenant_id", query.TenantId);
                if (query.CaptainId != null) SqliteCommandHelper.Add(cmd, "@captain_id", query.CaptainId);
                if (query.Status.HasValue) SqliteCommandHelper.Add(cmd, "@status", query.Status.Value.ToString());
            };

            string where = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            using (SqliteConnection conn = new SqliteProviderConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                long total = await SqliteCommandHelper.CountAsync(conn, "SELECT COUNT(*) FROM battery_runs" + where + ";", bind, token).ConfigureAwait(false);
                List<BatteryRun> rows = await SqliteCommandHelper.QueryAsync(conn,
                    "SELECT * FROM battery_runs" + where + " ORDER BY created_utc DESC, id DESC" + SqliteCommandHelper.Page(offset, pageSize) + ";",
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

        private static void Bind(SqliteCommand cmd, BatteryRun run, bool includeCreateColumns)
        {
            SqliteCommandHelper.Add(cmd, "@id", run.Id);
            if (includeCreateColumns)
            {
                SqliteCommandHelper.Add(cmd, "@tenant_id", run.TenantId);
                SqliteCommandHelper.Add(cmd, "@user_id", run.UserId);
                SqliteCommandHelper.AddDate(cmd, "@created_utc", run.CreatedUtc);
            }

            SqliteCommandHelper.Add(cmd, "@captain_id", run.CaptainId);
            SqliteCommandHelper.Add(cmd, "@captain_name", run.CaptainName);
            SqliteCommandHelper.Add(cmd, "@packs", String.Join(",", run.Packs));
            SqliteCommandHelper.Add(cmd, "@battery_version", run.BatteryVersion ?? String.Empty);
            SqliteCommandHelper.Add(cmd, "@run_count", run.RunCount);
            SqliteCommandHelper.Add(cmd, "@status", run.Status.ToString());
            SqliteCommandHelper.Add(cmd, "@is_evaluation", run.IsEvaluation);
            SqliteCommandHelper.Add(cmd, "@pipeline", run.Pipeline ?? String.Empty);
            SqliteCommandHelper.Add(cmd, "@seed", run.Seed);
            SqliteCommandHelper.Add(cmd, "@sampling_json", run.SamplingJson);
            SqliteCommandHelper.Add(cmd, "@configuration_revision", run.ConfigurationRevision);
            SqliteCommandHelper.Add(cmd, "@configuration_json", run.ConfigurationJson);
            SqliteCommandHelper.Add(cmd, "@runtime", run.Runtime);
            SqliteCommandHelper.Add(cmd, "@model", run.Model);
            SqliteCommandHelper.Add(cmd, "@model_endpoint_id", run.ModelEndpointId);
            SqliteCommandHelper.Add(cmd, "@inference_host", run.InferenceHost);
            SqliteCommandHelper.Add(cmd, "@total_tasks", run.TotalTasks);
            SqliteCommandHelper.Add(cmd, "@passed_count", run.PassedCount);
            SqliteCommandHelper.Add(cmd, "@failed_count", run.FailedCount);
            SqliteCommandHelper.Add(cmd, "@could_not_run_count", run.CouldNotRunCount);
            SqliteCommandHelper.Add(cmd, "@pending_evaluation_count", run.PendingEvaluationCount);
            SqliteCommandHelper.Add(cmd, "@failure_reason", run.FailureReason);
            SqliteCommandHelper.AddDate(cmd, "@started_utc", run.StartedUtc);
            SqliteCommandHelper.AddDate(cmd, "@completed_utc", run.CompletedUtc);
            SqliteCommandHelper.AddDate(cmd, "@last_update_utc", run.LastUpdateUtc);
        }

        private static BatteryRun FromReader(SqliteDataReader reader)
        {
            BatteryRun run = new BatteryRun();
            run.Id = reader["id"].ToString()!;
            run.TenantId = SqliteCommandHelper.ReadString(reader["tenant_id"]);
            run.UserId = SqliteCommandHelper.ReadString(reader["user_id"]);
            run.CaptainId = SqliteCommandHelper.ReadString(reader["captain_id"]);
            run.CaptainName = SqliteCommandHelper.ReadString(reader["captain_name"]);
            run.Packs = SplitPacks(SqliteCommandHelper.ReadString(reader["packs"]));
            run.BatteryVersion = SqliteCommandHelper.ReadString(reader["battery_version"]) ?? String.Empty;
            run.RunCount = SqliteCommandHelper.ReadInt(reader["run_count"], 3);
            run.Status = SqliteCommandHelper.ReadEnum(reader["status"], BatteryRunStatusEnum.Queued);
            run.IsEvaluation = SqliteCommandHelper.ReadBool(reader["is_evaluation"], true);
            run.Pipeline = SqliteCommandHelper.ReadString(reader["pipeline"]) ?? String.Empty;
            run.Seed = SqliteCommandHelper.ReadNullableInt(reader["seed"]);
            run.SamplingJson = SqliteCommandHelper.ReadString(reader["sampling_json"]);
            run.ConfigurationRevision = SqliteCommandHelper.ReadString(reader["configuration_revision"]);
            run.ConfigurationJson = SqliteCommandHelper.ReadString(reader["configuration_json"]);
            run.Runtime = SqliteCommandHelper.ReadString(reader["runtime"]);
            run.Model = SqliteCommandHelper.ReadString(reader["model"]);
            run.ModelEndpointId = SqliteCommandHelper.ReadString(reader["model_endpoint_id"]);
            run.InferenceHost = SqliteCommandHelper.ReadString(reader["inference_host"]);
            run.TotalTasks = SqliteCommandHelper.ReadInt(reader["total_tasks"], 0);
            run.PassedCount = SqliteCommandHelper.ReadInt(reader["passed_count"], 0);
            run.FailedCount = SqliteCommandHelper.ReadInt(reader["failed_count"], 0);
            run.CouldNotRunCount = SqliteCommandHelper.ReadInt(reader["could_not_run_count"], 0);
            run.PendingEvaluationCount = SqliteCommandHelper.ReadInt(reader["pending_evaluation_count"], 0);
            run.FailureReason = SqliteCommandHelper.ReadString(reader["failure_reason"]);
            run.CreatedUtc = SqliteCommandHelper.ReadDate(reader["created_utc"]);
            run.StartedUtc = SqliteCommandHelper.ReadNullableDate(reader["started_utc"]);
            run.CompletedUtc = SqliteCommandHelper.ReadNullableDate(reader["completed_utc"]);
            run.LastUpdateUtc = SqliteCommandHelper.ReadDate(reader["last_update_utc"]);
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
