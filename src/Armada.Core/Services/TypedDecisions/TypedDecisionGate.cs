namespace Armada.Core.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// The outcome the shared gate rule selects for one consulted decision. It names which of the four
    /// conservative paths applies; the caller maps it to its own verdict and recorded event. The model
    /// never approves, so every outcome except <see cref="Gated"/> returns the deterministic rule
    /// unchanged, and <see cref="Gated"/> only ever lets the caller make the outcome more conservative.
    /// </summary>
    public enum TypedGateOutcome
    {
        /// <summary>The decision is off: keep the rule, do not call the model, record nothing.</summary>
        Off,

        /// <summary>The model is unavailable: keep the rule and record an unavailable event.</summary>
        Unavailable,

        /// <summary>The decision is in Shadow: keep the rule and record a shadow event.</summary>
        Shadow,

        /// <summary>The decision is in Gate but the confidence is below its threshold: keep the rule and record a shadow event.</summary>
        BelowThreshold,

        /// <summary>The decision is in Gate at or above its threshold: the caller may combine the rule with the model.</summary>
        Gated
    }

    /// <summary>
    /// The one place the mode, threshold and fallback rule of a typed decision is decided. Every adapter
    /// routes its gate through <see cref="Classify"/> so the rule exists once: an adapter that copied it
    /// could drift, and a copy that answered a cell of the mode x availability x confidence matrix
    /// differently from the rest would silently either approve where the model may not or lose a gated
    /// signal. The classification is pure and total, and <see cref="TypedDecisionAdapterBase{TInput,TVerdict,TModel}"/>
    /// plus every standalone adapter call it.
    /// </summary>
    public static class TypedDecisionGate
    {
        /// <summary>
        /// Select the gate outcome for one consulted decision. The order is fixed: Off wins first (no
        /// call is ever made), then an unavailable model, then Shadow, then a Gate confidence below the
        /// threshold, and only a Gate confidence at or above the threshold gates.
        /// </summary>
        /// <param name="cfg">The effective mode and threshold for this decision.</param>
        /// <param name="available">Whether the model returned an available result.</param>
        /// <param name="confidence">The confidence of the single action the model proposes, in [0, 1].</param>
        /// <returns>The outcome the caller acts on.</returns>
        public static TypedGateOutcome Classify(ResolvedTypedDecision cfg, bool available, double confidence)
        {
            if (cfg == null || cfg.Mode == TypedDecisionModeEnum.Off) return TypedGateOutcome.Off;
            if (!available) return TypedGateOutcome.Unavailable;
            if (cfg.Mode == TypedDecisionModeEnum.Shadow) return TypedGateOutcome.Shadow;
            if (confidence < cfg.GateThreshold) return TypedGateOutcome.BelowThreshold;
            return TypedGateOutcome.Gated;
        }

        /// <summary>
        /// The recorded outcome label for a kept-rule path: <c>shadow_mode</c> for a Shadow decision and
        /// <c>below_threshold</c> for a Gate confidence under the threshold. Only meaningful for
        /// <see cref="TypedGateOutcome.Shadow"/> and <see cref="TypedGateOutcome.BelowThreshold"/>.
        /// </summary>
        /// <param name="outcome">The gate outcome.</param>
        /// <returns>The event outcome label.</returns>
        public static string ShadowOutcomeLabel(TypedGateOutcome outcome)
        {
            return outcome == TypedGateOutcome.Shadow ? "shadow_mode" : "below_threshold";
        }
    }
}
