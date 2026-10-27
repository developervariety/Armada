namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
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
    /// SQLite implementation of model probe result persistence.
    /// </summary>
    public class ModelProbeResultMethods : IModelProbeResultMethods
    {
        #region Private-Members

        private static readonly string _Insert = @"INSERT INTO model_probe_results
            (id, run_id, tenant_id, probe, outcome, summary, reason, attempts, tokens_used, duration_ms, metrics_json,
             output_excerpt, created_utc)
            VALUES
            (@id, @run_id, @tenant_id, @probe, @outcome, @summary, @reason, @attempts, @tokens_used, @duration_ms, @metrics_json,
             @output_excerpt, @created_utc);";

        private static readonly JsonSerializerOptions _Json = CreateJsonOptions();

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
        public ModelProbeResultMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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
        public async Task<ModelProbeResult> CreateAsync(ModelProbeResult result, CancellationToken token = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            await SqliteCommandHelper.WriteAsync(_ConnectionString, _WriteGate, async (SqliteConnection conn, SqliteTransaction tx) =>
            {
                await SqliteCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, result), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return result;
        }

        /// <inheritdoc />
        public async Task<List<ModelProbeResult>> ReadByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            return await SqliteCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM model_probe_results WHERE run_id = @run_id ORDER BY created_utc ASC, id ASC;",
                cmd => SqliteCommandHelper.Add(cmd, "@run_id", runId), FromReader, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> DeleteByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            int deleted = 0;
            await SqliteCommandHelper.WriteAsync(_ConnectionString, _WriteGate, async (SqliteConnection conn, SqliteTransaction tx) =>
            {
                deleted = await SqliteCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM model_probe_results WHERE run_id = @run_id;",
                    cmd => SqliteCommandHelper.Add(cmd, "@run_id", runId), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return deleted;
        }

        #endregion

        #region Private-Methods

        private static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.PropertyNameCaseInsensitive = true;
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private static string SerializeMetrics(ModelProbeMetrics? metrics)
        {
            return JsonSerializer.Serialize(metrics ?? new ModelProbeMetrics(), _Json);
        }

        private static ModelProbeMetrics DeserializeMetrics(object value)
        {
            string? json = SqliteCommandHelper.ReadString(value);
            if (String.IsNullOrEmpty(json)) return new ModelProbeMetrics();
            try
            {
                ModelProbeMetrics? metrics = JsonSerializer.Deserialize<ModelProbeMetrics>(json, _Json);
                return metrics ?? new ModelProbeMetrics();
            }
            catch (JsonException)
            {
                return new ModelProbeMetrics();
            }
        }

        private static void Bind(SqliteCommand cmd, ModelProbeResult result)
        {
            SqliteCommandHelper.Add(cmd, "@id", result.Id);
            SqliteCommandHelper.Add(cmd, "@run_id", result.RunId ?? String.Empty);
            SqliteCommandHelper.Add(cmd, "@tenant_id", result.TenantId);
            SqliteCommandHelper.Add(cmd, "@probe", result.Probe.ToString());
            SqliteCommandHelper.Add(cmd, "@outcome", result.Outcome.ToString());
            SqliteCommandHelper.Add(cmd, "@summary", result.Summary);
            SqliteCommandHelper.Add(cmd, "@reason", result.Reason);
            SqliteCommandHelper.Add(cmd, "@attempts", result.Attempts);
            SqliteCommandHelper.Add(cmd, "@tokens_used", result.TokensUsed);
            SqliteCommandHelper.Add(cmd, "@duration_ms", result.DurationMs);
            SqliteCommandHelper.Add(cmd, "@metrics_json", SerializeMetrics(result.Metrics));
            SqliteCommandHelper.Add(cmd, "@output_excerpt", result.OutputExcerpt);
            SqliteCommandHelper.AddDate(cmd, "@created_utc", result.CreatedUtc);
        }

        private static ModelProbeResult FromReader(SqliteDataReader reader)
        {
            ModelProbeResult result = new ModelProbeResult();
            result.Id = reader["id"].ToString()!;
            result.RunId = reader["run_id"]?.ToString() ?? String.Empty;
            result.TenantId = SqliteCommandHelper.ReadString(reader["tenant_id"]);
            result.Probe = SqliteCommandHelper.ReadEnum(reader["probe"], ModelProbeKindEnum.ServedContext);
            result.Outcome = SqliteCommandHelper.ReadEnum(reader["outcome"], ModelProbeOutcomeEnum.CouldNotRun);
            result.Summary = SqliteCommandHelper.ReadString(reader["summary"]);
            result.Reason = SqliteCommandHelper.ReadString(reader["reason"]);
            result.Attempts = SqliteCommandHelper.ReadInt(reader["attempts"], 0);
            result.TokensUsed = SqliteCommandHelper.ReadNullableLong(reader["tokens_used"]) ?? 0;
            result.DurationMs = SqliteCommandHelper.ReadNullableLong(reader["duration_ms"]) ?? 0;
            result.Metrics = DeserializeMetrics(reader["metrics_json"]);
            result.OutputExcerpt = SqliteCommandHelper.ReadString(reader["output_excerpt"]);
            result.CreatedUtc = SqliteCommandHelper.ReadDate(reader["created_utc"]);
            return result;
        }

        #endregion
    }
}
