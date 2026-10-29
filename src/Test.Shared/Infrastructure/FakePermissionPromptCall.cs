namespace Test.Shared.Infrastructure
{
    using System;

    /// <summary>
    /// One <c>cli_permission_prompt</c> call received by <see cref="FakePermissionPromptMcpServer"/>.
    /// </summary>
    public sealed class FakePermissionPromptCall
    {
        /// <summary>
        /// The tool_name argument.
        /// </summary>
        public string ToolName { get; set; } = "";

        /// <summary>
        /// The input.command argument, when present.
        /// </summary>
        public string? Command { get; set; } = null;

        /// <summary>
        /// The tool_use_id argument.
        /// </summary>
        public string? ToolUseId { get; set; } = null;

        /// <summary>
        /// The X-Token header of the call.
        /// </summary>
        public string? Token { get; set; } = null;

        /// <summary>
        /// The Mcp-Session-Id header of the call.
        /// </summary>
        public string? SessionId { get; set; } = null;

        /// <summary>
        /// When the call arrived.
        /// </summary>
        public DateTime ArrivedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the server answered it (DateTime.MinValue while waiting).
        /// </summary>
        public DateTime AnsweredUtc { get; set; } = DateTime.MinValue;
    }
}
