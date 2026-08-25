namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;

    /// <summary>
    /// Normalises tool-call arguments against the tool's registered input schema before the
    /// handler deserialises them. Runs once, at the transport's dispatch seam, so every tool
    /// receives the same tolerance without carrying its own copy of the rule.
    /// <para>
    /// Model-driven clients routinely fill an optional parameter they mean to omit with an
    /// empty string, and send a boolean or number as its string spelling. The handlers
    /// deserialise into typed argument classes, where an empty string is not a valid
    /// nullable DateTime, enum, or boolean, so the whole call fails on a parameter the caller
    /// did not care about. The normaliser applies three rules, each keyed by the schema:
    /// an empty string for a property the schema does not list as required -- or for any
    /// non-string, enum-constrained, or formatted property -- is treated as omitted; a string
    /// spelling of a boolean or number for a boolean, integer, or number property is converted;
    /// everything else is passed through
    /// untouched. A value that cannot be converted is left as it was, so the handler's own
    /// error still names it.
    /// </para>
    /// </summary>
    public static class McpToolArgumentNormalizer
    {
        /// <summary>
        /// Returns the normalised arguments, or the input unchanged when there is nothing to do.
        /// </summary>
        /// <param name="arguments">The raw tool-call arguments, or null when the call carried none.</param>
        /// <param name="inputSchema">The tool's registered input schema (a JSON object schema), or null.</param>
        /// <returns>The normalised arguments.</returns>
        public static JsonElement? Normalize(JsonElement? arguments, JsonElement? inputSchema)
        {
            if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object) return arguments;
            if (!inputSchema.HasValue || inputSchema.Value.ValueKind != JsonValueKind.Object) return arguments;
            if (!inputSchema.Value.TryGetProperty("properties", out JsonElement properties)
                || properties.ValueKind != JsonValueKind.Object)
                return arguments;

            Dictionary<string, JsonElement> schemaByName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in properties.EnumerateObject())
                schemaByName[property.Name] = property.Value;

            HashSet<string> required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (inputSchema.Value.TryGetProperty("required", out JsonElement requiredList)
                && requiredList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in requiredList.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) continue;
                    string? name = entry.GetString();
                    if (!String.IsNullOrWhiteSpace(name)) required.Add(name);
                }
            }

            bool changed = false;
            List<KeyValuePair<string, JsonElement?>> normalized = new List<KeyValuePair<string, JsonElement?>>();
            foreach (JsonProperty argument in arguments.Value.EnumerateObject())
            {
                if (!schemaByName.TryGetValue(argument.Name, out JsonElement propertySchema))
                {
                    normalized.Add(new KeyValuePair<string, JsonElement?>(argument.Name, argument.Value));
                    continue;
                }

                JsonElement? value = NormalizeValue(argument.Value, propertySchema, required.Contains(argument.Name), out bool valueChanged);
                changed |= valueChanged;
                normalized.Add(new KeyValuePair<string, JsonElement?>(argument.Name, value));
            }

            if (!changed) return arguments;

            using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (KeyValuePair<string, JsonElement?> entry in normalized)
                    {
                        if (!entry.Value.HasValue) continue;
                        writer.WritePropertyName(entry.Key);
                        entry.Value.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }

                using (JsonDocument document = JsonDocument.Parse(stream.ToArray()))
                {
                    return document.RootElement.Clone();
                }
            }
        }

        /// <summary>
        /// Convenience overload for a schema held as an arbitrary object (anonymous type or element).
        /// </summary>
        /// <param name="arguments">The raw tool-call arguments.</param>
        /// <param name="inputSchema">The registered schema object.</param>
        /// <param name="options">Serializer options used to project a non-element schema.</param>
        /// <returns>The normalised arguments.</returns>
        public static JsonElement? Normalize(JsonElement? arguments, object? inputSchema, JsonSerializerOptions options)
        {
            if (inputSchema == null) return arguments;
            JsonElement schema = inputSchema is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(inputSchema, options);
            return Normalize(arguments, schema);
        }

        private static JsonElement? NormalizeValue(JsonElement value, JsonElement propertySchema, bool isRequired, out bool changed)
        {
            changed = false;
            string? type = ReadType(propertySchema);
            if (type == null) return value;

            bool isString = String.Equals(type, "string", StringComparison.OrdinalIgnoreCase);
            bool hasEnum = propertySchema.TryGetProperty("enum", out JsonElement enumList)
                && enumList.ValueKind == JsonValueKind.Array;
            bool hasFormat = propertySchema.TryGetProperty("format", out JsonElement format)
                && format.ValueKind == JsonValueKind.String
                && !String.IsNullOrWhiteSpace(format.GetString());

            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? String.Empty;

                if (text.Length == 0)
                {
                    if (!isRequired || !isString || hasEnum || hasFormat)
                    {
                        // An empty string for an OPTIONAL property means "omitted", whatever its type:
                        // no Armada tool gives an empty optional string a meaning of its own, and the
                        // clients that send one mean to leave it out. A required string keeps its
                        // empty value so the handler's own "is required" error still names it. A
                        // non-string, enum-constrained, or formatted value is dropped even when required,
                        // because "" can never deserialise into it.
                        changed = true;
                        return null;
                    }
                    return value;
                }

                if (String.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase))
                {
                    if (Boolean.TryParse(text.Trim(), out bool parsed))
                    {
                        changed = true;
                        return JsonSerializer.SerializeToElement(parsed);
                    }
                    return value;
                }

                if (String.Equals(type, "integer", StringComparison.OrdinalIgnoreCase))
                {
                    if (Int64.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                    {
                        changed = true;
                        return JsonSerializer.SerializeToElement(parsed);
                    }
                    return value;
                }

                if (String.Equals(type, "number", StringComparison.OrdinalIgnoreCase))
                {
                    if (Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        changed = true;
                        return JsonSerializer.SerializeToElement(parsed);
                    }
                    return value;
                }
            }

            return value;
        }

        private static string? ReadType(JsonElement propertySchema)
        {
            if (!propertySchema.TryGetProperty("type", out JsonElement typeElement)) return null;
            if (typeElement.ValueKind == JsonValueKind.String) return typeElement.GetString();
            if (typeElement.ValueKind == JsonValueKind.Array)
            {
                // ["string", "null"] style: the first non-null entry decides.
                foreach (JsonElement entry in typeElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) continue;
                    string? candidate = entry.GetString();
                    if (!String.Equals(candidate, "null", StringComparison.OrdinalIgnoreCase)) return candidate;
                }
            }
            return null;
        }
    }
}
