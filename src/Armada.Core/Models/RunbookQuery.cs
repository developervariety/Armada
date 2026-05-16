namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Runbook enumeration filters.
    /// </summary>
    public class RunbookQuery
    {
        /// <summary>
        /// Optional workflow-profile filter.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Optional environment filter.
        /// </summary>
        public string? EnvironmentId { get; set; } = null;

        /// <summary>
        /// Optional suggested default check type filter.
        /// </summary>
        public CheckRunTypeEnum? DefaultCheckType { get; set; } = null;

        /// <summary>
        /// Optional active-state filter.
        /// </summary>
        public bool? Active { get; set; } = null;

        /// <summary>
        /// Optional search filter.
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
