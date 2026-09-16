namespace Armada.Core.Context
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// One entry in the generated context manifest. Mirrors <see cref="ContextChunk"/> metadata but
    /// carries no body text: the manifest is a machine index over locations and metadata, never a
    /// second copy of a rule. Field names are the design's snake_case names.
    /// </summary>
    public sealed class ContextManifestEntry
    {
        /// <summary>Stable unique id (equal to <see cref="Topic"/>).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>The stable dotted topic id.</summary>
        [JsonPropertyName("topic")]
        public string Topic { get; set; } = "";

        /// <summary>Logical, root-relative location, with an optional <c>#anchor</c> for a section.</summary>
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        /// <summary>One line describing what the chunk holds.</summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";

        /// <summary>The plain-language trigger under which a reader needs this chunk.</summary>
        [JsonPropertyName("read_when")]
        public string ReadWhen { get; set; } = "";

        /// <summary>Who the chunk applies to.</summary>
        [JsonPropertyName("applies_to")]
        public List<string> AppliesTo { get; set; } = new List<string>();

        /// <summary>The disclosure tier, serialized as <c>core</c> or <c>leaf</c>.</summary>
        [JsonPropertyName("tier")]
        public ContextTierEnum Tier { get; set; } = ContextTierEnum.Leaf;

        /// <summary>Domains for which retrieval must always include this leaf. Empty for most chunks.</summary>
        [JsonPropertyName("must_retrieve")]
        public List<string> MustRetrieve { get; set; } = new List<string>();

        /// <summary>UTF-8 byte count of the chunk body.</summary>
        [JsonPropertyName("bytes")]
        public int Bytes { get; set; }
    }
}
