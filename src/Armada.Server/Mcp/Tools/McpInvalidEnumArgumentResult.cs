namespace Armada.Server.Mcp.Tools
{
    using System;

    /// <summary>
    /// MCP tool result for an argument whose value is not a member of its enum.
    /// </summary>
    internal sealed class McpInvalidEnumArgumentResult
    {
        #region Public-Members

        /// <summary>
        /// Always "failed".
        /// </summary>
        public string Status { get; set; } = "failed";

        /// <summary>
        /// Tool that rejected the argument.
        /// </summary>
        public string Tool { get; set; } = String.Empty;

        /// <summary>
        /// Stable failure code.
        /// </summary>
        public string Code { get; set; } = "invalid_enum_value";

        /// <summary>
        /// Argument name as the caller sent it.
        /// </summary>
        public string Field { get; set; } = String.Empty;

        /// <summary>
        /// Human-readable failure.
        /// </summary>
        public string Error { get; set; } = String.Empty;

        /// <summary>
        /// Every value the field accepts.
        /// </summary>
        public string[] ValidValues { get; set; } = Array.Empty<string>();

        #endregion
    }
}
