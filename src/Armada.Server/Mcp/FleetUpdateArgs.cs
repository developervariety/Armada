namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for updating a fleet.
    /// </summary>
    public class FleetUpdateArgs
    {
        /// <summary>
        /// Fleet ID (flt_ prefix).
        /// </summary>
        public string FleetId { get; set; } = "";

        /// <summary>
        /// New fleet name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// New fleet description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Default pipeline ID for dispatches to vessels in this fleet (ppl_ prefix).
        /// </summary>
        public string? DefaultPipelineId { get; set; }

        /// <summary>
        /// JSON-serialized list of <see cref="Armada.Core.Models.SelectedPlaybook"/> entries
        /// that auto-merge into every mission whose vessel belongs to this fleet
        /// (Reflections v2-F3). Layered FIRST in the four-way merge.
        /// </summary>
        public string? DefaultPlaybooks { get; set; }

        /// <summary>
        /// Per-fleet fleet-curate trigger threshold in mission count across active vessels
        /// (Reflections v2-F3). Null disables the audit-drain auto-trigger.
        /// </summary>
        public int? CurateThreshold { get; set; }
    }
}
