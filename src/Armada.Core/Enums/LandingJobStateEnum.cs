namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Durable state for a landing job.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LandingJobStateEnum
    {
        /// <summary>
        /// Queued for landing.
        /// </summary>
        Queued,

        /// <summary>
        /// Preparing an integration worktree.
        /// </summary>
        Rebasing,

        /// <summary>
        /// Merging the captain branch.
        /// </summary>
        Merging,

        /// <summary>
        /// Running validation tests.
        /// </summary>
        Testing,

        /// <summary>
        /// Validation passed.
        /// </summary>
        Passed,

        /// <summary>
        /// Pushing the integration branch.
        /// </summary>
        Pushing,

        /// <summary>
        /// Creating a pull request fallback.
        /// </summary>
        CreatingPR,

        /// <summary>
        /// Landing completed.
        /// </summary>
        Landed,

        /// <summary>
        /// Landing failed.
        /// </summary>
        Failed,

        /// <summary>
        /// Pull request fallback is open.
        /// </summary>
        PullRequestOpen,

        /// <summary>
        /// Landing was cancelled.
        /// </summary>
        Cancelled
    }
}
