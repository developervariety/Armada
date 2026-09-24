namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The environments row-to-model contract, shared by every provider. A kind name outside the model reads as
    /// the model's default kind; a verification list that is null or blank reads as empty, and one that is not
    /// valid JSON is named rather than read as empty.
    /// </summary>
    internal static class DeploymentEnvironmentColumns
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Read an environments row.
        /// </summary>
        /// <param name="record">Reader positioned on an environments row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The deployment environment.</returns>
        internal static DeploymentEnvironment Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "DeploymentEnvironment");
            DeploymentEnvironment environment = new DeploymentEnvironment();
            environment.Id = row.Text("id");
            environment.TenantId = row.NullableText("tenant_id");
            environment.UserId = row.NullableText("user_id");
            environment.VesselId = row.NullableText("vessel_id");
            environment.Name = row.Text("name");
            environment.Description = row.NullableText("description");
            environment.Kind = row.EnumOrFallback("kind", EnvironmentKindEnum.Development);
            environment.ConfigurationSource = row.NullableText("configuration_source");
            environment.BaseUrl = row.NullableText("base_url");
            environment.HealthEndpoint = row.NullableText("health_endpoint");
            environment.AccessNotes = row.NullableText("access_notes");
            environment.DeploymentRules = row.NullableText("deployment_rules");
            environment.VerificationDefinitions = row.Json<List<DeploymentVerificationDefinition>>("verification_definitions_json", _Json)
                ?? new List<DeploymentVerificationDefinition>();
            environment.RolloutMonitoringWindowMinutes = row.Int("rollout_monitoring_window_minutes");
            environment.RolloutMonitoringIntervalSeconds = row.Int("rollout_monitoring_interval_seconds");
            environment.AlertOnRegression = row.Bool("alert_on_regression");
            environment.RequiresApproval = row.Bool("requires_approval");
            environment.IsDefault = row.Bool("is_default");
            environment.Active = row.Bool("active");
            environment.CreatedUtc = row.Utc("created_utc");
            environment.LastUpdateUtc = row.Utc("last_update_utc");
            return environment;
        }
    }
}
