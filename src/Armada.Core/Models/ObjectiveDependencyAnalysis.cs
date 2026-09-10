namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Complete dependency-readiness result for one objective.
    /// </summary>
    public class ObjectiveDependencyAnalysis
    {
        /// <summary>Analyzed objective identifier.</summary>
        public string ObjectiveId { get; set; } = string.Empty;

        /// <summary>True when every direct blocking objective is complete.</summary>
        public bool IsDependencyReady { get; set; } = true;

        /// <summary>True when a reachable blocking path contains a cycle.</summary>
        public bool HasCycle { get; set; } = false;

        /// <summary>First deterministic closed cycle path, with the first identifier repeated at the end.</summary>
        public List<string> CyclePath { get; set; } = new List<string>();

        /// <summary>Every reachable non-complete or missing blocking node.</summary>
        public List<ObjectiveDependencyNode> BlockingNodes { get; set; } = new List<ObjectiveDependencyNode>();

        /// <summary>Every reachable edge that participates in blocking the objective.</summary>
        public List<ObjectiveDependencyEdge> BlockingEdges { get; set; } = new List<ObjectiveDependencyEdge>();

        /// <summary>Every deterministic root-to-terminal blocking path.</summary>
        public List<ObjectiveDependencyChain> BlockingChains { get; set; } = new List<ObjectiveDependencyChain>();

        /// <summary>
        /// True when the complete node-and-edge graph contains more terminal paths than
        /// <see cref="BlockingChains"/> can safely return.
        /// </summary>
        public bool BlockingChainsTruncated { get; set; } = false;
    }
}
