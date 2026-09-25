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

        /// <summary>
        /// Bind every stored environments column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the environments table.</param>
        /// <param name="environment">Row to bind.</param>
        internal static void Write(StoredParameters parameters, DeploymentEnvironment environment)
        {
            parameters
                .Text("id", environment.Id)
                .Text("tenant_id", environment.TenantId)
                .Text("user_id", environment.UserId)
                .Text("vessel_id", environment.VesselId)
                .Text("name", environment.Name)
                .Text("description", environment.Description)
                .Text("kind", environment.Kind.ToString())
                .Text("configuration_source", environment.ConfigurationSource)
                .Text("base_url", environment.BaseUrl)
                .Text("health_endpoint", environment.HealthEndpoint)
                .Text("access_notes", environment.AccessNotes)
                .Text("deployment_rules", environment.DeploymentRules)
                .Text("verification_definitions_json", JsonSerializer.Serialize(environment.VerificationDefinitions ?? new List<DeploymentVerificationDefinition>(), _Json))
                .Int("rollout_monitoring_window_minutes", environment.RolloutMonitoringWindowMinutes)
                .Int("rollout_monitoring_interval_seconds", environment.RolloutMonitoringIntervalSeconds)
                .Bool("alert_on_regression", environment.AlertOnRegression)
                .Bool("requires_approval", environment.RequiresApproval)
                .Bool("is_default", environment.IsDefault)
                .Bool("active", environment.Active)
                .Utc("created_utc", environment.CreatedUtc)
                .Utc("last_update_utc", environment.LastUpdateUtc);
        }
    }
}
