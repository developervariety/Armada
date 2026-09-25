namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Model endpoint persistence, one implementation for every provider. The health write compares and sets on the
    /// last update time, so a stale probe never overwrites a newer write.
    /// </summary>
    internal sealed class ModelEndpointMethods : IModelEndpointMethods
    {
        #region Internal-Members

        /// <summary>
        /// The model_endpoints table.
        /// </summary>
        internal static readonly StoredTable<ModelEndpoint> Table = new StoredTable<ModelEndpoint>(
            "model_endpoints",
            new[]
            {
                "id", "tenant_id", "user_id", "name", "kind", "scope", "provider", "base_url", "api_key", "model", "dimensionality", "timeout_ms", "enabled",
                "health_status", "last_health_check_utc", "last_health_error", "last_latency_ms", "health_history_json", "created_utc", "last_update_utc"
            },
            Array.Empty<string>(),
            ModelEndpointColumns.Read,
            ModelEndpointColumns.Write);

        /// <summary>
        /// Newest first.
        /// </summary>
        internal const string Order = "created_utc DESC";

        /// <summary>
        /// Rewrites the health columns only while the stored last update time still matches the expected one.
        /// </summary>
        internal const string HealthSql = "UPDATE model_endpoints SET health_status = @health_status, last_health_check_utc = @last_health_check_utc,"
            + " last_health_error = @last_health_error, last_latency_ms = @last_latency_ms, health_history_json = @health_history_json,"
            + " last_update_utc = @last_update_utc WHERE id = @id AND last_update_utc = @expected_last_update_utc;";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private readonly StoredMethods<ModelEndpoint> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal ModelEndpointMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<ModelEndpoint>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<ModelEndpoint> CreateAsync(ModelEndpoint endpoint, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            await _Rows.InsertAsync(endpoint, token).ConfigureAwait(false);
            return endpoint;
        }

        /// <inheritdoc />
        public async Task<ModelEndpoint> UpdateAsync(ModelEndpoint endpoint, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            await _Rows.UpdateAsync(endpoint, token).ConfigureAwait(false);
            return endpoint;
        }

        /// <inheritdoc />
        public async Task<bool> UpdateHealthAsync(ModelEndpoint endpoint, DateTime expectedLastUpdateUtc, CancellationToken token = default)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = HealthSql;
                _Rows.Dialect.Binder.For(command, Table.Name)
                    .Text("id", endpoint.Id)
                    .Text("health_status", endpoint.HealthStatus.ToString())
                    .Utc("last_health_check_utc", endpoint.LastHealthCheckUtc)
                    .Text("last_health_error", endpoint.LastHealthError)
                    .Long("last_latency_ms", endpoint.LastLatencyMs)
                    .Text("health_history_json", JsonSerializer.Serialize(endpoint.HealthHistory ?? new List<ModelEndpointHealthRecord>(), _Json))
                    .Utc("last_update_utc", endpoint.LastUpdateUtc)
                    .Utc("@expected_last_update_utc", "last_update_utc", expectedLastUpdateUtc);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            }
        }

        /// <inheritdoc />
        public Task<ModelEndpoint?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Table.Filter().Key("id", id), token);
        }

        /// <inheritdoc />
        public Task<ModelEndpoint?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Table.Filter().Key("tenant_id", tenantId).Key("id", id), token);
        }

        /// <inheritdoc />
        public Task<ModelEndpoint?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Table.Filter().Key("tenant_id", tenantId).Key("user_id", userId).Key("id", id), token);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Table.Filter().Key("id", id), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Table.Filter().Key("tenant_id", tenantId).Key("id", id), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<List<ModelEndpoint>> EnumerateAsync(CancellationToken token = default)
        {
            return _Rows.ListAsync(Table.Filter(), Order, token);
        }

        /// <inheritdoc />
        public Task<List<ModelEndpoint>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return _Rows.ListAsync(Table.Filter().Key("tenant_id", tenantId), Order, token);
        }

        /// <inheritdoc />
        public Task<List<ModelEndpoint>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            return _Rows.ListAsync(Table.Filter().Key("tenant_id", tenantId).Key("user_id", userId), Order, token);
        }

        /// <inheritdoc />
        public Task<bool> ExistsAnyAsync(CancellationToken token = default)
        {
            return _Rows.AnyAsync(Table.Filter(), token);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await _Rows.CountAsync(Table.Filter().Key("id", id), token).ConfigureAwait(false) > 0;
        }

        #endregion
    }
}
