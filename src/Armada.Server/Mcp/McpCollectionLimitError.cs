namespace Armada.Server.Mcp
{
    using System.Text.Json.Nodes;
    using Armada.Core.Services;

    /// <summary>
    /// A collection-limit refusal as an MCP tool error. The payload carries the same code, field,
    /// limit and count the REST body carries. The stdio frame is returned directly so the transport
    /// does not collapse it into a protocol error that drops those fields.
    /// </summary>
    internal static class McpCollectionLimitError
    {
        #region Internal-Methods

        /// <summary>
        /// Build the object an HTTP tool handler returns.
        /// </summary>
        /// <param name="limit">The refusal.</param>
        /// <returns>An error envelope the HTTP transport marks as a tool error.</returns>
        internal static object Create(CollectionLimitExceededException limit)
        {
            if (limit == null) throw new ArgumentNullException(nameof(limit));
            return new
            {
                Error = limit.Message,
                ErrorCode = CollectionLimitExceededException.ErrorCode,
                Field = limit.Field,
                Limit = limit.Limit,
                Actual = limit.Actual
            };
        }

        /// <summary>
        /// Build the stdio tools/call result. It has no top-level Error string, so the stdio server
        /// returns it as the tool result instead of a JSON-RPC protocol error.
        /// </summary>
        /// <param name="limit">The refusal.</param>
        /// <returns>A tool result whose isError flag is set.</returns>
        internal static JsonObject StdioResult(CollectionLimitExceededException limit)
        {
            if (limit == null) throw new ArgumentNullException(nameof(limit));
            return new JsonObject
            {
                ["isError"] = true,
                ["errorCode"] = CollectionLimitExceededException.ErrorCode,
                ["field"] = limit.Field,
                ["limit"] = limit.Limit,
                ["actual"] = limit.Actual,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = limit.Message
                    }
                }
            };
        }

        #endregion
    }
}
