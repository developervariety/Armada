namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How much a finding in a handoff artifact blocks the work.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HandoffFindingSeverityEnum
    {
        /// <summary>
        /// The finding must be resolved before the work can move on (a blocking verdict or a verifier block).
        /// </summary>
        [EnumMember(Value = "Blocking")]
        Blocking,

        /// <summary>
        /// The finding was labelled optional or non-blocking.
        /// </summary>
        [EnumMember(Value = "NonBlocking")]
        NonBlocking,

        /// <summary>
        /// The stage did not say how much the finding matters.
        /// </summary>
        [EnumMember(Value = "Unknown")]
        Unknown
    }
}
