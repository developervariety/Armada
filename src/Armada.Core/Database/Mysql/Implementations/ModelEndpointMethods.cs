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
    /// MySQL implementation of managed model endpoint persistence.
    /// </summary>
    public class ModelEndpointMethods : IModelEndpointMethods
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions();

        private readonly string _ConnectionString;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelEndpointMethods"/> class.
        /// </summary>
        public ModelEndpointMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint> CreateAsync(ModelEndpoint endpoint, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
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
                    MysqlDatabaseDriver.StoredBinder.For(cmd, "model_endpoints").Utc("@last_health_check_utc", "last_health_check_utc", endpoint.LastHealthCheckUtc);
                    StoredValueBinder.Value(cmd, "@last_health_error", endpoint.LastHealthError);
                    StoredValueBinder.Value(cmd, "@last_latency_ms", endpoint.LastLatencyMs);
                    StoredValueBinder.Value(cmd, "@health_history_json", JsonSerializer.Serialize(endpoint.HealthHistory ?? new List<ModelEndpointHealthRecord>(), _Json));
                    MysqlDatabaseDriver.StoredBinder.For(cmd, "model_endpoints").Utc("@last_update_utc", "last_update_utc", endpoint.LastUpdateUtc);
                    MysqlDatabaseDriver.StoredBinder.For(cmd, "model_endpoints").Utc("@expected_last_update_utc", "last_update_utc", expectedLastUpdateUtc);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM model_endpoints WHERE id = @id;",
                cmd => StoredValueBinder.Value(cmd, "@id", id),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM model_endpoints WHERE tenant_id = @tenant_id AND id = @id;",
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
                "SELECT * FROM model_endpoints WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;",
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
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1 FROM model_endpoints LIMIT 1;";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return result != null && result != DBNull.Value;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM model_endpoints WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                    return count > 0;
                }
            }
        }

        private async Task<ModelEndpoint?> ReadInternalAsync(string sql, Action<MySqlCommand> parameterize, CancellationToken token)
        {
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return ModelEndpointColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        private async Task<List<ModelEndpoint>> EnumerateInternalAsync(string sql, Action<MySqlCommand>? parameterize, CancellationToken token)
        {
            List<ModelEndpoint> results = new List<ModelEndpoint>();
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize?.Invoke(cmd);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(ModelEndpointColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        private async Task ExecuteDeleteAsync(string sql, Action<MySqlCommand> parameterize, CancellationToken token)
        {
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void BindEndpoint(MySqlCommand cmd, ModelEndpoint endpoint)
        {
            ModelEndpointColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "model_endpoints"), endpoint);
        }

    }
}
