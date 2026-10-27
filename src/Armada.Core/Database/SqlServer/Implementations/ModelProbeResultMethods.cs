namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQL Server implementation of model probe result persistence.
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
        /// <param name="driver">SQL Server database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        public ModelProbeResultMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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
            await SqlServerCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (SqlConnection conn, SqlTransaction tx) =>
            {
                await SqlServerCommandHelper.ExecuteAsync(conn, tx, _Insert, cmd => Bind(cmd, result), token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return result;
        }

        /// <inheritdoc />
        public async Task<List<ModelProbeResult>> ReadByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            return await SqlServerCommandHelper.QueryAsync(_ConnectionString,
                "SELECT * FROM model_probe_results WHERE run_id = @run_id ORDER BY created_utc ASC, id ASC;",
                cmd => SqlServerCommandHelper.Add(cmd, "@run_id", runId), FromReader, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> DeleteByRunAsync(string runId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(runId)) throw new ArgumentNullException(nameof(runId));
            int deleted = 0;
            await SqlServerCommandHelper.WriteAsync(_ConnectionString, _WriteLock, async (SqlConnection conn, SqlTransaction tx) =>
            {
                deleted = await SqlServerCommandHelper.ExecuteAsync(conn, tx, "DELETE FROM model_probe_results WHERE run_id = @run_id;",
                    cmd => SqlServerCommandHelper.Add(cmd, "@run_id", runId), token).ConfigureAwait(false);
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
            string? json = SqlServerCommandHelper.ReadString(value);
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

        private static void Bind(SqlCommand cmd, ModelProbeResult result)
        {
            SqlServerCommandHelper.Add(cmd, "@id", result.Id);
            SqlServerCommandHelper.Add(cmd, "@run_id", result.RunId ?? String.Empty);
            SqlServerCommandHelper.Add(cmd, "@tenant_id", result.TenantId);
            SqlServerCommandHelper.Add(cmd, "@probe", result.Probe.ToString());
            SqlServerCommandHelper.Add(cmd, "@outcome", result.Outcome.ToString());
            SqlServerCommandHelper.Add(cmd, "@summary", result.Summary);
            SqlServerCommandHelper.Add(cmd, "@reason", result.Reason);
            SqlServerCommandHelper.Add(cmd, "@attempts", result.Attempts);
            SqlServerCommandHelper.Add(cmd, "@tokens_used", result.TokensUsed);
            SqlServerCommandHelper.Add(cmd, "@duration_ms", result.DurationMs);
            SqlServerCommandHelper.Add(cmd, "@metrics_json", SerializeMetrics(result.Metrics));
            SqlServerCommandHelper.Add(cmd, "@output_excerpt", result.OutputExcerpt);
            SqlServerCommandHelper.AddDate(cmd, "@created_utc", result.CreatedUtc);
        }

        private static ModelProbeResult FromReader(SqlDataReader reader)
        {
            ModelProbeResult result = new ModelProbeResult();
            result.Id = reader["id"].ToString()!;
            result.RunId = reader["run_id"]?.ToString() ?? String.Empty;
            result.TenantId = SqlServerCommandHelper.ReadString(reader["tenant_id"]);
            result.Probe = SqlServerCommandHelper.ReadEnum(reader["probe"], ModelProbeKindEnum.ServedContext);
            result.Outcome = SqlServerCommandHelper.ReadEnum(reader["outcome"], ModelProbeOutcomeEnum.CouldNotRun);
            result.Summary = SqlServerCommandHelper.ReadString(reader["summary"]);
            result.Reason = SqlServerCommandHelper.ReadString(reader["reason"]);
            result.Attempts = SqlServerCommandHelper.ReadInt(reader["attempts"], 0);
            result.TokensUsed = SqlServerCommandHelper.ReadNullableLong(reader["tokens_used"]) ?? 0;
            result.DurationMs = SqlServerCommandHelper.ReadNullableLong(reader["duration_ms"]) ?? 0;
            result.Metrics = DeserializeMetrics(reader["metrics_json"]);
            result.OutputExcerpt = SqlServerCommandHelper.ReadString(reader["output_excerpt"]);
            result.CreatedUtc = SqlServerCommandHelper.ReadDate(reader["created_utc"]);
            return result;
        }

        #endregion
    }
}
