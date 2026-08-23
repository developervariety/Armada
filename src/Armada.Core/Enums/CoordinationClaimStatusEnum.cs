namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Lifecycle status of a coordination claim.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CoordinationClaimStatusEnum
    {
        /// <summary>
        /// The claim is held and unexpired.
        /// </summary>
        [EnumMember(Value = "Active")]
        Active,

        /// <summary>
        /// The holder released the claim, or the work finished.
        /// </summary>
        [EnumMember(Value = "Released")]
        Released
    }
}
