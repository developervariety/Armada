namespace Armada.Runtimes
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reads a JSON string as text and any other JSON value (a number, an array, an object, true, false) as absent.
    /// Runtimes put it on the detail fields of their event models: a provider may send a tool argument of any
    /// shape, and a strict text field would fail the whole event, so the runtime would write the raw JSON line,
    /// tool output included, into the mission log and hide any protocol marker the same event carried.
    /// </summary>
    internal sealed class LenientStringConverter : JsonConverter<string?>
    {
        /// <inheritdoc />
        public override bool HandleNull => true;

        /// <inheritdoc />
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString();

            reader.Skip();
            return null;
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }
    }
}
