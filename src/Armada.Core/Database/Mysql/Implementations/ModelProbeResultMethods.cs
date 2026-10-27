namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of model probe result persistence.
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
        private readonly SemaphoreSlim? _WriteLock;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="connectionString">MySQL connection string.</param>
        /// <exception cref="ArgumentNullException">Thrown when connectionString is null.</exception>
        public ModelProbeResultMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _WriteLock = null;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<ModelProbeResult> CreateAsync(ModelProbeResult result, CancellationToken token = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                await MysqlCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, result), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return result;
        }

        /// <inheritdoc />
        public async Task<List<ModelProbeResult>> ReadByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            return await MysqlCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM model_probe_results WHERE run_id = @run_id ORDER BY created_utc ASC, id ASC;",
                cmd => MysqlCommandHelper.Add(cmd, "@run_id", runId), FromReader, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> DeleteByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            int deleted = 0;
            await MysqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (MySqlConnection conn, MySqlTransaction tx) =>
            {
                deleted = await MysqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM model_probe_results WHERE run_id = @run_id;",
                    cmd => MysqlCommandHelper.Add(cmd, "@run_id", runId), token).ConfigureAwait(false);
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
            string? json = MysqlCommandHelper.ReadString(value);
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

        private static void Bind(MySqlCommand cmd, ModelProbeResult result)
        {
            MysqlCommandHelper.Add(cmd, "@id", result.Id);
            MysqlCommandHelper.Add(cmd, "@run_id", result.RunId ?? String.Empty);
            MysqlCommandHelper.Add(cmd, "@tenant_id", result.TenantId);
            MysqlCommandHelper.Add(cmd, "@probe", result.Probe.ToString());
            MysqlCommandHelper.Add(cmd, "@outcome", result.Outcome.ToString());
            MysqlCommandHelper.Add(cmd, "@summary", result.Summary);
            MysqlCommandHelper.Add(cmd, "@reason", result.Reason);
            MysqlCommandHelper.Add(cmd, "@attempts", result.Attempts);
            MysqlCommandHelper.Add(cmd, "@tokens_used", result.TokensUsed);
            MysqlCommandHelper.Add(cmd, "@duration_ms", result.DurationMs);
            MysqlCommandHelper.Add(cmd, "@metrics_json", SerializeMetrics(result.Metrics));
            MysqlCommandHelper.Add(cmd, "@output_excerpt", result.OutputExcerpt);
            MysqlCommandHelper.AddDate(cmd, "@created_utc", result.CreatedUtc);
        }

        private static ModelProbeResult FromReader(MySqlDataReader reader)
        {
            ModelProbeResult result = new ModelProbeResult();
            result.Id = reader["id"].ToString()!;
            result.RunId = reader["run_id"]?.ToString() ?? String.Empty;
            result.TenantId = MysqlCommandHelper.ReadString(reader["tenant_id"]);
            result.Probe = MysqlCommandHelper.ReadEnum(reader["probe"], ModelProbeKindEnum.ServedContext);
            result.Outcome = MysqlCommandHelper.ReadEnum(reader["outcome"], ModelProbeOutcomeEnum.CouldNotRun);
            result.Summary = MysqlCommandHelper.ReadString(reader["summary"]);
            result.Reason = MysqlCommandHelper.ReadString(reader["reason"]);
            result.Attempts = MysqlCommandHelper.ReadInt(reader["attempts"], 0);
            result.TokensUsed = MysqlCommandHelper.ReadNullableLong(reader["tokens_used"]) ?? 0;
            result.DurationMs = MysqlCommandHelper.ReadNullableLong(reader["duration_ms"]) ?? 0;
            result.Metrics = DeserializeMetrics(reader["metrics_json"]);
            result.OutputExcerpt = MysqlCommandHelper.ReadString(reader["output_excerpt"]);
            result.CreatedUtc = MysqlCommandHelper.ReadDate(reader["created_utc"]);
            return result;
        }

        #endregion
    }
}
