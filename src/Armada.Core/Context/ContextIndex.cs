namespace Armada.Core.Context
{
    using System.Collections.Generic;

    /// <summary>
    /// The in-memory result of building the context index: the manifest, the ordered chunks, and the
    /// derived core bundle text. Pure data; produced by <see cref="ContextIndexGenerator.Build"/>
    /// without any file writes, so a caller (or a test) can inspect it before, or instead of, writing.
    /// </summary>
    public sealed class ContextIndex
    {
        /// <summary>The generated manifest (metadata only).</summary>
        public ContextIndexManifest Manifest { get; set; } = new ContextIndexManifest();

        /// <summary>Every chunk, ordered by topic id (the same order as <see cref="ContextIndexManifest.Chunks"/>).</summary>
        public List<ContextChunk> Chunks { get; set; } = new List<ContextChunk>();

        /// <summary>
        /// The derived core bundle: the concatenation of every <c>tier: core</c> chunk body, in a
        /// stable order, under a short header. This is the always-on text.
        /// </summary>
        public string CoreBundle { get; set; } = "";

        /// <summary>The manifest serialized to indented JSON, deterministic across runs.</summary>
        public string ManifestJson { get; set; } = "";
    }
}
