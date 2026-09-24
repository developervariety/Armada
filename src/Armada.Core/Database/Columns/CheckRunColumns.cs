namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The check_runs row-to-model contract, shared by every provider. A type, status or source name outside the
    /// model reads as the model's default; an empty regression purpose reads as none. A summary or artifact
    /// document that is null or blank reads as absent, and one that is not valid JSON is named rather than read
    /// as absent.
    /// </summary>
    internal static class CheckRunColumns
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Read a check_runs row.
        /// </summary>
        /// <param name="record">Reader positioned on a check_runs row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The Check run.</returns>
        internal static CheckRun Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "CheckRun");
            CheckRun run = new CheckRun();
            run.Id = row.Text("id");
            run.TenantId = row.NullableText("tenant_id");
            run.UserId = row.NullableText("user_id");
            run.WorkflowProfileId = row.NullableText("workflow_profile_id");
            run.VesselId = row.NullableText("vessel_id");
            run.MissionId = row.NullableText("mission_id");
            run.VoyageId = row.NullableText("voyage_id");
            run.DeploymentId = row.NullableText("deployment_id");
            run.Label = row.NullableText("label");
            run.Type = row.EnumOrFallback("check_type", CheckRunTypeEnum.Build);
            run.Source = row.EnumOrFallback("source", CheckRunSourceEnum.Armada);
            run.Status = row.EnumOrFallback("status", CheckRunStatusEnum.Pending);
            run.ProviderName = row.NullableText("provider_name");
            run.ExternalId = row.NullableText("external_id");
            run.ExternalUrl = row.NullableText("external_url");
            run.EnvironmentName = row.NullableText("environment_name");
            run.Command = row.Text("command");
            run.WorkingDirectory = row.NullableText("working_directory");
            run.BranchName = row.NullableText("branch_name");
            run.CommitHash = row.NullableText("commit_hash");
            run.RegressionPurpose = String.IsNullOrWhiteSpace(row.TextOrNull("regression_purpose"))
                ? RegressionPurposeEnum.None
                : row.Enum<RegressionPurposeEnum>("regression_purpose");
            run.RegressionObjectiveId = row.TextOrNull("regression_objective_id");
            run.RegressionLandedCommit = row.TextOrNull("regression_landed_commit");
            run.ExitCode = row.NullableInt("exit_code");
            run.Output = row.NullableText("output");
            run.Summary = row.NullableText("summary");
            run.TestSummary = row.Json<CheckRunTestSummary>("test_summary_json", _Json);
            run.CoverageSummary = row.Json<CheckRunCoverageSummary>("coverage_summary_json", _Json);
            run.Artifacts = row.Json<List<CheckRunArtifact>>("artifacts_json", _Json) ?? new List<CheckRunArtifact>();
            run.DurationMs = row.NullableLong("duration_ms");
            run.SlotRequestedUtc = row.NullableUtc("slot_requested_utc");
            run.StartedUtc = row.NullableUtc("started_utc");
            run.CompletedUtc = row.NullableUtc("completed_utc");
            run.CreatedUtc = row.Utc("created_utc");
            run.LastUpdateUtc = row.Utc("last_update_utc");
            return run;
        }
    }
}
