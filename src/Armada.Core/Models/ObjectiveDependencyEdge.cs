namespace Armada.Core.Models
{
    /// <summary>
    /// One directed dependent-to-blocker edge in an objective dependency graph.
    /// </summary>
    public class ObjectiveDependencyEdge
    {
        /// <summary>Objective that waits.</summary>
        public string DependentObjectiveId { get; set; } = string.Empty;

        /// <summary>Objective that must complete.</summary>
        public string BlockingObjectiveId { get; set; } = string.Empty;
    }
}
