namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    internal sealed class RuntimeLogEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("toolName")] public string? ToolName { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("toolCall")] public RuntimeLogBlock? ToolCall { get; set; }
        [JsonPropertyName("result")] public RuntimeLogResult? Result { get; set; }
        [JsonPropertyName("message")] public RuntimeLogBlock? Message { get; set; }
        [JsonPropertyName("part")] public RuntimeLogBlock? Part { get; set; }
        [JsonPropertyName("item")] public RuntimeLogBlock? Item { get; set; }
        [JsonPropertyName("content")] public RuntimeLogContent? Content { get; set; }
    }

    internal sealed class RuntimeLogResult
    {
        [JsonPropertyName("success")] public bool? Success { get; set; }
    }

    internal sealed class RuntimeLogBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("tool")] public string? Tool { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("thinking")] public string? Thinking { get; set; }
        [JsonPropertyName("command")] public string? Command { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("aggregated_output")] public string? AggregatedOutput { get; set; }
        [JsonPropertyName("output")] public string? Output { get; set; }
        [JsonPropertyName("is_error")] public bool? IsError { get; set; }
        [JsonPropertyName("state")] public RuntimeLogBlock? State { get; set; }
        [JsonPropertyName("content")] public RuntimeLogContent? Content { get; set; }
    }

    [JsonConverter(typeof(RuntimeLogContentConverter))]
    internal sealed class RuntimeLogContent
    {
        internal string? Text { get; set; }
        internal List<RuntimeLogBlock?>? Blocks { get; set; }
    }

    internal sealed class RuntimeLogContentConverter : JsonConverter<RuntimeLogContent>
    {
        public override RuntimeLogContent? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return new RuntimeLogContent { Text = reader.GetString() };
            if (reader.TokenType == JsonTokenType.StartArray)
                return new RuntimeLogContent { Blocks = JsonSerializer.Deserialize<List<RuntimeLogBlock?>>(ref reader, options) };
            throw new JsonException("Unsupported runtime content shape.");
        }

        public override void Write(Utf8JsonWriter writer, RuntimeLogContent value, JsonSerializerOptions options)
        {
            throw new NotSupportedException("Runtime events are read-only.");
        }
    }
}
