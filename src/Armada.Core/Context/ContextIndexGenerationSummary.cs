namespace Armada.Core.Context
{
    /// <summary>
    /// The outcome of generating and writing the context index. Returned to the startup caller so it
    /// can log one line. A generation that read no source is reported, not thrown: the artifact is
    /// additive and non-critical, so absence of a source root degrades quietly.
    /// </summary>
    public sealed class ContextIndexGenerationSummary
    {
        /// <summary>Whether the manifest and core bundle were written.</summary>
        public bool Written { get; set; }

        /// <summary>Total chunk count.</summary>
        public int ChunkCount { get; set; }

        /// <summary>Core chunk count.</summary>
        public int CoreCount { get; set; }

        /// <summary>UTF-8 byte size of the derived core bundle.</summary>
        public int CoreBundleBytes { get; set; }

        /// <summary>Sum of every chunk's body byte count.</summary>
        public int TotalChunkBytes { get; set; }

        /// <summary>The absolute path of the written manifest, or null when nothing was written.</summary>
        public string? ManifestPath { get; set; }

        /// <summary>The absolute path of the written core bundle, or null when nothing was written.</summary>
        public string? CoreBundlePath { get; set; }

        /// <summary>A short reason when generation was skipped or degraded; null on a clean full run.</summary>
        public string? Note { get; set; }
    }
}
