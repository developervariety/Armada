namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Backlog/objective classification.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectiveKindEnum
    {
        [EnumMember(Value = "Feature")]
        Feature,
        [EnumMember(Value = "Bug")]
        Bug,
        [EnumMember(Value = "Refactor")]
        Refactor,
        [EnumMember(Value = "Research")]
        Research,
        [EnumMember(Value = "Chore")]
        Chore,
        [EnumMember(Value = "Initiative")]
        Initiative
    }
}
