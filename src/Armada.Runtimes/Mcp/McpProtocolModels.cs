namespace Armada.Runtimes.Mcp
{
    using System.Collections.Generic;

    /// <summary>
    /// A JSON-RPC request or notification. A notification carries no identifier.
    /// </summary>
    internal sealed class McpRpcRequest
    {
        /// <summary>Protocol version.</summary>
        public string Jsonrpc { get; set; } = "2.0";

        /// <summary>Request identifier; null for a notification.</summary>
        public int? Id { get; set; } = null;

        /// <summary>Method name.</summary>
        public string Method { get; set; } = "";

        /// <summary>Method parameters.</summary>
        public object? Params { get; set; } = null;
    }

    /// <summary>
    /// A JSON-RPC response with a typed result.
    /// </summary>
    /// <typeparam name="TResult">Result type.</typeparam>
    internal sealed class McpRpcResponse<TResult> where TResult : class
    {
        /// <summary>Result, when the call succeeded.</summary>
        public TResult? Result { get; set; } = null;

        /// <summary>Error, when the call failed.</summary>
        public McpRpcError? Error { get; set; } = null;
    }

    /// <summary>
    /// A JSON-RPC error.
    /// </summary>
    internal sealed class McpRpcError
    {
        /// <summary>Error code.</summary>
        public int Code { get; set; } = 0;

        /// <summary>Error message.</summary>
        public string? Message { get; set; } = null;
    }

    /// <summary>
    /// The <c>initialize</c> result; only its presence is used.
    /// </summary>
    internal sealed class McpInitializeResult
    {
        /// <summary>Negotiated protocol version.</summary>
        public string? ProtocolVersion { get; set; } = null;
    }

    /// <summary>
    /// The <c>tools/list</c> result.
    /// </summary>
    internal sealed class McpToolsListResult
    {
        /// <summary>Tools on this page.</summary>
        public List<McpToolDescriptor>? Tools { get; set; } = null;

        /// <summary>Cursor for the next page; null on the last page.</summary>
        public string? NextCursor { get; set; } = null;
    }

    /// <summary>
    /// One tool in a <c>tools/list</c> result.
    /// </summary>
    internal sealed class McpToolDescriptor
    {
        /// <summary>Tool name.</summary>
        public string? Name { get; set; } = null;

        /// <summary>Tool description.</summary>
        public string? Description { get; set; } = null;

        /// <summary>JSON-Schema input definition.</summary>
        public Dictionary<string, object>? InputSchema { get; set; } = null;
    }

    /// <summary>
    /// The <c>tools/call</c> result.
    /// </summary>
    internal sealed class McpCallToolResult
    {
        /// <summary>Content blocks.</summary>
        public List<McpContentBlock>? Content { get; set; } = null;

        /// <summary>True when the tool reported an error.</summary>
        public bool? IsError { get; set; } = null;
    }

    /// <summary>
    /// One content block of a tool result.
    /// </summary>
    internal sealed class McpContentBlock
    {
        /// <summary>Block type, for example text.</summary>
        public string? Type { get; set; } = null;

        /// <summary>Text of a text block.</summary>
        public string? Text { get; set; } = null;
    }
}
