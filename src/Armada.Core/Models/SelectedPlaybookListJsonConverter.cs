namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Reads a playbook selection list written either as a JSON array or as the JSON-text string a stored
    /// record carries (for example <see cref="Persona.DefaultPlaybooks"/>), so a record read back and sent
    /// again is accepted. An empty string reads as an empty list. Writes a JSON array.
    /// </summary>
    public class SelectedPlaybookListJsonConverter : JsonConverter<List<SelectedPlaybook>?>
    {
        #region Public-Methods

        /// <inheritdoc />
        public override List<SelectedPlaybook>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType == JsonTokenType.String)
            {
                string text = reader.GetString() ?? "";
                if (String.IsNullOrWhiteSpace(text)) return new List<SelectedPlaybook>();
                return JsonSerializer.Deserialize<List<SelectedPlaybook>>(text, options) ?? new List<SelectedPlaybook>();
            }

            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a playbook selection array.");
            List<SelectedPlaybook> list = new List<SelectedPlaybook>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                SelectedPlaybook? entry = JsonSerializer.Deserialize<SelectedPlaybook>(ref reader, options);
                if (entry != null) list.Add(entry);
            }

            return list;
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, List<SelectedPlaybook>? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            foreach (SelectedPlaybook entry in value) JsonSerializer.Serialize(writer, entry, options);
            writer.WriteEndArray();
        }

        #endregion
    }
}
