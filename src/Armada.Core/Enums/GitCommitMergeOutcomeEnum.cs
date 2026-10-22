namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// What happened when a commit was merged into a worktree's current branch.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GitCommitMergeOutcomeEnum
    {
        /// <summary>
        /// The commit was merged with a merge commit.
        /// </summary>
        Merged,

        /// <summary>
        /// The branch already contained the commit; nothing changed.
        /// </summary>
        AlreadyContained,

        /// <summary>
        /// The merge conflicted and was aborted, leaving the worktree as it was.
        /// </summary>
        Conflicted,

        /// <summary>
        /// The commit is not in the repository and could not be fetched.
        /// </summary>
        Missing,

        /// <summary>
        /// The merge could not run for another reason; the worktree was left as it was.
        /// </summary>
        Failed
    }
}
