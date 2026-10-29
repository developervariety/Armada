namespace Armada.Core.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The <c>result</c> of a pi <c>tool_execution_end</c> event: content blocks the model sees as the tool result.
    /// </summary>
    public class PiToolResult
    {
        #region Public-Members

        /// <summary>
        /// Result content blocks, or null.
        /// </summary>
        [JsonPropertyName("content")]
        [JsonConverter(typeof(PiContentListConverter))]
        public List<PiContentBlock>? Content { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Text of a raw tool result: the joined text blocks of a result object, the string itself for a JSON string,
        /// or null when the result has no text.
        /// </summary>
        /// <param name="rawJson">Raw JSON of the result, or null.</param>
        /// <returns>The text, or null.</returns>
        public static string? TextOf(string? rawJson)
        {
            if (String.IsNullOrWhiteSpace(rawJson)) return null;
            string trimmed = rawJson!.Trim();
            try
            {
                if (trimmed[0] == '"') return JsonSerializer.Deserialize<string>(trimmed);
                if (trimmed[0] != '{') return trimmed;
                PiToolResult? result = JsonSerializer.Deserialize<PiToolResult>(trimmed);
                if (result?.Content == null) return null;
                StringBuilder sb = new StringBuilder();
                foreach (PiContentBlock block in result.Content)
                {
                    if (block == null || String.IsNullOrEmpty(block.Text)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(block.Text);
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        #endregion
    }
}
