namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
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
    /// PostgreSQL implementation of model probe result persistence.
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
        /// <param name="driver">PostgreSQL database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        public ModelProbeResultMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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
        public async Task<ModelProbeResult> CreateAsync(ModelProbeResult result, CancellationToken token = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                await PostgresqlCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, result), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return result;
        }

        /// <inheritdoc />
        public async Task<List<ModelProbeResult>> ReadByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            return await PostgresqlCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM model_probe_results WHERE run_id = @run_id ORDER BY created_utc ASC, id ASC;",
                cmd => PostgresqlCommandHelper.Add(cmd, "@run_id", runId), FromReader, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> DeleteByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            int deleted = 0;
            await PostgresqlCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (NpgsqlConnection conn, NpgsqlTransaction tx) =>
            {
                deleted = await PostgresqlCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM model_probe_results WHERE run_id = @run_id;",
                    cmd => PostgresqlCommandHelper.Add(cmd, "@run_id", runId), token).ConfigureAwait(false);
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
            string? json = PostgresqlCommandHelper.ReadString(value);
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

        private static void Bind(NpgsqlCommand cmd, ModelProbeResult result)
        {
            PostgresqlCommandHelper.Add(cmd, "@id", result.Id);
            PostgresqlCommandHelper.Add(cmd, "@run_id", result.RunId ?? String.Empty);
            PostgresqlCommandHelper.Add(cmd, "@tenant_id", result.TenantId);
            PostgresqlCommandHelper.Add(cmd, "@probe", result.Probe.ToString());
            PostgresqlCommandHelper.Add(cmd, "@outcome", result.Outcome.ToString());
            PostgresqlCommandHelper.Add(cmd, "@summary", result.Summary);
            PostgresqlCommandHelper.Add(cmd, "@reason", result.Reason);
            PostgresqlCommandHelper.Add(cmd, "@attempts", result.Attempts);
            PostgresqlCommandHelper.Add(cmd, "@tokens_used", result.TokensUsed);
            PostgresqlCommandHelper.Add(cmd, "@duration_ms", result.DurationMs);
            PostgresqlCommandHelper.Add(cmd, "@metrics_json", SerializeMetrics(result.Metrics));
            PostgresqlCommandHelper.Add(cmd, "@output_excerpt", result.OutputExcerpt);
            PostgresqlCommandHelper.AddDate(cmd, "@created_utc", result.CreatedUtc);
        }

        private static ModelProbeResult FromReader(NpgsqlDataReader reader)
        {
            ModelProbeResult result = new ModelProbeResult();
            result.Id = reader["id"].ToString()!;
            result.RunId = reader["run_id"]?.ToString() ?? String.Empty;
            result.TenantId = PostgresqlCommandHelper.ReadString(reader["tenant_id"]);
            result.Probe = PostgresqlCommandHelper.ReadEnum(reader["probe"], ModelProbeKindEnum.ServedContext);
            result.Outcome = PostgresqlCommandHelper.ReadEnum(reader["outcome"], ModelProbeOutcomeEnum.CouldNotRun);
            result.Summary = PostgresqlCommandHelper.ReadString(reader["summary"]);
            result.Reason = PostgresqlCommandHelper.ReadString(reader["reason"]);
            result.Attempts = PostgresqlCommandHelper.ReadInt(reader["attempts"], 0);
            result.TokensUsed = PostgresqlCommandHelper.ReadNullableLong(reader["tokens_used"]) ?? 0;
            result.DurationMs = PostgresqlCommandHelper.ReadNullableLong(reader["duration_ms"]) ?? 0;
            result.Metrics = DeserializeMetrics(reader["metrics_json"]);
            result.OutputExcerpt = PostgresqlCommandHelper.ReadString(reader["output_excerpt"]);
            result.CreatedUtc = PostgresqlCommandHelper.ReadDate(reader["created_utc"]);
            return result;
        }

        #endregion
    }
}
