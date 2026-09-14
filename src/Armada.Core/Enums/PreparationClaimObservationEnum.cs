namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// What happened to one preparation claim at one point in time.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PreparationClaimObservationEnum
    {
        /// <summary>A claim, or a changed version of it, was verified for the first time.</summary>
        Established = 0,

        /// <summary>
        /// The same claim was verified again with unchanged statement, evidence, and anchors. This is
        /// repeated research.
        /// </summary>
        Reestablished = 1,

        /// <summary>A claim that needed a recheck, or whose anchors moved, was verified again.</summary>
        Revalidated = 2,

        /// <summary>A verified claim was delivered to dispatched work without new research.</summary>
        Reused = 3
    }
}
