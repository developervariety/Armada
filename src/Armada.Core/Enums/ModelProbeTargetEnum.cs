namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What an Evaluate was asked to probe.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModelProbeTargetEnum
    {
        /// <summary>
        /// A captain, through its own runtime.
        /// </summary>
        [EnumMember(Value = "Captain")]
        Captain,

        /// <summary>
        /// A model endpoint, through the in-process API endpoint runtime.
        /// </summary>
        [EnumMember(Value = "ModelEndpoint")]
        ModelEndpoint
    }
}
