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
    }
}
