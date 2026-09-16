namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A request to the <see cref="ContextRetrievalService"/>. It carries the caller's task text
    /// and/or an explicit set of topics, the caller's scope (persona and vessel), and the leaf budget.
    ///
    /// The request never widens the core: every <c>tier: core</c> chunk ships regardless of what this
    /// request holds. The request only shapes the LEAF set: which leaves are eligible
    /// (<see cref="RequestingPersona"/> and <see cref="Vessel"/>), which are ranked highest
    /// (<see cref="Query"/> and <see cref="Topics"/>), and how many fit (<see cref="MaxLeafBytes"/>).
    /// </summary>
    public sealed class ContextRetrievalRequest
    {
        /// <summary>Free task text to rank leaves against. May be null or empty.</summary>
        public string? Query { get; set; }

        /// <summary>Explicit topic ids the caller wants. An exact topic match ranks highest. May be empty.</summary>
        public List<string> Topics { get; set; } = new List<string>();

        /// <summary>
        /// The requesting persona (for example <c>Judge</c> or <c>Worker</c>), or null for an
        /// orchestrator or unscoped request. A persona-scoped request excludes leaves whose
        /// <c>applies_to</c> names a different persona; a leaf tagged <c>all</c> is always eligible.
        /// </summary>
        public string? RequestingPersona { get; set; }

        /// <summary>
        /// The vessel the task is in (for example <c>ExampleVessel</c>), or null. A vessel-scoped
        /// request excludes leaves whose <c>applies_to</c> names a different vessel, and pulls that
        /// vessel's <c>must_retrieve</c> safety leaves.
        /// </summary>
        public string? Vessel { get; set; }

        /// <summary>
        /// The byte budget for the ranked leaf set. Core chunks and matching-domain
        /// <c>must_retrieve</c> leaves are exempt and never counted against it. A value below zero is
        /// treated as zero.
        /// </summary>
        public int MaxLeafBytes { get; set; }
    }
}
