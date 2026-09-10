namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One objective in a reachable blocking dependency graph.
    /// </summary>
    public class ObjectiveDependencyNode
    {
        /// <summary>Objective identifier.</summary>
        public string ObjectiveId { get; set; } = string.Empty;

        /// <summary>Objective title, or null when the identifier does not resolve.</summary>
        public string? Title { get; set; } = null;

        /// <summary>Objective status, or null when the identifier does not resolve.</summary>
        public ObjectiveStatusEnum? Status { get; set; } = null;

        /// <summary>True when the identifier does not resolve in the supplied snapshot.</summary>
        public bool IsMissing { get; set; } = false;
    }
}
