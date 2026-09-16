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
        /// Optional objective/backlog item ID to link to the dispatched voyage.
        /// </summary>
        public string? ObjectiveId { get; set; }

        /// <summary>
        /// Override an incomplete dispatch preflight on the linked objective. It overrides only the
        /// preflight block; any other blocking issue still refuses the dispatch, and an override is
        /// recorded as an objective event.
        /// </summary>
        public bool ForcePreflight { get; set; } = false;

        /// <summary>
        /// Ordered playbooks to apply during dispatch.
        /// </summary>
        public List<SelectedPlaybook> SelectedPlaybooks { get; set; } = new List<SelectedPlaybook>();

        /// <summary>
        /// Optional per-persona captain overrides for this voyage. Each entry binds a pipeline step
        /// (persona) to a preferred captain and a fallback tier, applied to every mission of that persona.
        /// </summary>
        public List<CaptainAssignmentOverride>? CaptainAssignments { get; set; } = null;

        /// <summary>
        /// Persona names of pipeline stages the operator confirms this voyage does not need, for
        /// example <c>TestEngineer</c>. The stages are dropped when the voyage is materialised and the
        /// remaining stages chain across the gap. The Judge can never be skipped, and a name that is not
        /// a stage of the effective pipeline refuses the dispatch.
        /// </summary>
        public List<string>? SkipStages { get; set; } = null;

        /// <summary>
        /// Why the operator skips the stages. Recorded on each <c>voyage.stage_skipped</c> event.
        /// </summary>
        public string? SkipStagesReason { get; set; } = null;
    }
}
