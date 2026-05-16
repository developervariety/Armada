namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Request payload for importing recent GitHub Actions workflow runs into Armada check history.
    /// </summary>
    public class GitHubActionsSyncRequest
    {
        /// <summary>
        /// Vessel whose GitHub repository should be queried.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Optional workflow-profile link to apply to imported runs.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Optional deployment link to apply to imported runs.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Optional environment name to stamp onto imported runs.
        /// </summary>
        public string? EnvironmentName { get; set; } = null;

        /// <summary>
        /// Optional branch filter.
        /// </summary>
        public string? BranchName { get; set; } = null;

        /// <summary>
        /// Optional commit filter.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// Optional workflow-name filter applied after retrieval.
        /// </summary>
        public string? WorkflowName { get; set; } = null;

        /// <summary>
        /// Optional GitHub run-status filter.
        /// </summary>
        public string? RunStatus { get; set; } = null;

        /// <summary>
        /// Optional imported check-type override.
        /// </summary>
        public CheckRunTypeEnum? TypeOverride { get; set; } = null;

        /// <summary>
        /// Maximum number of runs to inspect per sync call.
        /// </summary>
        public int RunCount { get; set; } = 20;
    }
}
