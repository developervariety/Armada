namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for token-usage capture and retrieval.
    /// </summary>
    public interface ITokenUsageMethods
    {
        /// <summary>
        /// Create one token-usage record.
        /// </summary>
        Task<TokenUsageRecord> CreateAsync(TokenUsageRecord record, CancellationToken token = default);

        /// <summary>
        /// Read one token-usage record by identifier with optional scope filters.
        /// </summary>
        Task<TokenUsageRecord?> ReadAsync(string id, TokenUsageQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate token-usage records using pagination and filters.
        /// </summary>
        Task<EnumerationResult<TokenUsageRecord>> EnumerateAsync(TokenUsageQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all matching records for summary aggregation.
        /// </summary>
        Task<List<TokenUsageRecord>> EnumerateForSummaryAsync(TokenUsageQuery query, CancellationToken token = default);

        /// <summary>
        /// Read every record of one source for the given source identifiers, at any creation time, within the tenant
        /// and user of <paramref name="scope"/>. Identifiers are bound in batches, so the cost is a few statements
        /// however many identifiers are given.
        /// </summary>
        /// <param name="scope">Supplies the tenant and user conditions; its other filters are not applied.</param>
        /// <param name="source">Record source, such as mission.</param>
        /// <param name="sourceIds">Source identifiers; blank and repeated identifiers are ignored.</param>
        /// <param name="token">Cancellation token.</param>
        Task<List<TokenUsageRecord>> EnumerateBySourceIdsAsync(TokenUsageQuery scope, string source, IReadOnlyCollection<string> sourceIds, CancellationToken token = default);

        /// <summary>
        /// Read the mission token-usage events created at or after the query start and before its end, within its tenant
        /// and user, newest first, in one statement. The summary folds in the events that have no table record.
        /// </summary>
        /// <param name="query">Window and tenant and user scope; its other filters are not applied.</param>
        /// <param name="token">Cancellation token.</param>
        Task<List<ArmadaEvent>> EnumerateLegacyEventsAsync(TokenUsageQuery query, CancellationToken token = default);

        /// <summary>
        /// Delete all token-usage records matching the supplied filters.
        /// </summary>
        Task<int> DeleteByFilterAsync(TokenUsageQuery query, CancellationToken token = default);
    }
}
