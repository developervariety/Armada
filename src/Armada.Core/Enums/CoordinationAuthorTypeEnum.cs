namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The kind of participant that authored a coordination message.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CoordinationAuthorTypeEnum
    {
        /// <summary>
        /// A human or orchestrator session driving the admiral through MCP, REST, or the dashboard.
        /// </summary>
        [EnumMember(Value = "Operator")]
        Operator,

        /// <summary>
        /// A captain (AI coding agent).
        /// </summary>
        [EnumMember(Value = "Captain")]
        Captain,

        /// <summary>
        /// The admiral itself, mirroring fleet activity into the room.
        /// </summary>
        [EnumMember(Value = "System")]
        System
    }
}
