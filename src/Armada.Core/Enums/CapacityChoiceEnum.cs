namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The persona model list Smart Routing tries first for a mission.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CapacityChoiceEnum
    {
        /// <summary>The persona's default models.</summary>
        [EnumMember(Value = "Default")]
        Default,

        /// <summary>The persona's lighter models, for routine, well-specified, mechanical work.</summary>
        [EnumMember(Value = "Lighter")]
        Lighter,

        /// <summary>The persona's stronger models, for work harder than the default is expected to handle.</summary>
        [EnumMember(Value = "Stronger")]
        Stronger
    }
}
