namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Context;
    using Armada.Core.Models;

    /// <summary>
    /// The <c>memory_relevance</c> decision input: the mission being briefed and the ranked memory leaves
    /// retrieved for it.
    /// </summary>
    public sealed class MemoryRelevanceDecisionInput
    {
        /// <summary>The mission being briefed, for the event owner scope and the vessel egress check.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The ranked memory leaves, in retrieval order.</summary>
        public IReadOnlyList<ContextChunk> Leaves { get; init; } = new List<ContextChunk>();
    }
}
