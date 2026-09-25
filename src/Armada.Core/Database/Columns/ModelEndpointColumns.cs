namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The model_endpoints row-to-model contract, shared by every provider. The kind, scope and provider must name a
    /// defined member in any case; a health status outside the model reads as unknown. A health history that is
    /// null or blank reads as empty, and one that is not valid JSON is named rather than read as empty.
    /// </summary>
    internal static class ModelEndpointColumns
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions();

        /// <summary>
        /// Read a model_endpoints row.
        /// </summary>
        /// <param name="record">Reader positioned on a model_endpoints row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The model endpoint.</returns>
        internal static ModelEndpoint Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "ModelEndpoint");
            ModelEndpoint endpoint = new ModelEndpoint();
            endpoint.Id = row.Text("id");
            endpoint.TenantId = row.NullableText("tenant_id");
            endpoint.UserId = row.NullableText("user_id");
            endpoint.Name = row.Text("name");
            endpoint.Kind = row.Enum<ModelEndpointKindEnum>("kind", ignoreCase: true);
            endpoint.Scope = row.Enum<ScopeEnum>("scope", ignoreCase: true);
            endpoint.Provider = row.Enum<ModelProviderEnum>("provider", ignoreCase: true);
            endpoint.BaseUrl = row.Text("base_url");
            endpoint.Model = row.NullableText("model");
            endpoint.Dimensionality = row.Int("dimensionality");
            endpoint.TimeoutMs = row.Int("timeout_ms");
            endpoint.Enabled = row.Bool("enabled");
            endpoint.ApiKey = row.NullableText("api_key");
            endpoint.HealthStatus = row.EnumOrFallback("health_status", EndpointHealthStatusEnum.Unknown);
            endpoint.LastHealthCheckUtc = row.NullableUtc("last_health_check_utc");
            endpoint.LastHealthError = row.NullableText("last_health_error");
            endpoint.LastLatencyMs = row.NullableLong("last_latency_ms");
            endpoint.HealthHistory = row.Json<List<ModelEndpointHealthRecord>>("health_history_json", _Json) ?? new List<ModelEndpointHealthRecord>();
            endpoint.CreatedUtc = row.Utc("created_utc");
            endpoint.LastUpdateUtc = row.Utc("last_update_utc");
            return endpoint;
        }

        /// <summary>
        /// Bind every stored model_endpoints column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the model_endpoints table.</param>
        /// <param name="endpoint">Row to bind.</param>
        internal static void Write(StoredParameters parameters, ModelEndpoint endpoint)
        {
            parameters
                .Text("id", endpoint.Id)
                .Text("tenant_id", endpoint.TenantId)
                .Text("user_id", endpoint.UserId)
                .Text("name", endpoint.Name)
                .Text("kind", endpoint.Kind.ToString())
                .Text("scope", endpoint.Scope.ToString())
                .Text("provider", endpoint.Provider.ToString())
                .Text("base_url", endpoint.BaseUrl)
                .Text("model", endpoint.Model)
                .Int("dimensionality", endpoint.Dimensionality)
                .Int("timeout_ms", endpoint.TimeoutMs)
                .Bool("enabled", endpoint.Enabled)
                .Text("api_key", endpoint.ApiKey)
                .Text("health_status", endpoint.HealthStatus.ToString())
                .Utc("last_health_check_utc", endpoint.LastHealthCheckUtc)
                .Text("last_health_error", endpoint.LastHealthError)
                .Long("last_latency_ms", endpoint.LastLatencyMs)
                .Text("health_history_json", JsonSerializer.Serialize(endpoint.HealthHistory ?? new List<ModelEndpointHealthRecord>(), _Json))
                .Utc("created_utc", endpoint.CreatedUtc)
                .Utc("last_update_utc", endpoint.LastUpdateUtc);
        }
    }
}
