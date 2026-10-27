namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP arguments for evaluate_captain: exactly one of captainId or modelEndpointId, and an optional depth.
    /// </summary>
    public class ModelProbeEvaluateArgs
    {
        /// <summary>
        /// Captain to evaluate (cpt_ prefix), or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Model endpoint to evaluate (mep_ prefix), or null.
        /// </summary>
        public string? ModelEndpointId { get; set; } = null;

        /// <summary>
        /// Depth: Quick (default) or Full (not implemented until Phase 5).
        /// </summary>
        public string? Depth { get; set; } = null;
    }
}
