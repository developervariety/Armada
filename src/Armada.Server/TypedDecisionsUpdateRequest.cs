namespace Armada.Server
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>A partial update of typed-decision modes and thresholds.</summary>
    public sealed class TypedDecisionsUpdateRequest
    {
        /// <summary>New global mode, or null to keep it.</summary>
        public TypedDecisionModeEnum? Mode { get; set; }

        /// <summary>Per-decision updates keyed by shipped decision name, or null.</summary>
        public Dictionary<string, TypedDecisionRuleUpdate>? Decisions { get; set; }
    }
}
