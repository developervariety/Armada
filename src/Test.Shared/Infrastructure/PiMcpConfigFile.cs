namespace Test.Shared.Infrastructure
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A pi <c>mcp.json</c> (user <c>~/.pi/agent/mcp.json</c> or project <c>.pi/mcp.json</c>).
    /// </summary>
    public class PiMcpConfigFile
    {
        #region Public-Members

        /// <summary>
        /// MCP servers by name.
        /// </summary>
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, PiMcpServerEntry>? McpServers { get; set; } = null;

        #endregion
    }
}
