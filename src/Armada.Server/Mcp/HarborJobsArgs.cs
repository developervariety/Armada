namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments for list_harbor_jobs and stop_harbor_job.
    /// </summary>
    public class HarborJobsArgs
    {
        #region Public-Members

        /// <summary>
        /// Harbor identifier (hbr_ prefix).
        /// </summary>
        public string HarborId { get; set; } = string.Empty;

        /// <summary>
        /// Job identifier, for stop_harbor_job.
        /// </summary>
        public string? JobId { get; set; } = null;

        /// <summary>
        /// Only jobs that have not ended, for list_harbor_jobs.
        /// </summary>
        public bool? OpenOnly { get; set; } = null;

        /// <summary>
        /// Page number (1-based).
        /// </summary>
        public int? PageNumber { get; set; } = null;

        /// <summary>
        /// Results per page.
        /// </summary>
        public int? PageSize { get; set; } = null;

        #endregion
    }
}
