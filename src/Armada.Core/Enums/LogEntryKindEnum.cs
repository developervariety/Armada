namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Observed runtime event category, not a mission outcome.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LogEntryKindEnum
    {
        /// <summary>Assistant or unstructured display text.</summary>
        Text,
        /// <summary>Runtime reasoning text.</summary>
        Thinking,
        /// <summary>A proposed or running tool operation.</summary>
        ToolCall,
        /// <summary>A reported tool result.</summary>
        ToolResult,
        /// <summary>A runtime status event.</summary>
        Status,
        /// <summary>Compatibility projection containing more than one category.</summary>
        Mixed
    }
}
