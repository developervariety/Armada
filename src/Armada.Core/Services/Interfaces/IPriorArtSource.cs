namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Read-only access to the four surfaces the deterministic prior-art retriever searches: the landed
    /// tip, unlanded mission branches, preserved and recovery refs, and open objectives. It is injected
    /// behind this interface so the retriever is a deterministic, fixture-fed unit — the production
    /// implementation shells out to git and reads the objective store, and a test implementation returns
    /// scripted hits. A search that cannot run (a missing repository, an unreachable ref) returns an
    /// empty list rather than throwing, so one dead surface never fails the whole retrieval.
    /// </summary>
    public interface IPriorArtSource
    {
        /// <summary>
        /// Search one surface for the supplied terms and return the hits. The retriever calls this once
        /// per <see cref="PriorArtWhereEnum"/>. A hit carries a <c>path:line</c> (or an objective id for
        /// the open-objective surface), the matched term, an excerpt, and the ref it lives on.
        /// </summary>
        /// <param name="context">The repository context to search.</param>
        /// <param name="where">Which surface to search.</param>
        /// <param name="terms">The search terms the retriever mined from the query.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The hits on that surface, or an empty list when the surface cannot be searched.</returns>
        Task<IReadOnlyList<PriorArtHit>> SearchAsync(
            PriorArtSearchContext context,
            PriorArtWhereEnum where,
            IReadOnlyList<string> terms,
            CancellationToken token);
    }

    /// <summary>
    /// The deterministic prior-art retriever: it mines search terms from the query, searches every
    /// surface through an <see cref="IPriorArtSource"/>, and assembles de-duplicated candidates capped
    /// at twelve and a total token budget. It is the "contextual" half of D26 <c>prior_art</c>; the
    /// model reasons only over what it returns.
    /// </summary>
    public interface IPriorArtRetriever
    {
        /// <summary>
        /// Mine terms from the query and assemble the capped, de-duplicated candidate set.
        /// </summary>
        /// <param name="query">The objective (and any extra text) and repository context to search.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The retrieval result, empty when no term or no hit was found.</returns>
        Task<PriorArtRetrieval> RetrieveAsync(PriorArtQuery query, CancellationToken token);
    }
}
