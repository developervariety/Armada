namespace Armada.Server.Mcp
{
    /// <summary>
    /// Audit information for one MCP tool call. Authentication headers are never included.
    /// </summary>
    public class McpToolCallAudit
    {
        #region Public-Members

        /// <summary>
        /// Tool name.
        /// </summary>
        public string ToolName { get; set; } = String.Empty;

        /// <summary>
        /// Server-assigned or request participant key.
        /// </summary>
        public string? ParticipantKey { get; set; } = null;

        /// <summary>
        /// Serialized tool arguments, limited by the HTTP server.
        /// </summary>
        public string? ArgumentsJson { get; set; } = null;

        /// <summary>
        /// Audit phase: Started, Succeeded, or Failed.
        /// </summary>
        public string Phase { get; set; } = "Started";

        /// <summary>
        /// True when the handler completed successfully.
        /// </summary>
        public bool Succeeded { get; set; } = false;

        /// <summary>
        /// Error message when the handler failed.
        /// </summary>
        public string? Error { get; set; } = null;

        /// <summary>
        /// UTC request completion time.
        /// </summary>
        public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
