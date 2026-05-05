namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Query parameters for deployment APIs and storage.
    /// </summary>
    public class DeploymentQuery
    {
        /// <summary>
        /// Tenant scope or filter.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User scope or filter.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Vessel filter.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Workflow-profile filter.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Environment identifier filter.
        /// </summary>
        public string? EnvironmentId { get; set; } = null;

        /// <summary>
        /// Environment-name filter.
        /// </summary>
        public string? EnvironmentName { get; set; } = null;

        /// <summary>
        /// Linked release filter.
        /// </summary>
        public string? ReleaseId { get; set; } = null;

        /// <summary>
        /// Linked mission filter.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Linked voyage filter.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Linked check-run filter.
        /// </summary>
        public string? CheckRunId { get; set; } = null;

        /// <summary>
        /// Status filter.
        /// </summary>
        public DeploymentStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Verification-status filter.
        /// </summary>
        public DeploymentVerificationStatusEnum? VerificationStatus { get; set; } = null;

        /// <summary>
        /// Free-text search.
        /// </summary>
        public string? Search { get; set; } = null;

        /// <summary>
        /// Lower-bound timestamp filter.
        /// </summary>
        public DateTime? FromUtc { get; set; } = null;

        /// <summary>
        /// Upper-bound timestamp filter.
        /// </summary>
        public DateTime? ToUtc { get; set; } = null;

        /// <summary>
        /// Page number.
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Page size.
        /// </summary>
        public int PageSize { get; set; } = 25;

        /// <summary>
        /// Zero-based offset.
        /// </summary>
        public int Offset => PageNumber <= 1 ? 0 : (PageNumber - 1) * PageSize;
    }
}
