namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Query for Armada historical timeline aggregation.
    /// </summary>
    public class HistoricalTimelineQuery
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
        /// Optional objective filter.
        /// </summary>
        public string? ObjectiveId { get; set; } = null;

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
        /// Optional incident filter.
        /// </summary>
        public string? IncidentId { get; set; } = null;

        /// <summary>
        /// Optional filter that narrows results to incidents with postmortem data
        /// and the current Armada-side lifecycle entries directly linked to them.
        /// </summary>
        public bool PostmortemOnly { get; set; } = false;

        /// <summary>
        /// Optional mission filter.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Optional voyage filter.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Optional principal or actor text filter.
        /// </summary>
        public string? Actor { get; set; } = null;

        /// <summary>
        /// Optional free-text search across titles and descriptions.
        /// </summary>
        public string? Text { get; set; } = null;

        /// <summary>
        /// Optional source-type filters.
        /// </summary>
        public List<string> SourceTypes { get; set; } = new List<string>();

        /// <summary>
        /// Optional lower-bound time filter.
        /// </summary>
        public DateTime? FromUtc { get; set; } = null;

        /// <summary>
        /// Optional upper-bound time filter.
        /// </summary>
        public DateTime? ToUtc { get; set; } = null;

        /// <summary>
        /// When true, request-history entries for the read-only methods (GET, HEAD and OPTIONS) are left out. The
        /// request source applies it in the database, so the bounded newest-first read of requests holds only the
        /// remaining entries.
        /// </summary>
        public bool ExcludeReadRequests { get; set; } = false;

        /// <summary>
        /// Order of the entries by time of occurrence. Entries with the same time keep a fixed order by source
        /// identifier, so a page boundary does not move between requests.
        /// </summary>
        public EnumerationOrderEnum Order { get; set; } = EnumerationOrderEnum.CreatedDescending;

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
