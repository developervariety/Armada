namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The deterministic keyword-and-topic leaf ranker. It is the retrieval floor: a fixed request
    /// over a fixed leaf set always yields the same order. No model call, no randomness, no clock.
    ///
    /// Scoring, per leaf, over the request's terms (from <see cref="ContextRetrievalRequest.Query"/>
    /// and each <see cref="ContextRetrievalRequest.Topics"/> entry):
    ///   * An exact topic id match (a requested topic equals the leaf topic) is the strongest signal.
    ///   * A term found in the leaf topic is weighted above the summary, read-when, and applies-to
    ///     metadata, which in turn outweigh a term found only in the body (a light match, capped so a
    ///     long body cannot dominate a precise metadata hit).
    /// Ties, and every leaf a request gives no signal, break by ordinal topic id, so the order is
    /// total and stable. A zero-signal leaf keeps its place at the end of the order; the service's
    /// byte budget, not the ranker, decides whether it is returned.
    /// </summary>
    public sealed class DefaultContextLeafRanker : IContextLeafRanker
    {
        #region Private-Members

        private const int ExactTopicWeight = 1000;
        private const int TopicTermWeight = 60;
        private const int SummaryTermWeight = 25;
        private const int ReadWhenTermWeight = 20;
        private const int AppliesToTermWeight = 8;
        private const int BodyTermWeight = 3;
        private const int BodyScoreCap = 30; // A long body cannot outweigh a precise metadata match.

        #endregion

        #region Public-Methods

        /// <summary>Rank the eligible leaves in descending relevance, ties broken by topic id.</summary>
        public IReadOnlyList<ContextChunk> Rank(ContextRetrievalRequest request, IReadOnlyList<ContextChunk> eligibleLeaves)
        {
            if (eligibleLeaves == null || eligibleLeaves.Count == 0) return Array.Empty<ContextChunk>();

            HashSet<string> requestedTopics = new HashSet<string>(
                (request?.Topics ?? new List<string>()).Where(t => !String.IsNullOrWhiteSpace(t)).Select(t => t.Trim()),
                StringComparer.OrdinalIgnoreCase);

            List<string> terms = BuildTerms(request);

            // Score once, then order by (score desc, topic asc). Scoring is pure, so the sort is stable
            // and deterministic.
            List<ScoredChunk> scored = new List<ScoredChunk>(eligibleLeaves.Count);
            foreach (ContextChunk leaf in eligibleLeaves)
            {
                scored.Add(new ScoredChunk(leaf, Score(leaf, requestedTopics, terms)));
            }

            scored.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                return String.CompareOrdinal(a.Chunk.Topic, b.Chunk.Topic);
            });

            return scored.Select(s => s.Chunk).ToList();
        }

        #endregion

        #region Private-Methods

        // The request terms: every whitespace-separated token of the query, plus each requested topic
        // split into its dotted segments, all lower-cased and de-duplicated. Very short tokens (one or
        // two characters) are dropped so a stop-fragment does not match everything.
        private static List<string> BuildTerms(ContextRetrievalRequest? request)
        {
            HashSet<string> terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (request != null)
            {
                if (!String.IsNullOrWhiteSpace(request.Query))
                {
                    foreach (string token in Tokenize(request.Query!)) terms.Add(token);
                }

                foreach (string topic in request.Topics ?? new List<string>())
                {
                    if (String.IsNullOrWhiteSpace(topic)) continue;
                    foreach (string token in Tokenize(topic)) terms.Add(token);
                }
            }

            List<string> ordered = terms.ToList();
            ordered.Sort(StringComparer.Ordinal); // Order does not affect the score, but keeps the term set stable.
            return ordered;
        }

        private static IEnumerable<string> Tokenize(string text)
        {
            foreach (string raw in text.Split(_Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim().ToLowerInvariant();
                if (token.Length >= 3) yield return token;
            }
        }

        private static readonly char[] _Separators = new[]
        {
            ' ', '\t', '\n', '\r', '.', ',', ';', ':', '/', '\\', '#', '(', ')', '[', ']', '{', '}',
            '"', '\'', '-', '_', '<', '>', '|', '=', '!', '?'
        };

        private static int Score(ContextChunk leaf, HashSet<string> requestedTopics, List<string> terms)
        {
            int score = 0;

            if (requestedTopics.Contains(leaf.Topic)) score += ExactTopicWeight;

            if (terms.Count == 0) return score;

            string topic = (leaf.Topic ?? String.Empty).ToLowerInvariant();
            string summary = (leaf.Summary ?? String.Empty).ToLowerInvariant();
            string readWhen = (leaf.ReadWhen ?? String.Empty).ToLowerInvariant();
            string appliesTo = String.Join(" ", leaf.AppliesTo ?? new List<string>()).ToLowerInvariant();
            string body = (leaf.Text ?? String.Empty).ToLowerInvariant();

            int bodyScore = 0;
            foreach (string term in terms)
            {
                if (topic.Contains(term, StringComparison.Ordinal)) score += TopicTermWeight;
                if (summary.Contains(term, StringComparison.Ordinal)) score += SummaryTermWeight;
                if (readWhen.Contains(term, StringComparison.Ordinal)) score += ReadWhenTermWeight;
                if (appliesTo.Contains(term, StringComparison.Ordinal)) score += AppliesToTermWeight;
                if (body.Contains(term, StringComparison.Ordinal)) bodyScore += BodyTermWeight;
            }

            score += Math.Min(bodyScore, BodyScoreCap);
            return score;
        }

        #endregion

        #region Private-Types

        private readonly struct ScoredChunk
        {
            public ScoredChunk(ContextChunk chunk, int score)
            {
                Chunk = chunk;
                Score = score;
            }

            public ContextChunk Chunk { get; }

            public int Score { get; }
        }

        #endregion
    }
}
