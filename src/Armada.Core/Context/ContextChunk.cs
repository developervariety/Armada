namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// One parsed context chunk: either a whole source file, or one section inside a source file.
    ///
    /// A chunk carries the retrieval metadata (from front-matter or, when a source has none, derived
    /// from the tier configuration and the source structure) plus the chunk body text. The body is
    /// used to build the derived core bundle; it is never written into the manifest.
    /// </summary>
    public sealed class ContextChunk
    {
        /// <summary>Stable unique id for this chunk. Equal to <see cref="Topic"/>.</summary>
        public string Id => Topic;

        /// <summary>A stable dotted topic id, unique across the manifest.</summary>
        public string Topic { get; set; } = String.Empty;

        /// <summary>
        /// The chunk's logical, root-relative location (for example <c>AI-Memory/shared/land-then-sync.md</c>
        /// or <c>docs/armada-ops.md#build-and-test</c>). Never a host-absolute path: an absolute path
        /// would leak the workstation or server layout into a committed or shipped artifact.
        /// </summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>One line describing what the chunk holds. From front-matter, config, or the leading heading.</summary>
        public string Summary { get; set; } = String.Empty;

        /// <summary>The plain-language trigger under which a reader needs this chunk. Empty when unknown.</summary>
        public string ReadWhen { get; set; } = String.Empty;

        /// <summary>
        /// Who the chunk applies to: <c>all</c>, <c>orchestrator</c>, a persona (<c>persona:Judge</c>),
        /// or a vessel (<c>vessel:ExampleVessel</c>). Defaults to <c>all</c> when unknown.
        /// </summary>
        public List<string> AppliesTo { get; set; } = new List<string>();

        /// <summary>The chunk's disclosure tier.</summary>
        public ContextTierEnum Tier { get; set; } = ContextTierEnum.Leaf;

        /// <summary>
        /// Optional domains for which retrieval must always include this leaf, even without a keyword
        /// match (safety-shaped, per-repository rules). Empty for a plain leaf and for core.
        /// </summary>
        public List<string> MustRetrieve { get; set; } = new List<string>();

        /// <summary>The chunk body text. Not serialized into the manifest; used to build the core bundle.</summary>
        public string Text { get; set; } = String.Empty;

        /// <summary>UTF-8 byte count of <see cref="Text"/>. Lets a caller budget before it fetches.</summary>
        public int Bytes => Encoding.UTF8.GetByteCount(Text ?? String.Empty);

        /// <summary>
        /// Deterministic ordering key for the core bundle. Lower sorts first; ties break by
        /// <see cref="Topic"/>. Derived from the owner allowlist order for core chunks; leaves keep
        /// the default and never enter the bundle.
        /// </summary>
        public int CoreOrder { get; set; } = int.MaxValue;
    }
}
