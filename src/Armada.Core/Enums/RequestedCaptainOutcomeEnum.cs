namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How assignment applied a mission's requested captain and stored fallback tier.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RequestedCaptainOutcomeEnum
    {
        /// <summary>
        /// The mission stores neither a requested captain nor a fallback tier; normal routing applies unchanged.
        /// </summary>
        [EnumMember(Value = "NotRequested")]
        NotRequested,

        /// <summary>
        /// The requested captain is idle and passed every assignment gate, so it is assigned.
        /// </summary>
        [EnumMember(Value = "AssignRequested")]
        AssignRequested,

        /// <summary>
        /// The requested captain cannot take the mission now, or only a tier is stored; normal routing
        /// runs over captains at or above the fallback tier.
        /// </summary>
        [EnumMember(Value = "FallbackByTier")]
        FallbackByTier,

        /// <summary>
        /// No assignable idle captain is at or above the fallback tier; the mission waits.
        /// </summary>
        [EnumMember(Value = "WaitForTier")]
        WaitForTier,

        /// <summary>
        /// The requested captain no longer exists and no tier is stored; normal routing applies.
        /// </summary>
        [EnumMember(Value = "RequestedNotFound")]
        RequestedNotFound
    }
}
