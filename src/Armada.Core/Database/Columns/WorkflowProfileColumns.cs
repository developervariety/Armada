namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The workflow_profiles row-to-model contract, shared by every provider. A scope name outside the model reads
    /// as the model's default scope; a JSON list or map that is null or blank reads as empty, and one that is not
    /// valid JSON is named rather than read as empty.
    /// </summary>
    internal static class WorkflowProfileColumns
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Read a workflow_profiles row.
        /// </summary>
        /// <param name="record">Reader positioned on a workflow_profiles row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The workflow profile.</returns>
        internal static WorkflowProfile Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "WorkflowProfile");
            WorkflowProfile profile = new WorkflowProfile();
            profile.Id = row.Text("id");
            profile.TenantId = row.NullableText("tenant_id");
            profile.UserId = row.NullableText("user_id");
            profile.Name = row.Text("name");
            profile.Description = row.NullableText("description");
            profile.Scope = row.EnumOrFallback("scope", WorkflowProfileScopeEnum.Global);
            profile.FleetId = row.NullableText("fleet_id");
            profile.VesselId = row.NullableText("vessel_id");
            profile.IsDefault = row.Bool("is_default");
            profile.Active = row.Bool("active");
            profile.LintCommand = row.NullableText("lint_command");
            profile.BuildCommand = row.NullableText("build_command");
            profile.UnitTestCommand = row.NullableText("unit_test_command");
            profile.ContainerlessUnitTestCommand = row.NullableText("containerless_unit_test_command");
            profile.IntegrationTestCommand = row.NullableText("integration_test_command");
            profile.E2ETestCommand = row.NullableText("e2e_test_command");
            profile.PackageCommand = row.NullableText("package_command");
            profile.PublishArtifactCommand = row.NullableText("publish_artifact_command");
            profile.ReleaseVersioningCommand = row.NullableText("release_versioning_command");
            profile.ChangelogGenerationCommand = row.NullableText("changelog_generation_command");
            profile.MigrationCommand = row.NullableText("migration_command");
            profile.SecurityScanCommand = row.NullableText("security_scan_command");
            profile.PerformanceCommand = row.NullableText("performance_command");
            profile.DeploymentVerificationCommand = row.NullableText("deployment_verification_command");
            profile.RollbackVerificationCommand = row.NullableText("rollback_verification_command");
            profile.LanguageHints = row.Json<List<string>>("language_hints_json", _Json) ?? new List<string>();
            profile.RequiredSecrets = row.Json<List<string>>("required_secrets_json", _Json) ?? new List<string>();
            profile.ExpectedArtifacts = row.Json<List<string>>("expected_artifacts_json", _Json) ?? new List<string>();
            profile.Environments = row.Json<List<WorkflowEnvironmentProfile>>("environments_json", _Json) ?? new List<WorkflowEnvironmentProfile>();
            profile.EnvironmentVariables = row.Json<Dictionary<string, string>>("environment_variables_json", _Json) ?? new Dictionary<string, string>();
            profile.CreatedUtc = row.Utc("created_utc");
            profile.LastUpdateUtc = row.Utc("last_update_utc");
            return profile;
        }

        /// <summary>
        /// Bind every stored workflow_profiles column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the workflow_profiles table.</param>
        /// <param name="profile">Row to bind.</param>
        internal static void Write(StoredParameters parameters, WorkflowProfile profile)
        {
            parameters
                .Text("id", profile.Id)
                .Text("tenant_id", profile.TenantId)
                .Text("user_id", profile.UserId)
                .Text("name", profile.Name)
                .Text("description", profile.Description)
                .Text("scope", profile.Scope.ToString())
                .Text("fleet_id", profile.FleetId)
                .Text("vessel_id", profile.VesselId)
                .Bool("is_default", profile.IsDefault)
                .Bool("active", profile.Active)
                .Text("lint_command", profile.LintCommand)
                .Text("build_command", profile.BuildCommand)
                .Text("unit_test_command", profile.UnitTestCommand)
                .Text("containerless_unit_test_command", profile.ContainerlessUnitTestCommand)
                .Text("integration_test_command", profile.IntegrationTestCommand)
                .Text("package_command", profile.PackageCommand)
                .Text("publish_artifact_command", profile.PublishArtifactCommand)
                .Text("release_versioning_command", profile.ReleaseVersioningCommand)
                .Text("changelog_generation_command", profile.ChangelogGenerationCommand)
                .Text("migration_command", profile.MigrationCommand)
                .Text("security_scan_command", profile.SecurityScanCommand)
                .Text("performance_command", profile.PerformanceCommand)
                .Text("deployment_verification_command", profile.DeploymentVerificationCommand)
                .Text("rollback_verification_command", profile.RollbackVerificationCommand)
                .Text("language_hints_json", Serialize(profile.LanguageHints))
                .Text("required_secrets_json", Serialize(profile.RequiredSecrets))
                .Text("expected_artifacts_json", Serialize(profile.ExpectedArtifacts))
                .Text("environments_json", Serialize(profile.Environments))
                .Text("environment_variables_json", Serialize(profile.EnvironmentVariables))
                .Utc("created_utc", profile.CreatedUtc)
                .Utc("last_update_utc", profile.LastUpdateUtc);
        }

        /// <summary>
        /// A list or map column is stored as JSON, and an absent one as its empty form.
        /// </summary>
        private static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value ?? System.Activator.CreateInstance<T>(), _Json);
        }
    }
}
