namespace Armada.Core.Context
{
    using System.Collections.Generic;

    /// <summary>
    /// Ranks the eligible leaf chunks for a retrieval request. This is the seam a future typed
    /// relevance decision (Jev, decision <c>context_route</c>) plugs into: it may re-order the leaves
    /// and widen the set the deterministic floor returns, but it is layered ON TOP of the deterministic
    /// ranker and can only add or re-order, never subtract a floor leaf and never touch core.
    ///
    /// The ranker returns the FULL ordered candidate list; the service applies the byte budget. So a
    /// ranker widening the set never has to know the budget, and the deterministic default gives a
    /// fixed order for a fixed input.
    /// </summary>
    public interface IContextLeafRanker
    {
        /// <summary>
        /// Return the eligible leaves in descending relevance order. The input is already filtered by
        /// <c>applies_to</c> and excludes the matching-domain <c>must_retrieve</c> leaves. The output
        /// is the same set, ordered; an implementation must be deterministic for a fixed input and
        /// must not drop a chunk it was given.
        /// </summary>
        IReadOnlyList<ContextChunk> Rank(ContextRetrievalRequest request, IReadOnlyList<ContextChunk> eligibleLeaves);
    }
}
