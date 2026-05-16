namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Runbook execution enumeration filters.
    /// </summary>
    public class RunbookExecutionQuery
    {
        /// <summary>
        /// Optional runbook filter.
        /// </summary>
        public string? RunbookId { get; set; } = null;

        /// <summary>
        /// Optional deployment filter.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Optional incident filter.
        /// </summary>
        public string? IncidentId { get; set; } = null;

        /// <summary>
        /// Optional status filter.
        /// </summary>
        public RunbookExecutionStatusEnum? Status { get; set; } = null;

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
