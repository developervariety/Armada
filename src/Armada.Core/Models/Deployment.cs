namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// First-class deployment execution record with approval, verification, and rollback state.
    /// </summary>
    public class Deployment
    {
        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value.Trim();
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User who requested the deployment.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Vessel being deployed.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Workflow profile used for deploy, verify, and rollback.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Target environment identifier.
        /// </summary>
        public string? EnvironmentId { get; set; } = null;

        /// <summary>
        /// Target environment name.
        /// </summary>
        public string? EnvironmentName { get; set; } = null;

        /// <summary>
        /// Optional linked release identifier.
        /// </summary>
        public string? ReleaseId { get; set; } = null;

        /// <summary>
        /// Optional linked mission identifier.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Optional linked voyage identifier.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Human-facing deployment title.
        /// </summary>
        public string Title
        {
            get => _Title;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Title));
                _Title = value.Trim();
            }
        }

        /// <summary>
        /// Optional freeform source ref such as a branch, tag, or commit.
        /// </summary>
        public string? SourceRef { get; set; } = null;

        /// <summary>
        /// Full commit the source ref resolved to when the deployment was created or its source last changed.
        /// Approval, required Check evidence and the deploy and rollback Checks all name this commit, so a branch
        /// that moves later does not change what the deployment deploys. Null only when the vessel has no git
        /// repository to resolve the ref in.
        /// </summary>
        public string? SourceCommit { get; set; } = null;

        /// <summary>
        /// Optional identity of the built artifact the deployment ships, such as an image digest or a package
        /// version with its checksum. It is fixed with the source commit once the deployment is approved or starts.
        /// </summary>
        public string? ArtifactIdentity { get; set; } = null;

        /// <summary>
        /// Source commit the approver approved. Execution of a deployment that requires approval refuses to start
        /// when this differs from <see cref="SourceCommit"/>.
        /// </summary>
        public string? ApprovedSourceCommit { get; set; } = null;

        /// <summary>
        /// Artifact identity the approver approved. Execution of a deployment that requires approval refuses to
        /// start when this differs from <see cref="ArtifactIdentity"/>.
        /// </summary>
        public string? ApprovedArtifactIdentity { get; set; } = null;

        /// <summary>
        /// Check runs that proved the environment's required Check types at the source commit.
        /// </summary>
        public List<string> ValidationCheckRunIds { get; set; } = new List<string>();

        /// <summary>
        /// Outcome of the latest required-evidence evaluation, with the reason when it refused.
        /// </summary>
        public string? ValidationSummary { get; set; } = null;

        /// <summary>
        /// Optional short summary.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// Optional operator notes.
        /// </summary>
        public string? Notes { get; set; } = null;

        /// <summary>
        /// Deployment lifecycle state.
        /// </summary>
        public DeploymentStatusEnum Status { get; set; } = DeploymentStatusEnum.PendingApproval;

        /// <summary>
        /// Post-deploy verification state.
        /// </summary>
        public DeploymentVerificationStatusEnum VerificationStatus { get; set; } = DeploymentVerificationStatusEnum.NotRun;

        /// <summary>
        /// Whether approval is required before deploy execution.
        /// </summary>
        public bool ApprovalRequired { get; set; } = false;

        /// <summary>
        /// User who approved or denied the deployment when applicable.
        /// </summary>
        public string? ApprovedByUserId { get; set; } = null;

        /// <summary>
        /// Approval or denial timestamp.
        /// </summary>
        public DateTime? ApprovedUtc { get; set; } = null;

        /// <summary>
        /// Optional approval comment.
        /// </summary>
        public string? ApprovalComment { get; set; } = null;

        /// <summary>
        /// Linked deploy check-run identifier.
        /// </summary>
        public string? DeployCheckRunId { get; set; } = null;

        /// <summary>
        /// Linked smoke-test check-run identifier.
        /// </summary>
        public string? SmokeTestCheckRunId { get; set; } = null;

        /// <summary>
        /// Linked health-check check-run identifier.
        /// </summary>
        public string? HealthCheckRunId { get; set; } = null;

        /// <summary>
        /// Linked deployment-verification check-run identifier.
        /// </summary>
        public string? DeploymentVerificationCheckRunId { get; set; } = null;

        /// <summary>
        /// Linked rollback check-run identifier.
        /// </summary>
        public string? RollbackCheckRunId { get; set; } = null;

        /// <summary>
        /// Linked rollback-verification check-run identifier.
        /// </summary>
        public string? RollbackVerificationCheckRunId { get; set; } = null;

        /// <summary>
        /// All linked check-run identifiers associated with the deployment lifecycle.
        /// </summary>
        public List<string> CheckRunIds { get; set; } = new List<string>();

        /// <summary>
        /// Optional request-history aggregate captured during deployment and verification.
        /// </summary>
        public RequestHistorySummaryResult? RequestHistorySummary { get; set; } = null;

        /// <summary>
        /// Creation timestamp.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Start timestamp once execution begins.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// Completion timestamp once deploy and verification finish.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>
        /// Timestamp of the latest successful verification pass.
        /// </summary>
        public DateTime? VerifiedUtc { get; set; } = null;

        /// <summary>
        /// Timestamp of the latest successful rollback.
        /// </summary>
        public DateTime? RolledBackUtc { get; set; } = null;

        /// <summary>
        /// End of the rollout-monitoring window for this deployment when configured.
        /// </summary>
        public DateTime? MonitoringWindowEndsUtc { get; set; } = null;

        /// <summary>
        /// Timestamp of the latest rollout-monitoring pass.
        /// </summary>
        public DateTime? LastMonitoredUtc { get; set; } = null;

        /// <summary>
        /// Timestamp of the latest regression alert emitted for this deployment.
        /// </summary>
        public DateTime? LastRegressionAlertUtc { get; set; } = null;

        /// <summary>
        /// Human-readable summary of the latest rollout monitoring pass.
        /// </summary>
        public string? LatestMonitoringSummary { get; set; } = null;

        /// <summary>
        /// Number of rollout-monitoring failures observed after initial verification.
        /// </summary>
        public int MonitoringFailureCount { get; set; } = 0;

        /// <summary>
        /// Last update timestamp.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.DeploymentIdPrefix, 24);
        private string _Title = "Deployment";
    }
}
