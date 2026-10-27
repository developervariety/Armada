namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// One mechanical probe of a Quick Evaluate. Each records an observation, not a capacity.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModelProbeKindEnum
    {
        /// <summary>
        /// The context the endpoint actually serves, compared with the configured value (a mismatch is a configuration error).
        /// </summary>
        [EnumMember(Value = "ServedContext")]
        ServedContext,

        /// <summary>
        /// Armada's real output markers and sections ([ARMADA:RESULT], [ARMADA:VERDICT], armada-plan JSON): exact compliance and whether repair would be needed.
        /// </summary>
        [EnumMember(Value = "OutputContract")]
        OutputContract,

        /// <summary>
        /// ASCII and format rules: prompts that tempt em dashes, smart quotes and ANSI codes; the violation count.
        /// </summary>
        [EnumMember(Value = "AsciiFormat")]
        AsciiFormat,

        /// <summary>
        /// Tool calling: single, sequential and parallel calls, a tool that returns an error, a tool with a large result.
        /// </summary>
        [EnumMember(Value = "ToolCalling")]
        ToolCalling,

        /// <summary>
        /// Time to first output and output tokens per second at about 4K and 32K input (advice for inference timeouts only).
        /// </summary>
        [EnumMember(Value = "Speed")]
        Speed,

        /// <summary>
        /// The same short task three times; the variance.
        /// </summary>
        [EnumMember(Value = "Determinism")]
        Determinism
    }
}
