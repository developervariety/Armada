namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// What proves that a mission's work is on its vessel's remote default branch.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LandingEvidenceKindEnum
    {
        /// <summary>The mission's recorded commit is an ancestor of the default branch.</summary>
        CommitAncestry = 0,

        /// <summary>A commit on the default branch carries the mission's exact Armada-Mission-Id trailer.</summary>
        MissionTrailer = 1
    }
}
