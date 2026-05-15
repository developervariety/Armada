namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Relative effort estimate for a backlog item.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectiveEffortEnum
    {
        /// <summary>
        /// Represents the xs effort estimate.
        /// </summary>
        [EnumMember(Value = "XS")]
        XS,
        /// <summary>
        /// Represents the s effort estimate.
        /// </summary>
        [EnumMember(Value = "S")]
        S,
        /// <summary>
        /// Represents the m effort estimate.
        /// </summary>
        [EnumMember(Value = "M")]
        M,
        /// <summary>
        /// Represents the l effort estimate.
        /// </summary>
        [EnumMember(Value = "L")]
        L,
        /// <summary>
        /// Represents the xl effort estimate.
        /// </summary>
        [EnumMember(Value = "XL")]
        XL
    }
}
