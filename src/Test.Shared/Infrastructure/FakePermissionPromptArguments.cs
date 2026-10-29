namespace Test.Shared.Infrastructure
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// cli_permission_prompt arguments.
    /// </summary>
    public sealed class FakePermissionPromptArguments
    {
        /// <summary>
        /// tool_name.
        /// </summary>
        [JsonPropertyName("tool_name")]
        public string? ToolName { get; set; } = null;

        /// <summary>
        /// input (string values).
        /// </summary>
        [JsonPropertyName("input")]
        public Dictionary<string, string>? Input { get; set; } = null;

        /// <summary>
        /// tool_use_id.
        /// </summary>
        [JsonPropertyName("tool_use_id")]
        public string? ToolUseId { get; set; } = null;
    }
}
