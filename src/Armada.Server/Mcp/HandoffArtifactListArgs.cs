namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments for list_handoff_artifacts.
    /// </summary>
    public class HandoffArtifactListArgs
    {
        /// <summary>
        /// Mission ID (msn_ prefix).
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Page number (default 1).
        /// </summary>
        public int? PageNumber { get; set; } = null;

        /// <summary>
        /// Page size (1-1000, default 25).
        /// </summary>
        public int? PageSize { get; set; } = null;
    }
}
