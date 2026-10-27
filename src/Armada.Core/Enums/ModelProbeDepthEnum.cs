namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How thorough an Evaluate is.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModelProbeDepthEnum
    {
        /// <summary>
        /// Mechanical probes only (the default).
        /// </summary>
        [EnumMember(Value = "Quick")]
        Quick,

        /// <summary>
        /// Adds the fixture battery and the evaluator's grading (not implemented until Phase 5).
        /// </summary>
        [EnumMember(Value = "Full")]
        Full
    }
}
