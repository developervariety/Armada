namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Query parameters for structured check-run APIs and storage.
    /// </summary>
    public class CheckRunQuery
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
        /// Workflow profile filter.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Vessel filter.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Mission filter.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Voyage filter.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Type filter.
        /// </summary>
        public CheckRunTypeEnum? Type { get; set; } = null;

        /// <summary>
        /// Status filter.
        /// </summary>
        public CheckRunStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Environment-name filter.
        /// </summary>
        public string? EnvironmentName { get; set; } = null;

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
