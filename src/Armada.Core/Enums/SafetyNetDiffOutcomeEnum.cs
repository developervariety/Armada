namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of loading the diff the landing-drain safety net measures a branch with.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SafetyNetDiffOutcomeEnum
    {
        /// <summary>
        /// The diff was read from the mission's dock worktree.
        /// </summary>
        [EnumMember(Value = "Loaded")]
        Loaded,

        /// <summary>
        /// The mission has no dock worktree, so the branch cannot be measured. The vessel's default-branch
        /// checkout is never used instead: it always diffs empty.
        /// </summary>
        [EnumMember(Value = "NoDock")]
        NoDock,

        /// <summary>
        /// No git service is configured.
        /// </summary>
        [EnumMember(Value = "GitUnavailable")]
        GitUnavailable,

        /// <summary>
        /// Git failed while reading the diff.
        /// </summary>
        [EnumMember(Value = "DiffFailed")]
        DiffFailed
    }
}
