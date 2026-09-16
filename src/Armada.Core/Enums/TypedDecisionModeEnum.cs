namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Operating mode for the typed-decision client (TypeSafe Jev). Controls whether a wired
    /// decision point may consult the model and whether its answer may change the outcome. Ordered
    /// Off &lt; Shadow &lt; Gate so the effective mode of a decision is the minimum of the global
    /// mode and the decision's own mode. Operationally off until the key is confirmed in the
    /// container: without the key the null client is used regardless of mode.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TypedDecisionModeEnum
    {
        /// <summary>
        /// The typed-decision client is not consulted. Decision points behave exactly as they do
        /// without the feature.
        /// </summary>
        [EnumMember(Value = "Off")]
        Off,

        /// <summary>
        /// The model is consulted and its answer is recorded, but the deterministic rule stands.
        /// Also the demotion target for a decision whose gated outcomes operators reverse too often.
        /// Every Shadow-mode call emits a <c>typed_decision.shadow</c> event.
        /// </summary>
        [EnumMember(Value = "Shadow")]
        Shadow,

        /// <summary>
        /// The model may gate a decision at or above the configured confidence threshold. The gate
        /// only ever makes recovery more conservative (for example Rescue to Blocked); it never
        /// converts a rule hard-block into a rescue. A gate at or above threshold emits a loud
        /// <c>typed_decision.gated</c> event; a call below threshold records
        /// <c>typed_decision.shadow</c> and leaves the rule in place.
        /// </summary>
        [EnumMember(Value = "Gate")]
        Gate
    }
}
