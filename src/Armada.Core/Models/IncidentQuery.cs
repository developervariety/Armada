namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Incident enumeration filters.
    /// </summary>
    public class IncidentQuery
    {
        /// <summary>
        /// Optional tenant filter.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Optional user filter.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Optional vessel filter.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Optional environment filter.
        /// </summary>
        public string? EnvironmentId { get; set; } = null;

        /// <summary>
        /// Optional deployment filter.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Optional release filter.
        /// </summary>
        public string? ReleaseId { get; set; } = null;

        /// <summary>
        /// Optional mission filter.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Optional voyage filter.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Optional status filter.
        /// </summary>
        public IncidentStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Optional severity filter.
        /// </summary>
        public IncidentSeverityEnum? Severity { get; set; } = null;

        /// <summary>
        /// Optional free-text filter.
        /// </summary>
        public string? Search { get; set; } = null;

        /// <summary>
        /// One-based page number.
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Page size.
        /// </summary>
        public int PageSize { get; set; } = 50;
    }
}
