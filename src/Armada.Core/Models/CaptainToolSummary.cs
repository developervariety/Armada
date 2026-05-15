namespace Armada.Core.Models
{
    /// <summary>
    /// Summary of one Armada MCP tool exposed to captains.
    /// </summary>
    public class CaptainToolSummary
    {
        /// <summary>
        /// Tool name.
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Human-readable tool description.
        /// </summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>
        /// Serialized JSON input schema, when available.
        /// </summary>
        public string? InputSchemaJson { get; set; } = null;
    }
}
