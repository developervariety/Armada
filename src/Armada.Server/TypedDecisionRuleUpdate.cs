namespace Armada.Server
{
    using Armada.Core.Enums;

    /// <summary>A partial update of one decision.</summary>
    public sealed class TypedDecisionRuleUpdate
    {
        /// <summary>New mode, or null to keep it.</summary>
        public TypedDecisionModeEnum? Mode { get; set; }

        /// <summary>New gate threshold from 0 to 1, or null to keep it.</summary>
        public double? GateThreshold { get; set; }
    }
}
