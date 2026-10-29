namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments naming a handoff artifact (get_handoff_artifact).
    /// </summary>
    public class HandoffArtifactIdArgs
    {
        /// <summary>
        /// Artifact ID (hoa_ prefix).
        /// </summary>
        public string ArtifactId { get; set; } = "";
    }
}
