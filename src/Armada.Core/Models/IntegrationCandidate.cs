namespace Armada.Core.Models
{
    /// <summary>
    /// The exact commits one landing attempt intends to publish: the source branch tip, the target
    /// commit the merge was built on, and the merge result itself.
    /// </summary>
    public class IntegrationCandidate
    {
        #region Public-Members

        /// <summary>
        /// Vessel whose target branch the candidate would advance.
        /// </summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>
        /// Mission whose work the candidate carries, when known.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Merge queue entry that built the candidate, when the merge queue is the landing path.
        /// </summary>
        public string? MergeEntryId { get; set; } = null;

        /// <summary>
        /// Landing path that built the candidate: <c>LocalMerge</c> for a direct integration-worktree
        /// landing, <c>MergeQueue</c> for a queue landing.
        /// </summary>
        public string LandingPath { get; set; } = String.Empty;

        /// <summary>
        /// Target branch the candidate would advance.
        /// </summary>
        public string TargetBranch { get; set; } = String.Empty;

        /// <summary>
        /// Source branch merged into the target.
        /// </summary>
        public string? SourceBranch { get; set; } = null;

        /// <summary>
        /// Commit at the tip of the source branch when it was merged, when it could be resolved.
        /// </summary>
        public string? SourceCommit { get; set; } = null;

        /// <summary>
        /// Target commit the candidate was built on.
        /// </summary>
        public string? TargetBaseCommit { get; set; } = null;

        /// <summary>
        /// The merge result: the commit that would become the new target tip.
        /// </summary>
        public string IntegrationCommit { get; set; } = String.Empty;

        /// <summary>
        /// Tenant of the landing, used for the evidence event's owner scope.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user of the landing, used for the evidence event's owner scope.
        /// </summary>
        public string? UserId { get; set; } = null;

        #endregion
    }
}
