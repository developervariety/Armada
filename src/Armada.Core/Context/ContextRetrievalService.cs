namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using SyslogLogging;

    /// <summary>
    /// The retrieval layer over the built context index. It answers a <see cref="ContextRetrievalRequest"/>
    /// with an ordered <see cref="ContextRetrievalResult"/>: every core chunk first, then the
    /// matching-domain <c>must_retrieve</c> safety leaves, then the ranked relevant leaves within the
    /// caller's byte budget.
    ///
    /// The service holds an immutable snapshot of the index, partitioned once at construction into
    /// core and leaves, so the always-on core can be returned even if every other step fails. The
    /// design's non-negotiables hold here: retrieval only ever WIDENS beyond the core; the core is
    /// never budget-limited, never filtered, and never dropped; a matching <c>must_retrieve</c> leaf
    /// is always included; and any error fails SAFE — every core chunk plus a conservative leaf
    /// superset, never zero core, never an exception into the caller.
    ///
    /// The leaf ranking is delegated to an injected <see cref="IContextLeafRanker"/> (deterministic by
    /// default) so a future typed relevance decision can re-order and widen the leaf set without
    /// touching this service or the core rule.
    /// </summary>
    public sealed class ContextRetrievalService
    {
        #region Private-Members

        private const string _Header = "[ContextRetrievalService] ";
        private readonly LoggingModule? _Logging;
        private readonly IContextLeafRanker _Ranker;

        // Immutable, pre-partitioned snapshots. Core is ordered by the bundle order; leaves by topic.
        private readonly List<ContextChunk> _Core;
        private readonly List<ContextChunk> _Leaves;
        private readonly List<ContextChunk> _MustRetrieveLeaves;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Build the service over the given chunks (the bodies-carrying chunks from
        /// <see cref="ContextIndexGenerator.Build"/>). The chunk list is snapshotted and partitioned;
        /// the caller's list is not retained. A null ranker uses the deterministic default.
        /// </summary>
        public ContextRetrievalService(IEnumerable<ContextChunk> chunks, IContextLeafRanker? ranker = null, LoggingModule? logging = null)
        {
            _Logging = logging;
            _Ranker = ranker ?? new DefaultContextLeafRanker();

            List<ContextChunk> all = (chunks ?? Enumerable.Empty<ContextChunk>()).Where(c => c != null).ToList();

            _Core = all.Where(c => c.Tier == ContextTierEnum.Core).ToList();
            _Core.Sort((a, b) =>
            {
                int byOrder = a.CoreOrder.CompareTo(b.CoreOrder);
                return byOrder != 0 ? byOrder : String.CompareOrdinal(a.Topic, b.Topic);
            });

            _Leaves = all.Where(c => c.Tier == ContextTierEnum.Leaf).ToList();
            _Leaves.Sort((a, b) => String.CompareOrdinal(a.Topic, b.Topic));

            _MustRetrieveLeaves = _Leaves.Where(c => c.MustRetrieve != null && c.MustRetrieve.Count > 0).ToList();
        }

        #endregion

        #region Public-Members

        /// <summary>The count of core chunks in the snapshot. Core always ships; this never changes.</summary>
        public int CoreCount => _Core.Count;

        /// <summary>The count of leaf chunks in the snapshot.</summary>
        public int LeafCount => _Leaves.Count;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Retrieve the ordered context for a request. Never throws: any failure returns the fail-safe
        /// result (every core chunk plus a conservative leaf superset), marked degraded.
        /// </summary>
        public ContextRetrievalResult Retrieve(ContextRetrievalRequest request)
        {
            if (request == null) return FailSafe(null, "null request");

            try
            {
                ContextRetrievalResult result = new ContextRetrievalResult
                {
                    // The core ships in full, first, never budget-limited. A shallow copy of the list
                    // protects the snapshot from a caller mutating the returned list.
                    Core = new List<ContextChunk>(_Core)
                };

                // 1. Every must_retrieve leaf whose domain matches the request. Never budget-limited.
                HashSet<string> mustRetrieveTopics = new HashSet<string>(StringComparer.Ordinal);
                foreach (ContextChunk leaf in _MustRetrieveLeaves)
                {
                    if (DomainMatches(leaf, request))
                    {
                        result.MustRetrieve.Add(leaf);
                        mustRetrieveTopics.Add(leaf.Topic);
                    }
                }
                result.MustRetrieve.Sort((a, b) => String.CompareOrdinal(a.Topic, b.Topic));

                // 2. The candidate leaves: every leaf not already force-included, filtered by applies_to.
                List<ContextChunk> candidates = new List<ContextChunk>();
                foreach (ContextChunk leaf in _Leaves)
                {
                    if (mustRetrieveTopics.Contains(leaf.Topic)) continue;
                    if (IsEligible(leaf, request)) candidates.Add(leaf);
                }

                // 3. Rank (deterministic floor, or an injected router that only widens/re-orders).
                IReadOnlyList<ContextChunk> ranked = _Ranker.Rank(request, candidates) ?? candidates;

                // 4. Fill the leaf budget in ranked order. Core and must_retrieve are exempt. A leaf
                //    that would exceed the remaining budget stops the fill, so a lower-ranked small leaf
                //    never jumps ahead of a higher-ranked one.
                int budget = Math.Max(0, request.MaxLeafBytes);
                int used = 0;
                foreach (ContextChunk leaf in ranked)
                {
                    int size = leaf.Bytes;
                    if (used + size > budget) break;
                    result.Leaves.Add(leaf);
                    used += size;
                }

                return result;
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "retrieval failed, returning fail-safe: " + ex.Message);
                return FailSafe(request, "retrieval error: " + ex.Message);
            }
        }

        #endregion

        #region Private-Methods

        // The fail-safe result: every core chunk (from the pre-partitioned snapshot, so no risky step
        // runs), every must_retrieve leaf regardless of domain, and a conservative superset of the
        // remaining leaves — filtered by applies_to when that can be computed, else EVERY leaf. Ordered
        // by topic id, never budget-limited. The core is never empty for a non-empty index.
        private ContextRetrievalResult FailSafe(ContextRetrievalRequest? request, string note)
        {
            ContextRetrievalResult result = new ContextRetrievalResult
            {
                Degraded = true,
                Note = note,
                Core = new List<ContextChunk>(_Core)
            };

            HashSet<string> mustRetrieveTopics = new HashSet<string>(StringComparer.Ordinal);
            foreach (ContextChunk leaf in _MustRetrieveLeaves)
            {
                result.MustRetrieve.Add(leaf);
                mustRetrieveTopics.Add(leaf.Topic);
            }
            result.MustRetrieve.Sort((a, b) => String.CompareOrdinal(a.Topic, b.Topic));

            foreach (ContextChunk leaf in _Leaves)
            {
                if (mustRetrieveTopics.Contains(leaf.Topic)) continue;
                bool eligible;
                try { eligible = request == null || IsEligible(leaf, request); }
                catch { eligible = true; } // Over-include on any error; the fail-safe never narrows.
                if (eligible) result.Leaves.Add(leaf);
            }
            result.Leaves.Sort((a, b) => String.CompareOrdinal(a.Topic, b.Topic));

            return result;
        }

        // A leaf is eligible for a request under its applies_to scope tags.
        //   * A leaf tagged "all" (or with no tags) is always eligible.
        //   * A persona-tagged leaf is eligible only for a request whose persona matches one of its
        //     persona tags; a request with no persona does not reach a persona-restricted leaf.
        //   * A vessel-tagged leaf is eligible only for a request whose vessel matches one of its
        //     vessel tags; a request with no vessel does not reach a vessel-restricted leaf.
        //   * A leaf tagged only "orchestrator" is eligible for a request with no persona (an
        //     orchestrator or unscoped request), and excluded for a persona (a captain) request.
        private static bool IsEligible(ContextChunk leaf, ContextRetrievalRequest request)
        {
            List<string> tags = leaf.AppliesTo ?? new List<string>();
            if (tags.Count == 0) return true;
            if (tags.Any(t => String.Equals(t, "all", StringComparison.OrdinalIgnoreCase))) return true;

            List<string> personaTags = tags
                .Where(t => t.StartsWith("persona:", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Substring("persona:".Length).Trim())
                .Where(t => t.Length > 0)
                .ToList();
            List<string> vesselTags = tags
                .Where(t => t.StartsWith("vessel:", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Substring("vessel:".Length).Trim())
                .Where(t => t.Length > 0)
                .ToList();
            bool hasOrchestrator = tags.Any(t => String.Equals(t, "orchestrator", StringComparison.OrdinalIgnoreCase));

            if (personaTags.Count > 0)
            {
                if (String.IsNullOrWhiteSpace(request.RequestingPersona)) return false;
                if (!personaTags.Any(p => String.Equals(p, request.RequestingPersona, StringComparison.OrdinalIgnoreCase))) return false;
            }

            if (vesselTags.Count > 0)
            {
                if (String.IsNullOrWhiteSpace(request.Vessel)) return false;
                if (!vesselTags.Any(v => String.Equals(v, request.Vessel, StringComparison.OrdinalIgnoreCase))) return false;
            }

            // An orchestrator-only leaf (no persona, no vessel tag) is for an orchestrator request.
            if (hasOrchestrator && personaTags.Count == 0 && vesselTags.Count == 0)
            {
                return String.IsNullOrWhiteSpace(request.RequestingPersona);
            }

            return true;
        }

        // A must_retrieve leaf's domain matches the request when any of its domain tokens names the
        // request's vessel, persona, or a requested topic, or appears as a term in the query. A domain
        // token may be bare ("ExampleVessel") or prefixed ("vessel:ExampleVessel", "persona:Judge",
        // "port:eculink"); the bare value after the prefix is what is matched.
        private static bool DomainMatches(ContextChunk leaf, ContextRetrievalRequest request)
        {
            List<string> domains = leaf.MustRetrieve ?? new List<string>();
            if (domains.Count == 0) return false;

            string? vessel = Normalize(request.Vessel);
            string? persona = Normalize(request.RequestingPersona);
            HashSet<string> topics = new HashSet<string>(
                (request.Topics ?? new List<string>()).Select(Normalize).Where(t => t != null)!,
                StringComparer.Ordinal);
            string query = (request.Query ?? String.Empty).ToLowerInvariant();

            foreach (string rawDomain in domains)
            {
                if (String.IsNullOrWhiteSpace(rawDomain)) continue;

                string full = Normalize(rawDomain)!;
                string bare = full;
                int colon = full.IndexOf(':');
                if (colon >= 0 && colon < full.Length - 1) bare = full.Substring(colon + 1).Trim();

                if (bare.Length == 0) continue;

                if (vessel != null && (bare == vessel || full == vessel)) return true;
                if (persona != null && (bare == persona || full == persona)) return true;
                if (topics.Contains(bare) || topics.Contains(full)) return true;
                if (query.Length > 0 && query.Contains(bare, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static string? Normalize(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            return value.Trim().ToLowerInvariant();
        }

        #endregion
    }
}
