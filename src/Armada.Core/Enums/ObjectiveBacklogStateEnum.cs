namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Pre-dispatch backlog maturity state.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ObjectiveBacklogStateEnum
    {
        [EnumMember(Value = "Inbox")]
        Inbox,
        [EnumMember(Value = "Triaged")]
        Triaged,
        [EnumMember(Value = "Refining")]
        Refining,
        [EnumMember(Value = "ReadyForPlanning")]
        ReadyForPlanning,
        [EnumMember(Value = "ReadyForDispatch")]
        ReadyForDispatch,
        [EnumMember(Value = "Dispatched")]
        Dispatched
    }
}
