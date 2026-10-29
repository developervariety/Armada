namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// One content block of a pi message: <c>text</c>, <c>thinking</c>, <c>toolCall</c> (with free-form
    /// <c>arguments</c>), or <c>image</c>.
    /// </summary>
    public class PiContentBlock
    {
        #region Public-Members

        /// <summary>text.</summary>
        public const string TypeText = "text";

        /// <summary>thinking.</summary>
        public const string TypeThinking = "thinking";

        /// <summary>toolCall.</summary>
        public const string TypeToolCall = "toolCall";

        /// <summary>
        /// Block type.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; } = null;

        /// <summary>
        /// Text of a text block, or null.
        /// </summary>
        [JsonPropertyName("text")]
        public string? Text { get; set; } = null;

        /// <summary>
        /// Thinking text of a thinking block, or null.
        /// </summary>
        [JsonPropertyName("thinking")]
        public string? Thinking { get; set; } = null;

        /// <summary>
        /// Tool call id of a toolCall block, or null.
        /// </summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; } = null;

        /// <summary>
        /// Tool name of a toolCall block, or null.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; } = null;

        /// <summary>
        /// Tool arguments of a toolCall block as raw JSON, or null.
        /// </summary>
        [JsonPropertyName("arguments")]
        [JsonConverter(typeof(RawJsonStringConverter))]
        public string? Arguments { get; set; } = null;

        #endregion
    }
}
