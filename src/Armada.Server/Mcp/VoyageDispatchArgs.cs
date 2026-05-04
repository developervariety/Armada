namespace Armada.Server.Mcp
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// MCP tool arguments for dispatching a voyage with missions.
    /// </summary>
    public class VoyageDispatchArgs
    {
        /// <summary>
        /// Voyage title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Voyage description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Target vessel ID.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// List of missions to create.
        /// </summary>
        public List<MissionDescription> Missions { get; set; } = new List<MissionDescription>();

        /// <summary>
        /// Dispatch-level code context mode. Supported values are auto, off, and force.
        /// Default behavior is auto.
        /// </summary>
        public string? CodeContextMode { get; set; }

        /// <summary>
        /// Optional token budget for generated code context packs.
        /// </summary>
        public int? CodeContextTokenBudget { get; set; }

        /// <summary>
        /// Optional maximum evidence result count for generated code context packs.
        /// </summary>
        public int? CodeContextMaxResults { get; set; }

        /// <summary>
        /// Pipeline ID to use for this dispatch (overrides vessel/fleet default).
        /// </summary>
        public string? PipelineId { get; set; }

        /// <summary>
        /// Pipeline name to use (convenience alias for pipelineId -- resolves by name).
        /// </summary>
        public string? Pipeline { get; set; }

        /// <summary>
        /// Ordered playbooks to apply during dispatch.
        /// </summary>
        public List<SelectedPlaybook> SelectedPlaybooks { get; set; } = new List<SelectedPlaybook>();
    }
}
