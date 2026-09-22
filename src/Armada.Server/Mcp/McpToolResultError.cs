namespace Armada.Server.Mcp
{
    using System.Text.Json;
    using ModelContextProtocol.Protocol;

    /// <summary>
    /// The one rule every MCP transport uses to decide that a tool handler's returned value is an
    /// error. A handler reports an error without throwing in two ways: an explicit protocol result
    /// whose <c>isError</c> flag is true, or an error envelope whose top-level <c>Error</c> property
    /// (any letter case) is a non-empty string. An explicit <c>isError</c> flag is authoritative in
    /// both directions. An <c>Error</c> property that is null, empty, or not a string, and an
    /// <c>Error</c> nested inside the payload, do not make the result an error.
    /// </summary>
    public static class McpToolResultError
    {
        #region Public-Members

        /// <summary>
        /// Message reported when an error result carries no readable text.
        /// </summary>
        public const string DefaultMessage = "Tool returned an error.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Decide whether a serialized tool result is an error.
        /// </summary>
        /// <param name="result">The handler's return value serialized to JSON.</param>
        /// <param name="message">The error message when the result is an error; otherwise null.</param>
        /// <returns>True when the result is an error.</returns>
        public static bool TryGetError(JsonElement result, out string? message)
        {
            message = null;
            if (result.ValueKind != JsonValueKind.Object) return false;

            bool? explicitFlag = null;
            string? errorText = null;
            string? contentText = null;
            foreach (JsonProperty property in result.EnumerateObject())
            {
                if (String.Equals(property.Name, "isError", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.True) explicitFlag = true;
                    else if (property.Value.ValueKind == JsonValueKind.False) explicitFlag = false;
                }
                else if (String.Equals(property.Name, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        string? value = property.Value.GetString();
                        if (!String.IsNullOrWhiteSpace(value)) errorText = value;
                    }
                }
                else if (String.Equals(property.Name, "content", StringComparison.OrdinalIgnoreCase))
                {
                    contentText = FirstText(property.Value);
                }
            }

            if (explicitFlag.HasValue)
            {
                if (!explicitFlag.Value) return false;
                message = errorText ?? contentText ?? DefaultMessage;
                return true;
            }

            if (errorText == null) return false;
            message = errorText;
            return true;
        }

        /// <summary>
        /// Decide whether an explicit protocol result is an error. Its <c>IsError</c> flag is the
        /// whole answer.
        /// </summary>
        /// <param name="result">The protocol result the handler returned.</param>
        /// <param name="message">The error message when the result is an error; otherwise null.</param>
        /// <returns>True when the result is an error.</returns>
        public static bool TryGetError(CallToolResult result, out string? message)
        {
            message = null;
            if (result == null || result.IsError != true) return false;

            if (result.Content != null)
            {
                foreach (ContentBlock block in result.Content)
                {
                    if (block is TextContentBlock text && !String.IsNullOrWhiteSpace(text.Text))
                    {
                        message = text.Text;
                        return true;
                    }
                }
            }

            message = DefaultMessage;
            return true;
        }

        #endregion

        #region Private-Methods

        private static string? FirstText(JsonElement content)
        {
            if (content.ValueKind != JsonValueKind.Array) return null;
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                foreach (JsonProperty property in block.EnumerateObject())
                {
                    if (!String.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase)) continue;
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    string? text = property.Value.GetString();
                    if (!String.IsNullOrWhiteSpace(text)) return text;
                }
            }

            return null;
        }

        #endregion
    }
}
