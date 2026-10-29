namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The streaming delta inside a pi <c>message_update</c> event (<c>assistantMessageEvent</c>): <c>text_start</c>,
    /// <c>text_delta</c>, <c>text_end</c>, <c>thinking_*</c>, <c>toolcall_start</c>, <c>toolcall_delta</c>,
    /// <c>toolcall_end</c>, <c>done</c> or <c>error</c>.
    /// </summary>
    public class PiAssistantMessageEvent
    {
        #region Public-Members

        /// <summary>text_delta.</summary>
        public const string TypeTextDelta = "text_delta";

        /// <summary>text_end.</summary>
        public const string TypeTextEnd = "text_end";

        /// <summary>thinking_delta.</summary>
        public const string TypeThinkingDelta = "thinking_delta";

        /// <summary>toolcall_start.</summary>
        public const string TypeToolCallStart = "toolcall_start";

        /// <summary>toolcall_end.</summary>
        public const string TypeToolCallEnd = "toolcall_end";

        /// <summary>error.</summary>
        public const string TypeError = "error";

        /// <summary>
        /// Delta type.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; } = null;

        /// <summary>
        /// Index of the content block the delta belongs to, or null.
        /// </summary>
        [JsonPropertyName("contentIndex")]
        public int? ContentIndex { get; set; } = null;

        /// <summary>
        /// Text of a text, thinking or tool-call argument delta, or null.
        /// </summary>
        [JsonPropertyName("delta")]
        [JsonConverter(typeof(TextOrRawJsonConverter))]
        public string? Delta { get; set; } = null;

        /// <summary>
        /// Full block text of a <c>text_end</c> or <c>thinking_end</c>, or null.
        /// </summary>
        [JsonPropertyName("content")]
        [JsonConverter(typeof(TextOrRawJsonConverter))]
        public string? Content { get; set; } = null;

        /// <summary>
        /// Tool call id of a <c>toolcall_start</c>, or null.
        /// </summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; } = null;

        /// <summary>
        /// Tool name of a <c>toolcall_start</c>, or null.
        /// </summary>
        [JsonPropertyName("toolName")]
        public string? ToolName { get; set; } = null;

        /// <summary>
        /// The finished tool call of a <c>toolcall_end</c>, or null.
        /// </summary>
        [JsonPropertyName("toolCall")]
        public PiContentBlock? ToolCall { get; set; } = null;

        /// <summary>
        /// Reason of an <c>error</c> or <c>done</c> delta, or null.
        /// </summary>
        [JsonPropertyName("reason")]
        public string? Reason { get; set; } = null;

        #endregion
    }
}
