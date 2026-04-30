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
        /// Tests passed, ready to land.
        /// </summary>
        Passed,

        /// <summary>
        /// Tests failed.
        /// </summary>
        Failed,

        /// <summary>
        /// Successfully merged into the target branch.
        /// </summary>
        Landed,

        /// <summary>
        /// PR-fallback path: tests passed but a critical-risk trigger fired (UDS 0x34
        /// guard, RSA primitive, etc.), so the entry was routed to a real platform
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
