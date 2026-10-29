namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of cleaning one selected vessel resource.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VesselCleanupItemStatusEnum
    {
        /// <summary>
        /// The resource was removed.
        /// </summary>
        [EnumMember(Value = "Deleted")]
        Deleted,

        /// <summary>
        /// The resource was not touched because revalidation no longer proves it safe, or it no longer exists.
        /// </summary>
        [EnumMember(Value = "Skipped")]
        Skipped,

        /// <summary>
        /// Removal was attempted and did not complete.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed
    }
}
