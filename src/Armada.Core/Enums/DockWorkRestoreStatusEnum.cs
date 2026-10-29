namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of applying a mission's saved dock work to a checkout.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DockWorkRestoreStatusEnum
    {
        /// <summary>
        /// The mission has no saved dock work, or this git service cannot apply any.
        /// </summary>
        [EnumMember(Value = "NothingSaved")]
        NothingSaved,

        /// <summary>
        /// The saved work is now in the checkout as uncommitted changes.
        /// </summary>
        [EnumMember(Value = "Applied")]
        Applied,

        /// <summary>
        /// Saved work exists but was not applied; the message names why and the checkout is unchanged.
        /// </summary>
        [EnumMember(Value = "Skipped")]
        Skipped
    }
}
