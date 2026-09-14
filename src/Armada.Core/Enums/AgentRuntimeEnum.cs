namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Supported agent runtime types.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AgentRuntimeEnum
    {
        /// <summary>
        /// Anthropic Claude Code CLI.
        /// </summary>
        [EnumMember(Value = "ClaudeCode")]
        ClaudeCode,

        /// <summary>
        /// OpenAI Codex CLI.
        /// </summary>
        [EnumMember(Value = "Codex")]
        Codex,

        /// <summary>
        /// Google Gemini CLI.
        /// </summary>
        [EnumMember(Value = "Gemini")]
        Gemini,

        /// <summary>
        /// Cursor agent CLI.
        /// </summary>
        [EnumMember(Value = "Cursor")]
        Cursor,

        /// <summary>
        /// OpenCode agent runtime.
        /// </summary>
        [EnumMember(Value = "OpenCode")]
        OpenCode,

        /// <summary>
        /// In-process runtime driven by a configured inference model endpoint.
        /// </summary>
        [EnumMember(Value = "ApiEndpoint")]
        ApiEndpoint,

        /// <summary>
        /// Mux CLI.
        /// </summary>
        [EnumMember(Value = "Mux")]
        Mux,

        /// <summary>
        /// Custom agent runtime.
        /// </summary>
        [EnumMember(Value = "Custom")]
        Custom
    }
}
