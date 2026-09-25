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
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQL Server implementation of managed model endpoint persistence.
    /// </summary>
    public class ModelEndpointMethods : IModelEndpointMethods
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions();

        private readonly SqlServerDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelEndpointMethods"/> class.
        /// </summary>
        /// <param name="driver">SQL Server database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public ModelEndpointMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<ModelEndpoint> CreateAsync(ModelEndpoint endpoint, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO model_endpoints
                        (id, tenant_id, user_id, name, kind, scope, provider, base_url, api_key, model, dimensionality, timeout_ms, enabled, health_status, last_health_check_utc, last_health_error, last_latency_ms, health_history_json, created_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @name, @kind, @scope, @provider, @base_url, @api_key, @model, @dimensionality, @timeout_ms, @enabled, @health_status, @last_health_check_utc, @last_health_error, @last_latency_ms, @health_history_json, @created_utc, @last_update_utc);";
                    BindEndpoint(cmd, endpoint);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return endpoint;
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint> UpdateAsync(ModelEndpoint endpoint, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE model_endpoints SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        name = @name,
                        kind = @kind,
                        scope = @scope,
                        provider = @provider,
                        base_url = @base_url,
                        api_key = @api_key,
                        model = @model,
                        dimensionality = @dimensionality,
                        timeout_ms = @timeout_ms,
                        enabled = @enabled,
                        health_status = @health_status,
                        last_health_check_utc = @last_health_check_utc,
                        last_health_error = @last_health_error,
                        last_latency_ms = @last_latency_ms,
                        health_history_json = @health_history_json,
                        created_utc = @created_utc,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    BindEndpoint(cmd, endpoint);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return endpoint;
        }

        /// <inheritdoc />
        public async Task<bool> UpdateHealthAsync(ModelEndpoint endpoint, DateTime expectedLastUpdateUtc, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE model_endpoints SET
                        health_status = @health_status,
                        last_health_check_utc = @last_health_check_utc,
                        last_health_error = @last_health_error,
                        last_latency_ms = @last_latency_ms,
                        health_history_json = @health_history_json,
                        last_update_utc = @last_update_utc
                        WHERE id = @id AND last_update_utc = @expected_last_update_utc;";
                    StoredValueBinder.Value(cmd, "@id", endpoint.Id);
                    StoredValueBinder.Value(cmd, "@health_status", endpoint.HealthStatus.ToString());
                    SqlParameter lastHealthCheck = cmd.Parameters.Add("@last_health_check_utc", System.Data.SqlDbType.DateTime2);
                    lastHealthCheck.Value = endpoint.LastHealthCheckUtc.HasValue ? (object)endpoint.LastHealthCheckUtc.Value : DBNull.Value;
                    StoredValueBinder.Value(cmd, "@last_health_error", endpoint.LastHealthError);
                    StoredValueBinder.Value(cmd, "@last_latency_ms", endpoint.LastLatencyMs);
                    StoredValueBinder.Value(cmd, "@health_history_json", JsonSerializer.Serialize(endpoint.HealthHistory ?? new List<ModelEndpointHealthRecord>(), _Json));
                    SqlParameter lastUpdate = cmd.Parameters.Add("@last_update_utc", System.Data.SqlDbType.DateTime2);
                    lastUpdate.Value = endpoint.LastUpdateUtc;
                    SqlParameter expectedLastUpdate = cmd.Parameters.Add("@expected_last_update_utc", System.Data.SqlDbType.DateTime2);
                    expectedLastUpdate.Value = expectedLastUpdateUtc;
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT TOP 1 * FROM model_endpoints WHERE id = @id;",
                cmd => StoredValueBinder.Value(cmd, "@id", id),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT TOP 1 * FROM model_endpoints WHERE tenant_id = @tenant_id AND id = @id;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT TOP 1 * FROM model_endpoints WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@user_id", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await ExecuteDeleteAsync(
                "DELETE FROM model_endpoints WHERE id = @id;",
                cmd => StoredValueBinder.Value(cmd, "@id", id),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await ExecuteDeleteAsync(
                "DELETE FROM model_endpoints WHERE tenant_id = @tenant_id AND id = @id;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ModelEndpoint>> EnumerateAsync(CancellationToken token = default)
        {
            return await EnumerateInternalAsync(
                "SELECT * FROM model_endpoints ORDER BY created_utc DESC;",
                null,
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ModelEndpoint>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return await EnumerateInternalAsync(
                "SELECT * FROM model_endpoints WHERE tenant_id = @tenant_id ORDER BY created_utc DESC;",
                cmd => StoredValueBinder.Value(cmd, "@tenant_id", tenantId),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ModelEndpoint>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            return await EnumerateInternalAsync(
                "SELECT * FROM model_endpoints WHERE tenant_id = @tenant_id AND user_id = @user_id ORDER BY created_utc DESC;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@user_id", userId);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAnyAsync(CancellationToken token = default)
        {
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TOP 1 1 FROM model_endpoints;";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return result != null && result != DBNull.Value;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM model_endpoints WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                    return count > 0;
                }
            }
        }

        #endregion

        #region Private-Methods

        private async Task<ModelEndpoint?> ReadInternalAsync(string sql, Action<SqlCommand> parameterize, CancellationToken token)
        {
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return ModelEndpointColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        private async Task<List<ModelEndpoint>> EnumerateInternalAsync(string sql, Action<SqlCommand>? parameterize, CancellationToken token)
        {
            List<ModelEndpoint> results = new List<ModelEndpoint>();
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize?.Invoke(cmd);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(ModelEndpointColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        private async Task ExecuteDeleteAsync(string sql, Action<SqlCommand> parameterize, CancellationToken token)
        {
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void BindEndpoint(SqlCommand cmd, ModelEndpoint endpoint)
        {
            ModelEndpointColumns.Write(SqlServerDatabaseDriver.StoredBinder.For(cmd, "model_endpoints"), endpoint);
        }

        #endregion
    }
}
