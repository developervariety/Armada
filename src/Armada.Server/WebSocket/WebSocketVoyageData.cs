namespace Armada.Server.WebSocket
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Data payload for the create_voyage WebSocket command.
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
