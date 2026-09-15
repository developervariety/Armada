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
        /// Optional check-run filter.
        /// </summary>
        public string? CheckRunId { get; set; } = null;

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
        /// When true, exclude terminal incidents (Closed and RolledBack).
        /// </summary>
        public bool ExcludeTerminal { get; set; } = false;

        /// <summary>
        /// When true, order by oldest LastUpdateUtc first (then by identifier) instead of newest first.
        /// </summary>
        public bool OldestFirst { get; set; } = false;

        /// <summary>
        /// Optional keyset cursor for oldest-first enumeration: return only incidents whose
        /// LastUpdateUtc is later than this value, or equal to it with an identifier ordinally
        /// greater than <see cref="AfterId"/>. Applies only when <see cref="OldestFirst"/> is true.
        /// </summary>
        public DateTime? AfterLastUpdateUtc { get; set; } = null;

        /// <summary>
        /// Identifier tie-breaker for <see cref="AfterLastUpdateUtc"/>.
        /// </summary>
        public string? AfterId { get; set; } = null;

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
