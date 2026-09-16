namespace Armada.Core.Context
{
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The ordered result of a context retrieval. The three chunk sets are disjoint: a chunk appears
    /// in exactly one of <see cref="Core"/>, <see cref="MustRetrieve"/>, or <see cref="Leaves"/>.
    ///
    /// The contract, in priority order: <see cref="Core"/> holds every <c>tier: core</c> chunk and
    /// always ships first, never budget-limited; <see cref="MustRetrieve"/> holds every leaf whose
    /// <c>must_retrieve</c> domain matches the request, also never budget-limited; <see cref="Leaves"/>
    /// holds the ranked, relevant leaves that fit within the request's leaf budget.
    /// </summary>
    public sealed class ContextRetrievalResult
    {
        /// <summary>Every <c>tier: core</c> chunk, in the deterministic core-bundle order. Always present.</summary>
        public List<ContextChunk> Core { get; set; } = new List<ContextChunk>();

        /// <summary>Every leaf whose <c>must_retrieve</c> domain matches the request. Not budget-limited.</summary>
        public List<ContextChunk> MustRetrieve { get; set; } = new List<ContextChunk>();

        /// <summary>The ranked, relevant leaves that fit within <see cref="ContextRetrievalRequest.MaxLeafBytes"/>.</summary>
        public List<ContextChunk> Leaves { get; set; } = new List<ContextChunk>();

        /// <summary>
        /// True when retrieval fell back to its fail-safe path (a manifest, ranking, or filter error).
        /// The result still holds every core chunk and a conservative leaf superset; a caller may log
        /// it, but must never treat a degraded result as "no context".
        /// </summary>
        public bool Degraded { get; set; }

        /// <summary>A short reason when <see cref="Degraded"/> is true; null on a clean run.</summary>
        public string? Note { get; set; }

        /// <summary>The total UTF-8 byte count of every returned chunk (core, must-retrieve, and leaves).</summary>
        public int TotalBytes =>
            Core.Sum(c => c.Bytes) + MustRetrieve.Sum(c => c.Bytes) + Leaves.Sum(c => c.Bytes);

        /// <summary>The byte count of the ranked leaf set only (what the leaf budget bounds).</summary>
        public int LeafBytes => Leaves.Sum(c => c.Bytes);
    }
}
