namespace Armada.Core.Memory
{
    /// <summary>
    /// Result of computing a bounded preview diff between the current canonical learned-facts
    /// content and a proposed MemoryConsolidator candidate.
    /// </summary>
    public sealed class CandidateDiffResult
    {
        /// <summary>
        /// Length of the proposed candidate content.
        /// </summary>
        public int ProposedContentLength { get; set; }

        /// <summary>
        /// Whether the candidate differs from the canonical content.
        /// </summary>
        public bool HasDiff { get; set; }

        /// <summary>
        /// Bounded unified-diff preview, or null when <see cref="HasDiff"/> is false.
        /// </summary>
        public string? Preview { get; set; }
    }
}
