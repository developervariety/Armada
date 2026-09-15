namespace Armada.Core.Services
{
    using System;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Builds self-contained Armada MCP server configuration documents for the various agent runtimes.
    /// These are used when launching a captain in an isolated configuration: because strict / scoped
    /// launches deliberately ignore the host user's globally installed MCP servers, the launch must be
    /// handed an explicit configuration that still points the agent at the Armada MCP endpoint. The
    /// schemas here mirror exactly what "armada mcp install" writes for each client so an isolated
    /// captain sees the identical Armada server. Side-effect free (pure string/JSON construction) so it
    /// can be unit tested in isolation.
    /// </summary>
    public static class ArmadaMcpConfigBuilder
    {
        #region Public-Methods

        /// <summary>
        /// The Armada MCP Streamable HTTP URL for a given port (matches the installer's endpoint).
        /// </summary>
        /// <param name="mcpPort">The Admiral MCP port.</param>
        /// <returns>The MCP URL, e.g. http://localhost:7891/mcp.</returns>
        public static string GetMcpUrl(int mcpPort)
        {
            return "http://localhost:" + mcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/mcp";
        }

        /// <summary>
        /// Authorization header value for Claude Code and Gemini, which expand ${NAME} from the
        /// process environment. The launch credential itself is never written into a file.
        /// </summary>
        public static readonly string AuthorizationForDollarBraceExpansion = AuthorizationDollarBrace(McpLaunchCredential.EnvironmentVariable);

        /// <summary>
        /// Authorization header value for Cursor, which expands ${env:NAME} from the process environment.
        /// </summary>
        public static readonly string AuthorizationForCursorExpansion = AuthorizationCursor(McpLaunchCredential.EnvironmentVariable);

        /// <summary>
        /// Authorization header value for OpenCode, which expands {env:NAME} from the process environment.
        /// </summary>
        public static readonly string AuthorizationForOpenCodeExpansion = AuthorizationOpenCode(McpLaunchCredential.EnvironmentVariable);

        /// <summary>
        /// Authorization header value for Claude Code and Gemini, referencing the given environment variable
        /// with ${NAME} expansion. The credential value is referenced by name, never written into a file.
        /// </summary>
        /// <param name="environmentVariable">Environment variable name that carries the credential.</param>
        /// <returns>The Authorization header value.</returns>
        public static string AuthorizationDollarBrace(string environmentVariable)
        {
            if (String.IsNullOrWhiteSpace(environmentVariable)) throw new ArgumentNullException(nameof(environmentVariable));
            return "Bearer ${" + environmentVariable + "}";
        }

        /// <summary>
        /// Authorization header value for Cursor, referencing the given environment variable with
        /// ${env:NAME} expansion.
        /// </summary>
        /// <param name="environmentVariable">Environment variable name that carries the credential.</param>
        /// <returns>The Authorization header value.</returns>
        public static string AuthorizationCursor(string environmentVariable)
        {
            if (String.IsNullOrWhiteSpace(environmentVariable)) throw new ArgumentNullException(nameof(environmentVariable));
            return "Bearer ${env:" + environmentVariable + "}";
        }

        /// <summary>
        /// Authorization header value for OpenCode, referencing the given environment variable with
        /// {env:NAME} expansion.
        /// </summary>
        /// <param name="environmentVariable">Environment variable name that carries the credential.</param>
        /// <returns>The Authorization header value.</returns>
        public static string AuthorizationOpenCode(string environmentVariable)
        {
            if (String.IsNullOrWhiteSpace(environmentVariable)) throw new ArgumentNullException(nameof(environmentVariable));
            return "Bearer {env:" + environmentVariable + "}";
        }

        /// <summary>
        /// Build the keyed "mcpServers" document used by Claude Code, Gemini, and Cursor. The Armada
        /// server is registered under the "armada" key with the modern HTTP transport.
        /// </summary>
        /// <param name="mcpPort">The Admiral MCP port.</param>
        /// <param name="authorizationHeader">Authorization header value in the client's environment-reference syntax, or null to omit it.</param>
        /// <returns>An indented JSON document string.</returns>
        public static string BuildKeyedMcpServersJson(int mcpPort, string? authorizationHeader = null)
        {
            JsonObject server = new JsonObject
            {
                ["type"] = "http",
                ["url"] = GetMcpUrl(mcpPort),
            };
            if (!String.IsNullOrEmpty(authorizationHeader))
                server["headers"] = new JsonObject { ["Authorization"] = authorizationHeader };

            JsonObject root = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["armada"] = server,
                },
            };
            return root.ToJsonString(_IndentedOptions);
        }

        /// <summary>Build an OpenCode MCP-only overlay for an isolated launch.</summary>
        /// <param name="mcpPort">The Admiral MCP port.</param>
        /// <param name="authorizationHeader">Authorization header value in OpenCode's environment-reference syntax, or null to omit it.</param>
        /// <returns>A JSON document containing only the Armada MCP server.</returns>
        public static string BuildOpenCodeMcpJson(int mcpPort, string? authorizationHeader = null)
        {
            JsonObject server = new JsonObject { ["type"] = "remote", ["url"] = GetMcpUrl(mcpPort) };
            if (!String.IsNullOrEmpty(authorizationHeader))
                server["headers"] = new JsonObject { ["Authorization"] = authorizationHeader };

            JsonObject root = new JsonObject
            {
                ["mcp"] = new JsonObject
                {
                    ["armada"] = server
                }
            };
            return root.ToJsonString(_IndentedOptions);
        }

        /// <summary>
        /// Build the "servers" array document used by Mux, which stores each server as an object carrying
        /// its own name plus a separate transport/url/mcpPath (a different shape from the keyed clients).
        /// </summary>
        /// <param name="mcpPort">The Admiral MCP port.</param>
        /// <returns>An indented JSON document string.</returns>
        public static string BuildMuxServersJson(int mcpPort)
        {
            return BuildMuxServersJson(mcpPort, McpLaunchCredential.EnvironmentVariable);
        }

        /// <summary>
        /// Build the "servers" array document used by Mux, referencing the given environment variable in the
        /// bearer token so the credential value is never written into the file.
        /// </summary>
        /// <param name="mcpPort">The Admiral MCP port.</param>
        /// <param name="environmentVariable">Environment variable name that carries the credential.</param>
        /// <returns>An indented JSON document string.</returns>
        public static string BuildMuxServersJson(int mcpPort, string environmentVariable)
        {
            if (String.IsNullOrWhiteSpace(environmentVariable)) throw new ArgumentNullException(nameof(environmentVariable));

            // Mux sends an HTTP server's credential from its auth object and expands ${NAME} in the token,
            // so the credential is referenced by variable name and never written into the file.
            JsonObject server = new JsonObject
            {
                ["name"] = "armada",
                ["transport"] = "http",
                ["url"] = "http://localhost:" + mcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["mcpPath"] = "/mcp",
                ["auth"] = new JsonObject
                {
                    ["scheme"] = "bearer_token",
                    ["token"] = "${" + environmentVariable + "}",
                },
            };
            JsonObject root = new JsonObject
            {
                ["servers"] = new JsonArray(server),
            };
            return root.ToJsonString(_IndentedOptions);
        }

        /// <summary>
        /// Build the TOML fragment used by Codex (~/.codex/config.toml) to register the Armada MCP server
        /// over HTTP. Codex reads its configuration from a CODEX_HOME-scoped config.toml.
        /// </summary>
        /// <param name="mcpPort">The Admiral MCP port.</param>
        /// <returns>A TOML document string.</returns>
        public static string BuildCodexConfigToml(int mcpPort)
        {
            return "[mcp_servers.armada]" + Environment.NewLine
                + "url = \"" + GetMcpUrl(mcpPort) + "\"" + Environment.NewLine;
        }

        #endregion

        #region Private-Members

        private static readonly System.Text.Json.JsonSerializerOptions _IndentedOptions =
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

        #endregion
    }
}
