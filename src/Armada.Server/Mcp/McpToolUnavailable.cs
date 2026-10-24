namespace Armada.Server.Mcp
{
    /// <summary>
    /// Builds a stable error result for a tool whose service is not part of this host.
    /// </summary>
    internal static class McpToolUnavailable
    {
        /// <summary>
        /// Create a top-level MCP tool error that identifies the missing service.
        /// </summary>
        /// <param name="service">Name of the unavailable service.</param>
        /// <returns>A result that the MCP transport reports as an error.</returns>
        internal static object Create(string service)
        {
            return new
            {
                Error = "Unavailable: " + service + " is not available in this host.",
                ErrorCode = "Unavailable"
            };
        }
    }
}
