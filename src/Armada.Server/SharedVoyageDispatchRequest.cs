namespace Armada.Server
{
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;

    /// <summary>
    /// Normalized request for dispatching a voyage through the shared REST and MCP path.
    /// </summary>
    public sealed class SharedVoyageDispatchRequest
    {
        /// <summary>
        /// Voyage title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Voyage description.
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Target vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Missions to create.
        /// </summary>
        public List<MissionDescription> Missions { get; set; } = new List<MissionDescription>();

        /// <summary>
        /// Dispatch-level code context mode.
        /// </summary>
        public string? CodeContextMode { get; set; }

        /// <summary>
        /// Optional token budget for generated context packs.
        /// </summary>
        public int? CodeContextTokenBudget { get; set; }

        /// <summary>
        /// Optional maximum result count for generated context packs.
        /// </summary>
        public int? CodeContextMaxResults { get; set; }

        /// <summary>
        /// Pipeline identifier to use.
        /// </summary>
        public string? PipelineId { get; set; }

        /// <summary>
        /// Pipeline name to resolve when PipelineId is empty.
        /// </summary>
        public string? Pipeline { get; set; }

        /// <summary>
        /// Objective identifier to link after dispatch.
        /// </summary>
        public string? ObjectiveId { get; set; }

        /// <summary>
        /// Auth context used for objective reads and links. MCP callers omit this and use the default tenant admin context.
        /// </summary>
        public AuthContext? ObjectiveAuthContext { get; set; }

        /// <summary>
        /// Ordered playbooks applied at the voyage level.
        /// </summary>
        public List<SelectedPlaybook> SelectedPlaybooks { get; set; } = new List<SelectedPlaybook>();

        /// <summary>
        /// Settings used by pipeline model-tier enforcement.
        /// </summary>
        public ArmadaSettings? Settings { get; set; }
    }
}
