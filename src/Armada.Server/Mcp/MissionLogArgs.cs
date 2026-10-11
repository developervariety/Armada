namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for retrieving a mission's session log.
    /// </summary>
    public class MissionLogArgs
    {
        /// <summary>
        /// Mission ID (msn_ prefix).
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Number of lines to return (default 100).
        /// </summary>
        public int? Lines { get; set; }

        /// <summary>
        /// Line offset to start from (default 0).
        /// </summary>
        public int? Offset { get; set; }

        /// <summary>Mission-bound output capture ID. When set, byte paging is used instead of the session log.</summary>
        public string? CaptureId { get; set; }

        /// <summary>Zero-based capture byte offset.</summary>
        public long? OffsetBytes { get; set; }

        /// <summary>Requested capture page length in bytes.</summary>
        public int? LengthBytes { get; set; }

        /// <summary>Expected capture SHA-256 returned in the definition-of-done report.</summary>
        public string? Sha256 { get; set; }
    }
}
