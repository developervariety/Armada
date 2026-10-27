namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments for list_model_probe_runs.
    /// </summary>
    public class ModelProbeRunListArgs
    {
        /// <summary>
        /// Captain filter (cpt_ prefix), or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Model endpoint filter (mep_ prefix), or null.
        /// </summary>
        public string? ModelEndpointId { get; set; } = null;

        /// <summary>
        /// Status filter: Queued, Running, Completed, or Failed.
        /// </summary>
        public string? Status { get; set; } = null;

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
