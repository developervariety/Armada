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
        /// that auto-merge into every mission whose vessel belongs to this fleet.
        /// Layered FIRST in the fleet -> vessel -> persona -> captain merge.
        /// </summary>
        public string? DefaultPlaybooks { get; set; }
    }
}
