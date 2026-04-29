namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Status of a planning session.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PlanningSessionStatusEnum
    {
        /// <summary>
        /// Session record created but not yet fully provisioned.
        /// </summary>
        [EnumMember(Value = "Created")]
        Created,

        /// <summary>
        /// Session is active and ready for the next user turn.
        /// </summary>
        [EnumMember(Value = "Active")]
        Active,

        /// <summary>
        /// Armada is currently generating a response for the session.
        /// </summary>
        [EnumMember(Value = "Responding")]
        Responding,

        /// <summary>
        /// Session is stopping and cleaning up resources.
        /// </summary>
        [EnumMember(Value = "Stopping")]
        Stopping,

        /// <summary>
        /// Session has been stopped.
        /// </summary>
        [EnumMember(Value = "Stopped")]
        Stopped,

        /// <summary>
        /// Session failed.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed
    }
}
