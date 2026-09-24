namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The deployments row-to-model contract, shared by every provider. A status or verification status name
    /// outside the model reads as the model's default; the Check list and request-history summary read as empty
    /// or absent when null or blank, and a document that is not valid JSON is named rather than read as empty.
    /// </summary>
    internal static class DeploymentColumns
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Read a deployments row.
        /// </summary>
        /// <param name="record">Reader positioned on a deployments row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The deployment.</returns>
        internal static Deployment Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Deployment");
            Deployment deployment = new Deployment();
            deployment.Id = row.Text("id");
            deployment.TenantId = row.NullableText("tenant_id");
            deployment.UserId = row.NullableText("user_id");
            deployment.VesselId = row.NullableText("vessel_id");
            deployment.WorkflowProfileId = row.NullableText("workflow_profile_id");
            deployment.EnvironmentId = row.NullableText("environment_id");
            deployment.EnvironmentName = row.NullableText("environment_name");
            deployment.ReleaseId = row.NullableText("release_id");
            deployment.MissionId = row.NullableText("mission_id");
            deployment.VoyageId = row.NullableText("voyage_id");
            deployment.Title = row.Text("title");
            deployment.SourceRef = row.NullableText("source_ref");
            deployment.Summary = row.NullableText("summary");
            deployment.Notes = row.NullableText("notes");
            deployment.Status = row.EnumOrFallback("status", DeploymentStatusEnum.PendingApproval);
            deployment.VerificationStatus = row.EnumOrFallback("verification_status", DeploymentVerificationStatusEnum.NotRun);
            deployment.ApprovalRequired = row.Bool("approval_required");
            deployment.ApprovedByUserId = row.NullableText("approved_by_user_id");
            deployment.ApprovedUtc = row.NullableUtc("approved_utc");
            deployment.ApprovalComment = row.NullableText("approval_comment");
            deployment.DeployCheckRunId = row.NullableText("deploy_check_run_id");
            deployment.SmokeTestCheckRunId = row.NullableText("smoke_test_check_run_id");
            deployment.HealthCheckRunId = row.NullableText("health_check_run_id");
            deployment.DeploymentVerificationCheckRunId = row.NullableText("deployment_verification_check_run_id");
            deployment.RollbackCheckRunId = row.NullableText("rollback_check_run_id");
            deployment.RollbackVerificationCheckRunId = row.NullableText("rollback_verification_check_run_id");
            deployment.CheckRunIds = row.Json<List<string>>("check_run_ids_json", _Json) ?? new List<string>();
            deployment.RequestHistorySummary = row.Json<RequestHistorySummaryResult>("request_history_summary_json", _Json);
            deployment.CreatedUtc = row.Utc("created_utc");
            deployment.StartedUtc = row.NullableUtc("started_utc");
            deployment.CompletedUtc = row.NullableUtc("completed_utc");
            deployment.VerifiedUtc = row.NullableUtc("verified_utc");
            deployment.RolledBackUtc = row.NullableUtc("rolled_back_utc");
            deployment.MonitoringWindowEndsUtc = row.NullableUtc("monitoring_window_ends_utc");
            deployment.LastMonitoredUtc = row.NullableUtc("last_monitored_utc");
            deployment.LastRegressionAlertUtc = row.NullableUtc("last_regression_alert_utc");
            deployment.LatestMonitoringSummary = row.NullableText("latest_monitoring_summary");
            deployment.MonitoringFailureCount = row.Int("monitoring_failure_count");
            deployment.LastUpdateUtc = row.Utc("last_update_utc");
            return deployment;
        }
    }
}
