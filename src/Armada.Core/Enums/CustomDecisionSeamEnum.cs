namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The conservative action a custom decision may take when it gates at or above its threshold.
    /// The list is fixed in code, and every member is non-approving by construction: a custom
    /// decision can flag, annotate, or record, and nothing here lands, dispatches, approves a PASS,
    /// or writes memory. There is deliberately no member that takes an irreversible or approving
    /// action; adding one would change the owner's non-negotiables and needs a new ruling.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CustomDecisionSeamEnum
    {
        /// <summary>
        /// No binding. The decision only records its answer and never flags, whatever its mode or
        /// confidence. This is the default and the safest.
        /// </summary>
        [EnumMember(Value = "None")]
        None,

        /// <summary>
        /// Requires the MissionDiff surface. When the decision gates at or above its threshold, the
        /// call is recorded as a <c>typed_decision.gated</c> event and the caller is told it flagged.
        /// Takes no other action.
        /// </summary>
        [EnumMember(Value = "MissionDiffFlag")]
        MissionDiffFlag
    }
}
