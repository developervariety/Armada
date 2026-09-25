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

        /// <summary>
        /// Bind every stored deployments column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the deployments table.</param>
        /// <param name="deployment">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Deployment deployment)
        {
            parameters
                .Text("id", deployment.Id)
                .Text("tenant_id", deployment.TenantId)
                .Text("user_id", deployment.UserId)
                .Text("vessel_id", deployment.VesselId)
                .Text("workflow_profile_id", deployment.WorkflowProfileId)
                .Text("environment_id", deployment.EnvironmentId)
                .Text("environment_name", deployment.EnvironmentName)
                .Text("release_id", deployment.ReleaseId)
                .Text("mission_id", deployment.MissionId)
                .Text("voyage_id", deployment.VoyageId)
                .Text("title", deployment.Title)
                .Text("source_ref", deployment.SourceRef)
                .Text("summary", deployment.Summary)
                .Text("notes", deployment.Notes)
                .Text("status", deployment.Status.ToString())
                .Text("verification_status", deployment.VerificationStatus.ToString())
                .Bool("approval_required", deployment.ApprovalRequired)
                .Text("approved_by_user_id", deployment.ApprovedByUserId)
                .Utc("approved_utc", deployment.ApprovedUtc)
                .Text("approval_comment", deployment.ApprovalComment)
                .Text("deploy_check_run_id", deployment.DeployCheckRunId)
                .Text("smoke_test_check_run_id", deployment.SmokeTestCheckRunId)
                .Text("health_check_run_id", deployment.HealthCheckRunId)
                .Text("deployment_verification_check_run_id", deployment.DeploymentVerificationCheckRunId)
                .Text("rollback_check_run_id", deployment.RollbackCheckRunId)
                .Text("rollback_verification_check_run_id", deployment.RollbackVerificationCheckRunId)
                .Text("check_run_ids_json", JsonSerializer.Serialize(deployment.CheckRunIds ?? new List<string>(), _Json))
                .Text("request_history_summary_json", deployment.RequestHistorySummary != null ? JsonSerializer.Serialize(deployment.RequestHistorySummary, _Json) : null)
                .Utc("created_utc", deployment.CreatedUtc)
                .Utc("started_utc", deployment.StartedUtc)
                .Utc("completed_utc", deployment.CompletedUtc)
                .Utc("verified_utc", deployment.VerifiedUtc)
                .Utc("rolled_back_utc", deployment.RolledBackUtc)
                .Utc("monitoring_window_ends_utc", deployment.MonitoringWindowEndsUtc)
                .Utc("last_monitored_utc", deployment.LastMonitoredUtc)
                .Utc("last_regression_alert_utc", deployment.LastRegressionAlertUtc)
                .Text("latest_monitoring_summary", deployment.LatestMonitoringSummary)
                .Int("monitoring_failure_count", deployment.MonitoringFailureCount)
                .Utc("last_update_utc", deployment.LastUpdateUtc);
        }
    }
}
