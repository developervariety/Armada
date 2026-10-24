namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Query parameters for structured check-run APIs and storage.
    /// </summary>
    public class CheckRunQuery
    {
        #region Private-Members

        private int _PageSize = 25;

        #endregion

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
        /// Deployment filter.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Type filter.
        /// </summary>
        public CheckRunTypeEnum? Type { get; set; } = null;

        /// <summary>
        /// Status filter.
        /// </summary>
        public CheckRunStatusEnum? Status { get; set; } = null;

        /// <summary>
        /// Source filter.
        /// </summary>
        public CheckRunSourceEnum? Source { get; set; } = null;

        /// <summary>
        /// Provider-name filter.
        /// </summary>
        public string? ProviderName { get; set; } = null;

        /// <summary>
        /// External provider identifier filter.
        /// </summary>
        public string? ExternalId { get; set; } = null;

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
        /// Results per page (1 to 1000, default 25). Values outside the range are clamped, so a request can never ask for
        /// an unbounded page.
        /// </summary>
        public int PageSize
        {
            get => _PageSize;
            set => _PageSize = value < 1 ? 1 : (value > 1000 ? 1000 : value);
        }

        /// <summary>
        /// Zero-based offset.
        /// </summary>
        public int Offset => PageNumber <= 1 ? 0 : (PageNumber - 1) * PageSize;
    }
}
