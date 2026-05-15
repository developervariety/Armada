namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Result payload returned after importing GitHub Actions runs.
    /// </summary>
    public class GitHubActionsSyncResult
    {
        /// <summary>
        /// GitHub provider name used for imported runs.
        /// </summary>
        public string ProviderName { get; set; } = "GitHubActions";

        /// <summary>
        /// Vessel identifier used for the sync.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Deployment identifier linked during sync when supplied.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Number of imported runs that were newly created.
        /// </summary>
        public int CreatedCount { get; set; } = 0;

        /// <summary>
        /// Number of imported runs that updated existing Armada records.
        /// </summary>
        public int UpdatedCount { get; set; } = 0;

        /// <summary>
        /// Imported or refreshed check runs.
        /// </summary>
        public List<CheckRun> CheckRuns { get; set; } = new List<CheckRun>();
    }
}
