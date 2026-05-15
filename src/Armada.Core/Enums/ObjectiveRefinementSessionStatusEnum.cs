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
        /// <summary>
        /// Represents the created refinement session status.
        /// </summary>
        [EnumMember(Value = "Created")]
        Created,
        /// <summary>
        /// Represents the active refinement session status.
        /// </summary>
        [EnumMember(Value = "Active")]
        Active,
        /// <summary>
        /// Represents the responding refinement session status.
        /// </summary>
        [EnumMember(Value = "Responding")]
        Responding,
        /// <summary>
        /// Represents the stopping refinement session status.
        /// </summary>
        [EnumMember(Value = "Stopping")]
        Stopping,
        /// <summary>
        /// Represents the stopped refinement session status.
        /// </summary>
        [EnumMember(Value = "Stopped")]
        Stopped,
        /// <summary>
        /// Represents the completed refinement session status.
        /// </summary>
        [EnumMember(Value = "Completed")]
        Completed,
        /// <summary>
        /// Represents the failed refinement session status.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed
    }
}
