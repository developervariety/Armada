namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What a request to stop an agent process did.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AgentStopOutcomeEnum
    {
        /// <summary>The process exited within the grace period or was killed.</summary>
        [EnumMember(Value = "Stopped")]
        Stopped,

        /// <summary>No process was running under the identifier, so there was nothing to stop.</summary>
        [EnumMember(Value = "NotRunning")]
        NotRunning,

        /// <summary>
        /// The stop was refused and the process may still be running: it could not be verified as the launched agent,
        /// or the kill failed. The result names the reason.
        /// </summary>
        [EnumMember(Value = "Refused")]
        Refused
    }
}
