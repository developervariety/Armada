namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// One deterministic root-to-terminal path through an objective dependency graph.
    /// </summary>
    public class ObjectiveDependencyChain
    {
        /// <summary>Objective identifiers from the analyzed objective to the terminal blocker.</summary>
        public List<string> ObjectiveIds { get; set; } = new List<string>();

        /// <summary>Reason the terminal node blocks the chain.</summary>
        public ObjectiveDependencyTerminalReasonEnum TerminalReason { get; set; }
    }
}
