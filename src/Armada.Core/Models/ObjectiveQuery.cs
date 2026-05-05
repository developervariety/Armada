namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Query for objective enumeration.
    /// </summary>
    public class ObjectiveQuery
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
        /// Optional owner filter.
        /// </summary>
        public string? Owner { get; set; } = null;

        /// <summary>
        /// Optional vessel filter.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Optional fleet filter.
        /// </summary>
        public string? FleetId { get; set; } = null;

        /// <summary>
        /// Optional planning-session filter.
        /// </summary>
        public string? PlanningSessionId { get; set; } = null;

        /// <summary>
        /// Optional voyage filter.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Optional mission filter.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Optional check-run filter.
        /// </summary>
        public string? CheckRunId { get; set; } = null;

        /// <summary>
        /// Optional release filter.
        /// </summary>
        public string? ReleaseId { get; set; } = null;

        /// <summary>
        /// Optional deployment filter.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Optional incident filter.
        /// </summary>
        public string? IncidentId { get; set; } = null;

        /// <summary>
        /// Optional tag filter.
        /// </summary>
        public string? Tag { get; set; } = null;

        /// <summary>
        /// Optional state filter.
        /// </summary>
        public ObjectiveStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Optional free-text search.
        /// </summary>
        public string? Search { get; set; } = null;

        /// <summary>
        /// Optional lower-bound UTC timestamp.
        /// </summary>
        public DateTime? FromUtc { get; set; } = null;

        /// <summary>
        /// Optional upper-bound UTC timestamp.
        /// </summary>
        public DateTime? ToUtc { get; set; } = null;

        /// <summary>
        /// Page number.
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Page size.
        /// </summary>
        public int PageSize { get; set; } = 50;

        /// <summary>
        /// Zero-based offset.
        /// </summary>
        public int Offset => PageNumber <= 1 ? 0 : (PageNumber - 1) * PageSize;
    }
}
