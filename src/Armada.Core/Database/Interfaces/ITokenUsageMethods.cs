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
        /// Delete all token-usage records matching the supplied filters.
        /// </summary>
        Task<int> DeleteByFilterAsync(TokenUsageQuery query, CancellationToken token = default);
    }
}
