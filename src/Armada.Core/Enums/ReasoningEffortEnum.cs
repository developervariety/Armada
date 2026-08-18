namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reasoning-effort level for a captain. Translated to each runtime's native control at launch
    /// (Claude Code thinking budget, Codex model_reasoning_effort, Mux --effort). Runtimes without a
    /// native control ignore it. Null on a captain means "use the runtime default".
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ReasoningEffortEnum
    {
        /// <summary>
        /// No reasoning effort; disable the reasoning field where the runtime allows it.
        /// </summary>
        [EnumMember(Value = "Off")]
        Off,

        /// <summary>
        /// Minimal reasoning effort.
        /// </summary>
        [EnumMember(Value = "Minimal")]
        Minimal,

        /// <summary>
        /// Low reasoning effort.
        /// </summary>
        [EnumMember(Value = "Low")]
        Low,

        /// <summary>
        /// Medium reasoning effort.
        /// </summary>
        [EnumMember(Value = "Medium")]
        Medium,

        /// <summary>
        /// High reasoning effort.
        /// </summary>
        [EnumMember(Value = "High")]
        High
    }
}
