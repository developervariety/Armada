namespace Armada.Core.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A pi message carried by <c>message_start</c>, <c>message_end</c>, <c>turn_end</c> and <c>agent_end</c>: role
    /// (<c>system</c>, <c>user</c>, <c>assistant</c>, <c>toolResult</c>), content blocks, and for assistant messages the
    /// provider, model, usage, <c>stopReason</c> and <c>errorMessage</c>.
    /// </summary>
    public class PiMessage
    {
        #region Public-Members

        /// <summary>assistant.</summary>
        public const string RoleAssistant = "assistant";

        /// <summary>toolResult.</summary>
        public const string RoleToolResult = "toolResult";

        /// <summary>stop (normal completion).</summary>
        public const string StopReasonStop = "stop";

        /// <summary>toolUse (the response ended with tool calls).</summary>
        public const string StopReasonToolUse = "toolUse";

        /// <summary>length (output token limit reached).</summary>
        public const string StopReasonLength = "length";

        /// <summary>error (provider or request error; see <see cref="ErrorMessage"/>).</summary>
        public const string StopReasonError = "error";

        /// <summary>aborted (the run was cancelled).</summary>
        public const string StopReasonAborted = "aborted";

        /// <summary>
        /// Message role.
        /// </summary>
        [JsonPropertyName("role")]
        public string? Role { get; set; } = null;

        /// <summary>
        /// Content blocks, or null.
        /// </summary>
        [JsonPropertyName("content")]
        [JsonConverter(typeof(PiContentListConverter))]
        public List<PiContentBlock>? Content { get; set; } = null;

        /// <summary>
        /// Provider id of an assistant message, or null.
        /// </summary>
        [JsonPropertyName("provider")]
        public string? Provider { get; set; } = null;

        /// <summary>
        /// Requested model id of an assistant message, or null.
        /// </summary>
        [JsonPropertyName("model")]
        public string? Model { get; set; } = null;

        /// <summary>
        /// Model the provider reported, or null.
        /// </summary>
        [JsonPropertyName("responseModel")]
        public string? ResponseModel { get; set; } = null;

        /// <summary>
        /// Usage of an assistant message, or null.
        /// </summary>
        [JsonPropertyName("usage")]
        public PiUsage? Usage { get; set; } = null;

        /// <summary>
        /// Stop reason of an assistant message (pending, stop, length, toolUse, error, aborted, deferred), or null.
        /// </summary>
        [JsonPropertyName("stopReason")]
        public string? StopReason { get; set; } = null;

        /// <summary>
        /// Error text of an assistant message that stopped with an error (for a provider HTTP error it starts with the
        /// status, for example <c>401 {...}</c>), or null.
        /// </summary>
        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; } = null;

        /// <summary>
        /// Tool call id of a toolResult message, or null.
        /// </summary>
        [JsonPropertyName("toolCallId")]
        public string? ToolCallId { get; set; } = null;

        /// <summary>
        /// Tool name of a toolResult message, or null.
        /// </summary>
        [JsonPropertyName("toolName")]
        public string? ToolName { get; set; } = null;

        /// <summary>
        /// Whether a toolResult message is an error, or null.
        /// </summary>
        [JsonPropertyName("isError")]
        public bool? IsError { get; set; } = null;

        /// <summary>
        /// True for an assistant message.
        /// </summary>
        [JsonIgnore]
        public bool IsAssistant => String.Equals(Role, RoleAssistant, StringComparison.Ordinal);

        /// <summary>
        /// True when an assistant message ended the run unsuccessfully: <c>error</c>, <c>aborted</c> or <c>length</c>.
        /// </summary>
        [JsonIgnore]
        public bool IsFailedStop => String.Equals(StopReason, StopReasonError, StringComparison.Ordinal)
            || String.Equals(StopReason, StopReasonAborted, StringComparison.Ordinal)
            || String.Equals(StopReason, StopReasonLength, StringComparison.Ordinal);

        /// <summary>
        /// The text blocks joined (thinking and tool calls excluded), or an empty string.
        /// </summary>
        [JsonIgnore]
        public string Text
        {
            get
            {
                if (Content == null) return String.Empty;
                StringBuilder sb = new StringBuilder();
                foreach (PiContentBlock block in Content)
                {
                    if (block == null || !String.Equals(block.Type, PiContentBlock.TypeText, StringComparison.Ordinal)) continue;
                    if (String.IsNullOrEmpty(block.Text)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(block.Text);
                }

                return sb.ToString();
            }
        }

        #endregion
    }
}
