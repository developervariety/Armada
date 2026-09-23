namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Why the admiral stopped an agent process on purpose, which decides what its exit means.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum IntentionalStopKindEnum
    {
        /// <summary>
        /// The process kept running after its terminal marker. Its exit completes the mission from the
        /// recorded output, whatever exit code the stop produced.
        /// </summary>
        [EnumMember(Value = "Completion")]
        Completion,

        /// <summary>
        /// The admiral replaced the process (a stalled captain being recovered or deferred). Its exit is
        /// not the mission's outcome: the admiral already decided what happens to the mission.
        /// </summary>
        [EnumMember(Value = "Superseded")]
        Superseded
    }
}
