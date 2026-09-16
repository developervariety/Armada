namespace Armada.Core.Context.Census
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The context-retrieval coverage census. It produces objective numbers that gate whether the
    /// orchestrator and captain briefs can be slimmed onto the progressive-disclosure context system:
    /// it never renders an opinion, only measured recall and byte-reduction figures over the built
    /// <see cref="ContextIndex"/>.
    ///
    /// Four parts, each a pure function of its inputs so a fixed input yields a fixed report:
    ///   1. Safety recall — an INVARIANT (must be 100%): every retrieval result carries every
    ///      tier=core chunk and every domain-matched must_retrieve chunk. This is a guarantee the
    ///      retrieval service owns, checked here against representative requests, and pinned by a
    ///      registered unit test.
    ///   2. read_when recall — for each leaf that carries a read_when trigger, a query synthesized from
    ///      that trigger, run under the leaf's own applies_to scope, should surface the leaf within a
    ///      generous leaf budget. A miss names a leaf whose metadata is too weak to be found by its own
    ///      trigger.
    ///   3. Byte reduction — for a set of real task descriptions, the slimmed load (core plus the
    ///      retrieved leaves at a realistic budget) against the eager baseline the orchestrator pays
    ///      today.
    ///   4. Failure replay — for a set of real, hand-mapped past failures, whether the slimmed brief
    ///      for that task's domain would still carry the chunk whose rule was needed.
    ///
    /// The census only READS the index and the retrieval service; it writes nothing and calls no model.
    /// </summary>
    public static class ContextCoverageCensus
    {
        #region Part-1-Safety-Recall

        /// <summary>
        /// A representative request paired with the must_retrieve chunk topics the request MUST carry
        /// (computed by the caller, who knows the request's domain, so the census never re-derives the
        /// service's own domain-match rule).
        /// </summary>
        public sealed class SafetyRecallCase
        {
            /// <summary>A short label for the case, for the report only.</summary>
            public string Label { get; set; } = String.Empty;

            /// <summary>The retrieval request.</summary>
            public ContextRetrievalRequest Request { get; set; } = new ContextRetrievalRequest();

            /// <summary>The must_retrieve chunk topics this request's domain requires. May be empty.</summary>
            public List<string> ExpectedMustRetrieve { get; set; } = new List<string>();
        }

        /// <summary>The result of the safety-recall invariant check.</summary>
        public sealed class SafetyRecallReport
        {
            /// <summary>True only when every case returned every core chunk and every expected must_retrieve chunk.</summary>
            public bool Pass { get; set; }

            /// <summary>The number of tier=core chunks in the index (the count every case must return).</summary>
            public int CoreCount { get; set; }

            /// <summary>The number of representative cases checked.</summary>
            public int CasesChecked { get; set; }

            /// <summary>One human-readable line per invariant violation. Empty on a pass.</summary>
            public List<string> Failures { get; set; } = new List<string>();
        }

        /// <summary>
        /// Check the safety-recall invariant across the given cases. Builds one retrieval service over
        /// the chunk set and, for each case, asserts the result carries every tier=core chunk and every
        /// expected must_retrieve topic. A zero leaf budget in a case proves the core and the
        /// must_retrieve set are budget-exempt.
        /// </summary>
        public static SafetyRecallReport SafetyRecall(IReadOnlyList<ContextChunk> chunks, IReadOnlyList<SafetyRecallCase> cases)
        {
            List<ContextChunk> all = (chunks ?? new List<ContextChunk>()).Where(c => c != null).ToList();
            HashSet<string> coreTopics = new HashSet<string>(
                all.Where(c => c.Tier == ContextTierEnum.Core).Select(c => c.Topic), StringComparer.Ordinal);

            ContextRetrievalService service = new ContextRetrievalService(all);
            SafetyRecallReport report = new SafetyRecallReport
            {
                CoreCount = coreTopics.Count,
                CasesChecked = cases?.Count ?? 0
            };

            foreach (SafetyRecallCase c in cases ?? new List<SafetyRecallCase>())
            {
                ContextRetrievalResult result = service.Retrieve(c.Request);

                HashSet<string> returnedCore = new HashSet<string>(result.Core.Select(x => x.Topic), StringComparer.Ordinal);
                foreach (string core in coreTopics)
                {
                    if (!returnedCore.Contains(core))
                        report.Failures.Add(c.Label + ": missing core chunk '" + core + "'");
                }

                HashSet<string> forced = new HashSet<string>(result.MustRetrieve.Select(x => x.Topic), StringComparer.Ordinal);
                foreach (string need in c.ExpectedMustRetrieve ?? new List<string>())
                {
                    if (!forced.Contains(need))
                        report.Failures.Add(c.Label + ": missing must_retrieve chunk '" + need + "'");
                }
            }

            report.Pass = report.Failures.Count == 0;
            return report;
        }

        #endregion

        #region Part-2-ReadWhen-Recall

        /// <summary>One leaf that its own read_when-derived query failed to surface.</summary>
        public sealed class ReadWhenMiss
        {
            /// <summary>The leaf topic that was not recalled.</summary>
            public string Topic { get; set; } = String.Empty;

            /// <summary>The read_when trigger the query was synthesized from.</summary>
            public string ReadWhen { get; set; } = String.Empty;

            /// <summary>The applies_to scope the retrieval ran under.</summary>
            public string Scope { get; set; } = String.Empty;
        }

        /// <summary>The read_when recall report.</summary>
        public sealed class ReadWhenRecallReport
        {
            /// <summary>The number of leaves that carry a read_when trigger (the recall denominator).</summary>
            public int Total { get; set; }

            /// <summary>The number of those leaves surfaced by their own read_when query.</summary>
            public int Recalled { get; set; }

            /// <summary>Recall as a percentage; 100 when Total is zero.</summary>
            public double Percent => Total == 0 ? 100.0 : Math.Round(100.0 * Recalled / Total, 1);

            /// <summary>The generous leaf budget the recall ran under, in bytes.</summary>
            public int LeafBudgetBytes { get; set; }

            /// <summary>Every miss, ordered by topic.</summary>
            public List<ReadWhenMiss> Misses { get; set; } = new List<ReadWhenMiss>();
        }

        /// <summary>
        /// Measure read_when recall over every leaf carrying a read_when trigger. For each such leaf the
        /// query is the read_when text, the scope is the leaf's own applies_to (its persona and vessel
        /// tags), and the leaf is "recalled" when it appears in the retrieved leaves or the
        /// must_retrieve set within the generous budget. Deterministic: no model, no clock.
        /// </summary>
        public static ReadWhenRecallReport ReadWhenRecall(IReadOnlyList<ContextChunk> chunks, int leafBudgetBytes)
        {
            List<ContextChunk> all = (chunks ?? new List<ContextChunk>()).Where(c => c != null).ToList();
            ContextRetrievalService service = new ContextRetrievalService(all);

            List<ContextChunk> targets = all
                .Where(c => c.Tier == ContextTierEnum.Leaf && !String.IsNullOrWhiteSpace(c.ReadWhen))
                .OrderBy(c => c.Topic, StringComparer.Ordinal)
                .ToList();

            ReadWhenRecallReport report = new ReadWhenRecallReport
            {
                Total = targets.Count,
                LeafBudgetBytes = leafBudgetBytes
            };

            foreach (ContextChunk leaf in targets)
            {
                (string? persona, string? vessel) = ScopeOf(leaf);
                ContextRetrievalRequest request = new ContextRetrievalRequest
                {
                    Query = leaf.ReadWhen,
                    RequestingPersona = persona,
                    Vessel = vessel,
                    MaxLeafBytes = leafBudgetBytes
                };

                ContextRetrievalResult result = service.Retrieve(request);
                bool recalled = result.Leaves.Any(x => x.Topic == leaf.Topic)
                    || result.MustRetrieve.Any(x => x.Topic == leaf.Topic);

                if (recalled) report.Recalled++;
                else report.Misses.Add(new ReadWhenMiss
                {
                    Topic = leaf.Topic,
                    ReadWhen = leaf.ReadWhen,
                    Scope = String.Join(",", leaf.AppliesTo ?? new List<string>())
                });
            }

            return report;
        }

        // Derive a retrieval scope from a leaf's applies_to tags: the first persona: tag and the first
        // vessel: tag. A leaf tagged only "all" or "orchestrator" runs unscoped (an orchestrator
        // request), which is the widest candidate pool and so the hardest recall.
        private static (string? persona, string? vessel) ScopeOf(ContextChunk leaf)
        {
            string? persona = null;
            string? vessel = null;
            foreach (string tag in leaf.AppliesTo ?? new List<string>())
            {
                if (tag.StartsWith("persona:", StringComparison.OrdinalIgnoreCase) && persona == null)
                    persona = tag.Substring("persona:".Length).Trim();
                else if (tag.StartsWith("vessel:", StringComparison.OrdinalIgnoreCase) && vessel == null)
                    vessel = tag.Substring("vessel:".Length).Trim();
            }
            return (persona, vessel);
        }

        #endregion

        #region Part-3-Byte-Reduction

        /// <summary>One task's byte-reduction datum.</summary>
        public sealed class ByteReductionSample
        {
            /// <summary>The slimmed load in bytes: core plus must_retrieve plus the retrieved leaves.</summary>
            public int SlimmedBytes { get; set; }

            /// <summary>The reduction percentage against the eager baseline.</summary>
            public double ReductionPct { get; set; }

            /// <summary>The number of ranked leaves the retrieval returned within the budget.</summary>
            public int LeafCount { get; set; }
        }

        /// <summary>The byte-reduction distribution report.</summary>
        public sealed class ByteReductionReport
        {
            /// <summary>The number of task descriptions sampled.</summary>
            public int Samples { get; set; }

            /// <summary>The eager baseline: the always-on load an orchestrator reads today, in bytes.</summary>
            public long EagerBaselineBytes { get; set; }

            /// <summary>The core bundle chunk bytes (the fixed part of every slimmed load).</summary>
            public int CoreBytes { get; set; }

            /// <summary>The realistic leaf budget the retrieval ran under, in bytes.</summary>
            public int LeafBudgetBytes { get; set; }

            /// <summary>The smallest reduction percentage across the samples.</summary>
            public double MinPct { get; set; }

            /// <summary>The median reduction percentage.</summary>
            public double MedianPct { get; set; }

            /// <summary>The largest reduction percentage.</summary>
            public double MaxPct { get; set; }

            /// <summary>The median count of retrieved leaves across the samples.</summary>
            public int MedianLeafCount { get; set; }

            /// <summary>The per-sample data, in input order.</summary>
            public List<ByteReductionSample> Details { get; set; } = new List<ByteReductionSample>();
        }

        /// <summary>
        /// Compute the byte-reduction distribution over a set of real task descriptions. Each task runs
        /// retrieval as an orchestrator (no persona) with an optional detected vessel, at the realistic
        /// leaf budget, and its slimmed load (core plus must_retrieve plus retrieved leaves) is compared
        /// to the eager baseline.
        /// </summary>
        public static ByteReductionReport ByteReduction(
            ContextIndex index,
            long eagerBaselineBytes,
            IReadOnlyList<string> taskDescriptions,
            IReadOnlyList<string> knownVessels,
            int leafBudgetBytes)
        {
            List<ContextChunk> all = (index?.Chunks ?? new List<ContextChunk>()).Where(c => c != null).ToList();
            ContextRetrievalService service = new ContextRetrievalService(all);
            int coreBytes = all.Where(c => c.Tier == ContextTierEnum.Core).Sum(c => c.Bytes);

            ByteReductionReport report = new ByteReductionReport
            {
                EagerBaselineBytes = eagerBaselineBytes,
                CoreBytes = coreBytes,
                LeafBudgetBytes = leafBudgetBytes
            };

            foreach (string task in taskDescriptions ?? new List<string>())
            {
                if (String.IsNullOrWhiteSpace(task)) continue;

                string? vessel = DetectVessel(task, knownVessels);
                ContextRetrievalResult result = service.Retrieve(new ContextRetrievalRequest
                {
                    Query = task,
                    RequestingPersona = null,
                    Vessel = vessel,
                    MaxLeafBytes = leafBudgetBytes
                });

                int slimmed = result.TotalBytes;
                double reduction = eagerBaselineBytes <= 0
                    ? 0.0
                    : Math.Round(100.0 * (eagerBaselineBytes - slimmed) / eagerBaselineBytes, 1);

                report.Details.Add(new ByteReductionSample
                {
                    SlimmedBytes = slimmed,
                    ReductionPct = reduction,
                    LeafCount = result.Leaves.Count
                });
            }

            report.Samples = report.Details.Count;
            if (report.Samples > 0)
            {
                List<double> pcts = report.Details.Select(d => d.ReductionPct).OrderBy(x => x).ToList();
                List<int> leaves = report.Details.Select(d => d.LeafCount).OrderBy(x => x).ToList();
                report.MinPct = pcts.First();
                report.MaxPct = pcts.Last();
                report.MedianPct = Median(pcts);
                report.MedianLeafCount = (int)Math.Round(Median(leaves.Select(x => (double)x).ToList()));
            }

            return report;
        }

        // The vessel a task names, or null. First match wins; case-insensitive substring.
        private static string? DetectVessel(string task, IReadOnlyList<string> knownVessels)
        {
            if (String.IsNullOrWhiteSpace(task) || knownVessels == null) return null;
            foreach (string v in knownVessels)
            {
                if (!String.IsNullOrWhiteSpace(v) && task.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)
                    return v;
            }
            return null;
        }

        #endregion

        #region Part-4-Failure-Replay

        /// <summary>One hand-mapped past failure: the task domain, a query, and the chunk whose rule was needed.</summary>
        public sealed class FailureMapping
        {
            /// <summary>A short label for the report only.</summary>
            public string Label { get; set; } = String.Empty;

            /// <summary>The vessel the failed task was in, or null for an orchestrator-domain failure.</summary>
            public string? Vessel { get; set; }

            /// <summary>A query approximating the failed task, from its public keywords.</summary>
            public string Query { get; set; } = String.Empty;

            /// <summary>The chunk topic that encodes the rule the failure needed.</summary>
            public string NeededTopic { get; set; } = String.Empty;
        }

        /// <summary>The failure-replay report.</summary>
        public sealed class FailureReplayReport
        {
            /// <summary>The number of failures confidently mapped to a chunk.</summary>
            public int Mapped { get; set; }

            /// <summary>The number of mapped failures whose needed chunk the slimmed brief would NOT carry.</summary>
            public int Regressions { get; set; }

            /// <summary>The failures that could not be confidently mapped to a chunk.</summary>
            public int Unmapped { get; set; }

            /// <summary>One line per regression: the label and the topic that would be dropped.</summary>
            public List<string> RegressionDetails { get; set; } = new List<string>();
        }

        /// <summary>
        /// Replay hand-mapped failures against the slimmed brief. For each mapping, the needed chunk is
        /// carried when it is core, or when retrieval for the task's domain returns it (as a
        /// must_retrieve leaf or a ranked leaf within the realistic budget). A mapping whose needed
        /// chunk is neither core nor retrieved is a regression.
        /// </summary>
        public static FailureReplayReport FailureReplay(
            ContextIndex index,
            IReadOnlyList<FailureMapping> mappings,
            int unmappedCount,
            int leafBudgetBytes)
        {
            List<ContextChunk> all = (index?.Chunks ?? new List<ContextChunk>()).Where(c => c != null).ToList();
            ContextRetrievalService service = new ContextRetrievalService(all);
            HashSet<string> coreTopics = new HashSet<string>(
                all.Where(c => c.Tier == ContextTierEnum.Core).Select(c => c.Topic), StringComparer.Ordinal);

            FailureReplayReport report = new FailureReplayReport { Unmapped = unmappedCount };

            foreach (FailureMapping m in mappings ?? new List<FailureMapping>())
            {
                report.Mapped++;

                bool carried = coreTopics.Contains(m.NeededTopic);
                if (!carried)
                {
                    ContextRetrievalResult result = service.Retrieve(new ContextRetrievalRequest
                    {
                        Query = m.Query,
                        RequestingPersona = null,
                        Vessel = m.Vessel,
                        MaxLeafBytes = leafBudgetBytes
                    });
                    carried = result.MustRetrieve.Any(x => x.Topic == m.NeededTopic)
                        || result.Leaves.Any(x => x.Topic == m.NeededTopic);
                }

                if (!carried)
                {
                    report.Regressions++;
                    report.RegressionDetails.Add(m.Label + ": needed '" + m.NeededTopic + "' not carried for domain '" + (m.Vessel ?? "orchestrator") + "'");
                }
            }

            return report;
        }

        #endregion

        #region Helpers

        private static double Median(List<double> sorted)
        {
            if (sorted == null || sorted.Count == 0) return 0.0;
            int n = sorted.Count;
            if (n % 2 == 1) return sorted[n / 2];
            return Math.Round((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0, 1);
        }

        #endregion
    }
}
