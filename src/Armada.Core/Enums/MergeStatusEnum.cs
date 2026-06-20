namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Status of a merge queue entry.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MergeStatusEnum
    {
        /// <summary>
        /// Queued for merge, waiting to be picked up.
        /// </summary>
        Queued,

        /// <summary>
        /// Currently being tested (merged into integration branch, tests running).
        /// </summary>
        Testing,

        /// <summary>
        /// Preparing the entry against the current target branch before merge.
        /// </summary>
        Rebasing,

        /// <summary>
        /// Merging the captain branch into the durable integration worktree.
        /// </summary>
        Merging,

        /// <summary>
        /// Tests passed, ready to land.
        /// </summary>
        Passed,

        /// <summary>
        /// Pushing the integration branch to the target branch.
        /// </summary>
        Pushing,

        /// <summary>
        /// Creating a platform pull request instead of pushing directly.
        /// </summary>
        CreatingPR,

        /// <summary>
        /// Tests failed.
        /// </summary>
        Failed,

        /// <summary>
        /// Successfully merged into the target branch.
        /// </summary>
        Landed,

        /// <summary>
        /// PR-fallback path: tests passed but a critical-risk trigger fired (a guarded
        /// high-risk operation, RSA primitive, etc.), so the entry was routed to a real platform
        /// pull request instead of an automatic land. The entry stays in this state
        /// until the PR merges (transitions to Landed) or the orchestrator abandons
        /// it (transitions to Cancelled).
        /// </summary>
        PullRequestOpen,

        /// <summary>
        /// Removed from the queue (manually or due to conflict).
        /// </summary>
        Cancelled
    }
}
