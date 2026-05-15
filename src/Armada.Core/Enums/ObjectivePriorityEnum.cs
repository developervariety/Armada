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
        [EnumMember(Value = "P0")]
        P0,
        [EnumMember(Value = "P1")]
        P1,
        [EnumMember(Value = "P2")]
        P2,
        [EnumMember(Value = "P3")]
        P3
    }
}
