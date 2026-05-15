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
        [EnumMember(Value = "XS")]
        XS,
        [EnumMember(Value = "S")]
        S,
        [EnumMember(Value = "M")]
        M,
        [EnumMember(Value = "L")]
        L,
        [EnumMember(Value = "XL")]
        XL
    }
}
