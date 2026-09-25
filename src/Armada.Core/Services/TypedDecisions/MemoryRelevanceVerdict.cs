namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// The <c>memory_relevance</c> verdict: which ranked memory leaves a brief delivers as reference
    /// material instead of read-first. The rule verdict moves none. A gated verdict moves the leaves the
    /// model is confident do not apply to the mission's work. Either way every leaf is still delivered in
    /// full; the verdict only changes the reading order.
    /// </summary>
    public readonly struct MemoryRelevanceVerdict
    {
        /// <summary>The leaf topics the decision was asked about, in retrieval order.</summary>
        public IReadOnlyList<string> Topics { get; }

        /// <summary>The leaf topics to deliver as reference material.</summary>
        public IReadOnlyCollection<string> ReferenceTopics { get; }

        private MemoryRelevanceVerdict(IReadOnlyList<string>? topics, IReadOnlyCollection<string>? referenceTopics)
        {
            Topics = topics ?? new List<string>();
            ReferenceTopics = referenceTopics ?? new List<string>();
        }

        /// <summary>A short label for the effective outcome.</summary>
        public string OutcomeLabel => ReferenceTopics.Count == 0
            ? "all_read_first"
            : "reference:" + ReferenceTopics.Count.ToString(CultureInfo.InvariantCulture);

        /// <summary>The rule verdict: every leaf is read-first.</summary>
        /// <param name="topics">The leaf topics, in retrieval order.</param>
        /// <returns>A verdict that moves no leaf.</returns>
        public static MemoryRelevanceVerdict AllReadFirst(IReadOnlyList<string> topics)
        {
            return new MemoryRelevanceVerdict(topics, null);
        }

        /// <summary>A verdict that moves the named leaves to reference material.</summary>
        /// <param name="topics">The leaf topics, in retrieval order.</param>
        /// <param name="referenceTopics">The topics to deliver as reference.</param>
        /// <returns>A verdict that moves the named leaves.</returns>
        public static MemoryRelevanceVerdict WithReference(IReadOnlyList<string> topics, IReadOnlyCollection<string> referenceTopics)
        {
            return new MemoryRelevanceVerdict(topics, referenceTopics);
        }
    }
}
