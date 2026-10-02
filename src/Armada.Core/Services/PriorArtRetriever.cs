namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// The deterministic prior-art retriever — the "contextual" half of D26 <c>prior_art</c>. It mines
    /// search terms from the query, searches every surface through an <see cref="IPriorArtSource"/>, and
    /// assembles de-duplicated candidates capped at twelve and a total token budget. It shells out to
    /// nothing itself: every tree read goes through the injected source, so this class is a
    /// deterministic, fixture-fed unit.
    ///
    /// The model reasons only over what this returns, so the caps matter: at most twelve candidates and
    /// a total of roughly twenty-four thousand tokens keep the state inside the egress limit, and each
    /// excerpt is trimmed to forty lines. When the two caps disagree the tighter one wins, and the
    /// result flags that it truncated.
    /// </summary>
    public sealed class PriorArtRetriever : IPriorArtRetriever
    {
        #region Private-Members

        private const int _MaxCandidates = 12;

        // The total token budget across all candidate excerpts and locations, approximated at four
        // characters per token. Twenty-four thousand tokens is the D26 cap; a candidate is admitted only
        // while the running total stays within it.
        private const int _MaxTotalTokens = 24000;
        private const int _CharsPerToken = 4;
        private const int _MaxExcerptLines = 40;

        // The surfaces are searched in this order, strongest evidence first: landed work is the most
        // important "already done", then an unlanded branch, then a preserved or recovery ref, then an
        // overlapping open objective. The order decides which candidates survive the twelve-cap.
        private static readonly PriorArtWhereEnum[] _SurfaceOrder =
        {
            PriorArtWhereEnum.Landed,
            PriorArtWhereEnum.UnlandedBranch,
            PriorArtWhereEnum.RecoverRef,
            PriorArtWhereEnum.OpenObjective
        };

        private readonly IPriorArtSource _Source;
        private readonly LoggingModule? _Logging;
        private const string _Header = "[PriorArtRetriever] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the retriever.
        /// </summary>
        /// <param name="source">The read-only source of hits on the four surfaces.</param>
        /// <param name="logging">Optional logging module.</param>
        public PriorArtRetriever(IPriorArtSource source, LoggingModule? logging = null)
        {
            _Source = source ?? throw new ArgumentNullException(nameof(source));
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<PriorArtRetrieval> RetrieveAsync(PriorArtQuery query, CancellationToken token)
        {
            if (query == null) return PriorArtRetrieval.Empty();

            IReadOnlyList<string> terms = PriorArtIdentifierExtractor.Extract(
                query.Title,
                query.Description,
                query.ExtraText,
                JoinCriteria(query.AcceptanceCriteria));

            if (terms.Count == 0) return PriorArtRetrieval.Empty(terms);

            // De-duplicate by (surface, location): the same landed line matched by two terms is one
            // candidate that carries both terms. A candidate keeps the first surface it was seen on.
            Dictionary<string, CandidateBuilder> builders = new Dictionary<string, CandidateBuilder>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            List<string> unsearched = new List<string>();

            foreach (PriorArtWhereEnum where in _SurfaceOrder)
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<PriorArtHit> hits;
                try
                {
                    hits = await _Source.SearchAsync(query.Context, where, terms, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One dead surface never fails the whole retrieval; the others still return. The
                    // failure travels with the result, so no answer reads an unsearched surface as empty.
                    _Logging?.Warn(_Header + "surface " + where + " search failed, skipped: " + ex.Message);
                    unsearched.Add(where + ": " + ex.Message);
                    continue;
                }

                foreach (PriorArtHit hit in hits ?? new List<PriorArtHit>())
                {
                    if (hit == null || String.IsNullOrWhiteSpace(hit.Location)) continue;
                    string key = ((int)hit.Where).ToString() + "\0" + hit.Location;
                    if (!builders.TryGetValue(key, out CandidateBuilder? builder))
                    {
                        builder = new CandidateBuilder(hit.Where, hit.Location, hit.Ref, TrimExcerpt(hit.Excerpt));
                        builders[key] = builder;
                        order.Add(key);
                    }
                    builder.AddTerm(hit.Term);
                }
            }

            List<PriorArtCandidate> candidates = new List<PriorArtCandidate>();
            int tokenTotal = 0;
            bool truncated = false;

            foreach (string key in order)
            {
                if (candidates.Count >= _MaxCandidates) { truncated = true; break; }
                CandidateBuilder builder = builders[key];
                int cost = EstimateTokens(builder.Excerpt) + EstimateTokens(builder.Location);
                if (candidates.Count > 0 && tokenTotal + cost > _MaxTotalTokens) { truncated = true; break; }
                tokenTotal += cost;
                candidates.Add(builder.Build());
            }

            return new PriorArtRetrieval
            {
                Candidates = candidates,
                Terms = terms,
                Truncated = truncated,
                SearchRan = true,
                UnsearchedSurfaces = unsearched
            };
        }

        #endregion

        #region Private-Methods

        private static string JoinCriteria(IReadOnlyList<string>? criteria)
        {
            if (criteria == null || criteria.Count == 0) return String.Empty;
            return String.Join("\n", criteria);
        }

        private static string TrimExcerpt(string? excerpt)
        {
            if (String.IsNullOrEmpty(excerpt)) return String.Empty;
            string[] lines = excerpt.Split('\n');
            if (lines.Length <= _MaxExcerptLines) return excerpt;
            return String.Join("\n", lines, 0, _MaxExcerptLines);
        }

        private static int EstimateTokens(string? text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            return (text.Length + _CharsPerToken - 1) / _CharsPerToken;
        }

        #endregion

        #region Private-Types

        private sealed class CandidateBuilder
        {
            private readonly List<string> _Terms = new List<string>();
            private readonly HashSet<string> _SeenTerms = new HashSet<string>(StringComparer.Ordinal);

            public CandidateBuilder(PriorArtWhereEnum where, string location, string? refName, string excerpt)
            {
                Where = where;
                Location = location;
                Ref = refName;
                Excerpt = excerpt;
            }

            public PriorArtWhereEnum Where { get; }
            public string Location { get; }
            public string? Ref { get; }
            public string Excerpt { get; }

            public void AddTerm(string? term)
            {
                if (String.IsNullOrWhiteSpace(term)) return;
                if (_SeenTerms.Add(term)) _Terms.Add(term);
            }

            public PriorArtCandidate Build()
            {
                return new PriorArtCandidate
                {
                    Where = Where,
                    Location = Location,
                    Ref = Ref,
                    Excerpt = Excerpt,
                    Terms = new List<string>(_Terms)
                };
            }
        }

        #endregion
    }
}
