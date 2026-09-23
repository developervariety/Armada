namespace Armada.Server.WebSocket
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Data payload for the create_voyage WebSocket command. A payload with a vessel and missions is
    /// dispatched through the shared voyage dispatch service, so it accepts the same dispatch fields
    /// as the REST voyage create and the MCP dispatch tool.
    /// </summary>
    public class WebSocketVoyageData
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
        /// Target vessel identifier.
        /// </summary>
        public string? VesselId { get; set; }

        /// <summary>
        /// List of missions to create with the voyage.
        /// </summary>
        public List<MissionDescription>? Missions { get; set; }

        /// <summary>
        /// Dispatch-level code context mode: auto, off, or force.
        /// </summary>
        public string? CodeContextMode { get; set; } = null;

        /// <summary>
        /// Optional token budget for generated context packs.
        /// </summary>
        public int? CodeContextTokenBudget { get; set; } = null;

        /// <summary>
        /// Optional maximum result count for generated context packs.
        /// </summary>
        public int? CodeContextMaxResults { get; set; } = null;

        /// <summary>
        /// Pipeline identifier to use.
        /// </summary>
        public string? PipelineId { get; set; } = null;

        /// <summary>
        /// Pipeline name to resolve when <see cref="PipelineId"/> is empty.
        /// </summary>
        public string? Pipeline { get; set; } = null;

        /// <summary>
        /// Ordered playbooks to apply to every mission in the voyage.
        /// </summary>
        public List<SelectedPlaybook>? SelectedPlaybooks { get; set; } = null;

        /// <summary>
        /// Objective to link to the dispatched voyage. Requires a vessel and at least one mission.
        /// </summary>
        public string? ObjectiveId { get; set; } = null;

        /// <summary>
        /// Operator override that lets a linked objective dispatch despite an incomplete dispatch preflight.
        /// </summary>
        public bool ForcePreflight { get; set; } = false;

        /// <summary>
        /// Per-persona captain overrides applied to every mission of that persona, including fan-out missions.
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
