namespace Armada.Core.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reads a pi message <c>content</c> value that is either a string (the system message has <c>""</c>) or an array
    /// of content blocks. A non-empty string becomes a single <c>text</c> block. Thread-safe (stateless).
    /// </summary>
    public class PiContentListConverter : JsonConverter<List<PiContentBlock>?>
    {
        #region Public-Members

        /// <inheritdoc />
        public override bool HandleNull => true;

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public override List<PiContentBlock>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType == JsonTokenType.String)
            {
                string? text = reader.GetString();
                List<PiContentBlock> blocks = new List<PiContentBlock>();
                if (!String.IsNullOrEmpty(text)) blocks.Add(new PiContentBlock { Type = PiContentBlock.TypeText, Text = text });
                return blocks;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("pi message content must be a string or an array of content blocks.");

            return JsonSerializer.Deserialize<List<PiContentBlock>>(ref reader, options);
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, List<PiContentBlock>? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else JsonSerializer.Serialize(writer, value, options);
        }

        #endregion
    }
}
