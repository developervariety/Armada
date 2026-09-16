namespace Armada.Core.Context
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The generated machine index. One file maps every topic to its location and metadata. It is
    /// generated from the chunk front-matter and the tier configuration and is never hand-edited.
    /// The content is deterministic: it carries no timestamp and no host-absolute path, so two runs
    /// over the same input produce byte-identical output.
    /// </summary>
    public sealed class ContextIndexManifest
    {
        /// <summary>Manifest schema version.</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>A fixed description of the inputs the manifest is generated from.</summary>
        [JsonPropertyName("generated_from")]
        public string GeneratedFrom { get; set; } =
            "AI-Memory shared/, repos/, machine-notes/ and Armada docs/ front-matter, whole-file, and section chunks";

        /// <summary>Total chunk count.</summary>
        [JsonPropertyName("chunk_count")]
        public int ChunkCount { get; set; }

        /// <summary>The count of <c>tier: core</c> chunks. These ship inline, always, in the core bundle.</summary>
        [JsonPropertyName("core_count")]
        public int CoreCount { get; set; }

        /// <summary>The relative path of the derived core bundle beside this manifest.</summary>
        [JsonPropertyName("core_bundle_path")]
        public string CoreBundlePath { get; set; } = "context-core.md";

        /// <summary>Every chunk, ordered by topic id.</summary>
        [JsonPropertyName("chunks")]
        public List<ContextManifestEntry> Chunks { get; set; } = new List<ContextManifestEntry>();
    }
}
