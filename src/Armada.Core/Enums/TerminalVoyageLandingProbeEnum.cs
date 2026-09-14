namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What the vessel repository says about a mission's work when its voyage has ended.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TerminalVoyageLandingProbeEnum
    {
        /// <summary>
        /// The mission commit is an ancestor of the vessel default branch, or a merge entry recorded the landing.
        /// </summary>
        [EnumMember(Value = "Landed")]
        Landed,

        /// <summary>
        /// The mission commit exists in the vessel repository and is not an ancestor of the default branch.
        /// </summary>
        [EnumMember(Value = "NotLanded")]
        NotLanded,

        /// <summary>
        /// The default branch resolves but the mission commit is absent from the vessel repository,
        /// so it cannot be on the default branch.
        /// </summary>
        [EnumMember(Value = "CommitAbsent")]
        CommitAbsent,

        /// <summary>
        /// The mission recorded no commit.
        /// </summary>
        [EnumMember(Value = "NoCommit")]
        NoCommit,

        /// <summary>
        /// Ancestry could not be established: no vessel, no repository, no default branch, or a failed probe.
        /// </summary>
        [EnumMember(Value = "Unknown")]
        Unknown
    }
}
