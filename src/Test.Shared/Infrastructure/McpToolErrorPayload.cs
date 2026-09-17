namespace Test.Shared.Infrastructure
{
    /// <summary>
    /// The error envelope an Armada MCP tool returns in place of its normal payload. A refused call
    /// still answers 200 with a tool result, so a helper that deserializes the result straight into the
    /// entity it expected gets an object with every field null and carries that emptiness forward. The
    /// refusal is then reported by a later step ("Mission not found") instead of by the call that failed.
    /// </summary>
    public sealed class McpToolErrorPayload
    {
        #region Public-Members

        /// <summary>
        /// Human-readable refusal message, or null when the result is a normal payload.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Machine-readable refusal code, for example <c>fleet_capacity_reached</c>.
        /// </summary>
        public string? Code { get; set; }

        #endregion
    }
}
