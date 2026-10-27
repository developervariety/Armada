namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>The authority or source class of a context result.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ContextSearchSourceEnum
    {
        /// <summary>External rule content, which working facts cannot override.</summary>
        Rule,
        /// <summary>Product documentation, separate from external rule authority.</summary>
        Documentation,
        /// <summary>A caller-visible native memory record.</summary>
        Memory,
        /// <summary>An indexed code chunk at the reported commit.</summary>
        Code
    }
}
