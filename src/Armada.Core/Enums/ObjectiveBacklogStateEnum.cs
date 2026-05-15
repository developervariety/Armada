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
        /// <summary>
        /// Represents the inbox backlog state.
        /// </summary>
        [EnumMember(Value = "Inbox")]
        Inbox,
        /// <summary>
        /// Represents the triaged backlog state.
        /// </summary>
        [EnumMember(Value = "Triaged")]
        Triaged,
        /// <summary>
        /// Represents the refining backlog state.
        /// </summary>
        [EnumMember(Value = "Refining")]
        Refining,
        /// <summary>
        /// Represents the ready for planning backlog state.
        /// </summary>
        [EnumMember(Value = "ReadyForPlanning")]
        ReadyForPlanning,
        /// <summary>
        /// Represents the ready for dispatch backlog state.
        /// </summary>
        [EnumMember(Value = "ReadyForDispatch")]
        ReadyForDispatch,
        /// <summary>
        /// Represents the dispatched backlog state.
        /// </summary>
        [EnumMember(Value = "Dispatched")]
        Dispatched
    }
}
