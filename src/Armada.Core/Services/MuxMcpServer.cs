namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Text.Json.Serialization;

    /// <summary>
    /// One server entry in a <see cref="MuxMcpServersDocument"/>. An HTTP server is reached at
    /// <see cref="Url"/> plus <see cref="McpPath"/>; a stdio server runs <see cref="Command"/>.
    /// </summary>
    public sealed class MuxMcpServer
    {
        #region Public-Members

        /// <summary>
        /// Transport name for a remote Streamable HTTP server.
        /// </summary>
        public const string HttpTransport = "http";

        /// <summary>
        /// Unique server name. Mux prefixes each discovered tool with it.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Transport: "stdio" or "http".
        /// </summary>
        [JsonPropertyName("transport")]
        public string? Transport { get; set; } = null;

        /// <summary>
        /// Command that starts a stdio server.
        /// </summary>
        [JsonPropertyName("command")]
        public string? Command { get; set; } = null;

        /// <summary>
        /// Arguments for a stdio server command.
        /// </summary>
        [JsonPropertyName("args")]
        public List<string>? Args { get; set; } = null;

        /// <summary>
        /// Environment for a stdio server process.
        /// </summary>
        [JsonPropertyName("env")]
        public Dictionary<string, string>? Env { get; set; } = null;

        /// <summary>
        /// Base URL of an HTTP server, without the MCP path.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; } = null;

        /// <summary>
        /// MCP path appended to <see cref="Url"/>. Mux uses /mcp when it is absent.
        /// </summary>
        [JsonPropertyName("mcpPath")]
        public string? McpPath { get; set; } = null;

        /// <summary>
        /// Credential Mux presents to an HTTP server.
        /// </summary>
        [JsonPropertyName("auth")]
        public MuxMcpAuth? Auth { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// An HTTP server entry.
        /// </summary>
        /// <param name="name">Server name.</param>
        /// <param name="baseUrl">Base URL without the MCP path.</param>
        /// <param name="mcpPath">MCP path.</param>
        /// <param name="auth">Credential, or null for none.</param>
        /// <returns>The server entry.</returns>
        public static MuxMcpServer Http(string name, string baseUrl, string mcpPath, MuxMcpAuth? auth)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));
            return new MuxMcpServer
            {
                Name = name,
                Transport = HttpTransport,
                Url = baseUrl,
                McpPath = mcpPath,
                Auth = auth,
            };
        }

        /// <summary>
        /// Read one server entry from a JSON node in this shape.
        /// </summary>
        /// <param name="node">Server object node.</param>
        /// <returns>The server entry, or null when the node is null.</returns>
        public static MuxMcpServer? FromJsonNode(JsonNode? node)
        {
            if (node == null) return null;
            return node.Deserialize<MuxMcpServer>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        /// <summary>
        /// This server entry as a JSON object, omitting fields that are not set.
        /// </summary>
        /// <returns>The server object.</returns>
        public JsonObject ToJsonObject()
        {
            return JsonSerializer.SerializeToNode(this, MuxMcpServersDocument.WriteOptions)!.AsObject();
        }

        #endregion
    }
}
