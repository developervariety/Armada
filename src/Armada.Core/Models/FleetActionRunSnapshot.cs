namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>Immutable definition and request data captured when a run is created.</summary>
    public sealed class FleetActionRunSnapshot
    {
        /// <summary>Action name at run creation.</summary>
        public string ActionName { get; set; } = string.Empty;
        /// <summary>Action kind at run creation.</summary>
        public FleetActionKindEnum ActionKind { get; set; } = FleetActionKindEnum.Mission;
        /// <summary>Prompt template at run creation.</summary>
        public string PromptTemplate { get; set; } = string.Empty;
        /// <summary>Pipeline identifier at run creation.</summary>
        public string? PipelineId { get; set; }
        /// <summary>Concurrency limit at run creation.</summary>
        public int Concurrency { get; set; } = 1;
        /// <summary>All selected targets and their fully resolved inputs.</summary>
        public List<FleetActionRunTarget> Targets { get; set; } = new List<FleetActionRunTarget>();
    }
}
