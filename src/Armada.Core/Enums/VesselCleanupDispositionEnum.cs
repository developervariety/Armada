namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How a vessel cleanup inventory classifies a resource.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VesselCleanupDispositionEnum
    {
        /// <summary>
        /// Evidence proves the resource is not needed by any work; it may be garbage collected.
        /// </summary>
        [EnumMember(Value = "Safe")]
        Safe,

        /// <summary>
        /// Running, queued, review, landing, retry, captain, or merge-queue work still references the resource.
        /// </summary>
        [EnumMember(Value = "InUse")]
        InUse,

        /// <summary>
        /// The resource could not be proven safe (dirty, unmerged, shared, unreachable, or ambiguous ownership).
        /// </summary>
        [EnumMember(Value = "Unknown")]
        Unknown
    }
}
