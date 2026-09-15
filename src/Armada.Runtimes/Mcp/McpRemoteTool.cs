namespace Armada.Runtimes.Mcp
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A tool a remote MCP server advertised in <c>tools/list</c>: its name, description and JSON-Schema input
    /// definition, so the caller can offer it to a model and route a matching call back to the server.
    /// </summary>
    public class McpRemoteTool
    {
        #region Public-Members

        /// <summary>
        /// Tool name, used verbatim in <c>tools/call</c>.
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Human-readable description; may be empty.
        /// </summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>
        /// JSON-Schema input definition. An object schema with no properties when the server advertised none.
        /// </summary>
        public Dictionary<string, object> InputSchema
        {
            get => _InputSchema;
            set => _InputSchema = value ?? new Dictionary<string, object> { ["type"] = "object" };
        }

        #endregion

        #region Private-Members

        private Dictionary<string, object> _InputSchema = new Dictionary<string, object> { ["type"] = "object" };

        #endregion
    }

    /// <summary>
    /// The result of one MCP tool call.
    /// </summary>
    public class McpToolCallResult
    {
        #region Public-Members

        /// <summary>
        /// Concatenated text of the result content.
        /// </summary>
        public string Text { get; set; } = String.Empty;

        /// <summary>
        /// True when the server marked the result as a tool error.
        /// </summary>
        public bool IsError { get; set; } = false;

        #endregion
    }
}
