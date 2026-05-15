namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Objective priority band.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectivePriorityEnum
    {
        /// <summary>
        /// Represents the p0 priority band.
        /// </summary>
        [EnumMember(Value = "P0")]
        P0,
        /// <summary>
        /// Represents the p1 priority band.
        /// </summary>
        [EnumMember(Value = "P1")]
        P1,
        /// <summary>
        /// Represents the p2 priority band.
        /// </summary>
        [EnumMember(Value = "P2")]
        P2,
        /// <summary>
        /// Represents the p3 priority band.
        /// </summary>
        [EnumMember(Value = "P3")]
        P3
    }
}
