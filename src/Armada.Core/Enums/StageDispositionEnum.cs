namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What Armada did with a stage attempt when its agent exited.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum StageDispositionEnum
    {
        /// <summary>
        /// The stage finished and moved on (work produced, landed or handed to the next stage).
        /// </summary>
        [EnumMember(Value = "Completed")]
        Completed,

        /// <summary>
        /// The same captain was relaunched in the same dock (a failed or unrunnable stage check, a transient runtime error).
        /// </summary>
        [EnumMember(Value = "Relaunched")]
        Relaunched,

        /// <summary>
        /// The stage was rerun to repair its output contract.
        /// </summary>
        [EnumMember(Value = "OutputRepair")]
        OutputRepair,

        /// <summary>
        /// The stage was handed to a different captain.
        /// </summary>
        [EnumMember(Value = "Escalated")]
        Escalated,

        /// <summary>
        /// The mission failed.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed,

        /// <summary>
        /// The agent was interrupted (exit -1) and the mission re-dispatched.
        /// </summary>
        [EnumMember(Value = "Interrupted")]
        Interrupted,

        /// <summary>
        /// The mission was cancelled.
        /// </summary>
        [EnumMember(Value = "Cancelled")]
        Cancelled,

        /// <summary>
        /// The attempt's result is not settled yet (the mission is still being handled).
        /// </summary>
        [EnumMember(Value = "Pending")]
        Pending
    }
}
