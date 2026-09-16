namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// A fake <see cref="IPriorArtSource"/> for the deterministic retriever tests. It returns scripted
    /// hits keyed by surface, records the terms it received, and can be told to throw on one surface so a
    /// test can prove the retriever skips a dead surface without failing the whole retrieval.
    /// </summary>
    public sealed class FakePriorArtSource : IPriorArtSource
    {
        private readonly Dictionary<PriorArtWhereEnum, List<PriorArtHit>> _HitsByWhere =
            new Dictionary<PriorArtWhereEnum, List<PriorArtHit>>();
        private readonly HashSet<PriorArtWhereEnum> _Throwing = new HashSet<PriorArtWhereEnum>();

        /// <summary>The terms the source was last asked to search for.</summary>
        public IReadOnlyList<string> LastTerms { get; private set; } = new List<string>();

        /// <summary>Script a surface to return the supplied hits.</summary>
        /// <param name="where">The surface.</param>
        /// <param name="hits">The hits it returns.</param>
        /// <returns>This, for chaining.</returns>
        public FakePriorArtSource With(PriorArtWhereEnum where, params PriorArtHit[] hits)
        {
            _HitsByWhere[where] = new List<PriorArtHit>(hits);
            return this;
        }

        /// <summary>Script a surface to throw, to prove the retriever skips it.</summary>
        /// <param name="where">The surface that throws.</param>
        /// <returns>This, for chaining.</returns>
        public FakePriorArtSource Throwing(PriorArtWhereEnum where)
        {
            _Throwing.Add(where);
            return this;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<PriorArtHit>> SearchAsync(
            PriorArtSearchContext context,
            PriorArtWhereEnum where,
            IReadOnlyList<string> terms,
            CancellationToken token)
        {
            LastTerms = terms;
            if (_Throwing.Contains(where)) throw new InvalidOperationException("scripted surface failure");
            if (_HitsByWhere.TryGetValue(where, out List<PriorArtHit>? hits))
                return Task.FromResult<IReadOnlyList<PriorArtHit>>(hits);
            return Task.FromResult<IReadOnlyList<PriorArtHit>>(new List<PriorArtHit>());
        }

        /// <summary>Build a hit on a surface.</summary>
        /// <param name="where">The surface.</param>
        /// <param name="location">The path:line or objective id.</param>
        /// <param name="term">The matched term.</param>
        /// <param name="excerpt">The excerpt.</param>
        /// <param name="refName">The ref, when any.</param>
        /// <returns>A hit.</returns>
        public static PriorArtHit Hit(PriorArtWhereEnum where, string location, string term, string excerpt = "", string? refName = null)
        {
            return new PriorArtHit { Where = where, Location = location, Term = term, Excerpt = excerpt, Ref = refName };
        }
    }

    /// <summary>
    /// A fake <see cref="IPriorArtRetriever"/> for the adapter tests. It returns a fixed retrieval on
    /// every call and records that it was called, so an adapter test can drive the model half without a
    /// real git tree.
    /// </summary>
    public sealed class FakePriorArtRetriever : IPriorArtRetriever
    {
        private readonly PriorArtRetrieval _Retrieval;

        /// <summary>How many times the retriever was called.</summary>
        public int Calls { get; private set; }

        /// <summary>Create a fake that returns the supplied retrieval.</summary>
        /// <param name="retrieval">The retrieval to return.</param>
        public FakePriorArtRetriever(PriorArtRetrieval retrieval)
        {
            _Retrieval = retrieval ?? PriorArtRetrieval.Empty();
        }

        /// <inheritdoc />
        public Task<PriorArtRetrieval> RetrieveAsync(PriorArtQuery query, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(_Retrieval);
        }

        /// <summary>Build a retrieval carrying the supplied candidates.</summary>
        /// <param name="candidates">The candidates.</param>
        /// <returns>A retrieval.</returns>
        public static PriorArtRetrieval RetrievalOf(params PriorArtCandidate[] candidates)
        {
            return new PriorArtRetrieval { Candidates = new List<PriorArtCandidate>(candidates), Terms = new List<string> { "SampleType" } };
        }

        /// <summary>Build a candidate.</summary>
        /// <param name="where">The surface.</param>
        /// <param name="location">The path:line or objective id.</param>
        /// <param name="excerpt">The excerpt.</param>
        /// <returns>A candidate.</returns>
        public static PriorArtCandidate Candidate(PriorArtWhereEnum where, string location, string excerpt = "excerpt")
        {
            return new PriorArtCandidate { Where = where, Location = location, Excerpt = excerpt, Terms = new List<string> { "SampleType" } };
        }
    }
}
