namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Lifecycle state for a backlog refinement session.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectiveRefinementSessionStatusEnum
    {
        [EnumMember(Value = "Created")]
        Created,
        [EnumMember(Value = "Active")]
        Active,
        [EnumMember(Value = "Responding")]
        Responding,
        [EnumMember(Value = "Stopping")]
        Stopping,
        [EnumMember(Value = "Stopped")]
        Stopped,
        [EnumMember(Value = "Completed")]
        Completed,
        [EnumMember(Value = "Failed")]
        Failed
    }
}
