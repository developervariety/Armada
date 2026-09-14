namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What happens to a mission after its captain refused it.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PolicyRefusalContinuationOutcomeEnum
    {
        /// <summary>
        /// The refusal does not conflict with a supplied owner policy (none was recorded), so normal
        /// completion handling applies.
        /// </summary>
        [EnumMember(Value = "NotApplicable")]
        NotApplicable,

        /// <summary>The mission is requeued once for an approved captain on a different runtime.</summary>
        [EnumMember(Value = "Continue")]
        Continue,

        /// <summary>
        /// The mission fails with the refusal recorded: its one continuation is spent, or no approved
        /// alternate runtime can run it. It is never retried on the blocked path.
        /// </summary>
        [EnumMember(Value = "Stop")]
        Stop
    }
}
