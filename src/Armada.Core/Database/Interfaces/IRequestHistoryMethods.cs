using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database methods for HTTP request-history capture and retrieval.
    /// </summary>
    public interface IRequestHistoryMethods
    {
        /// <summary>
        /// Create one request-history record and optional detail row.
        /// </summary>
        Task<RequestHistoryRecord> CreateAsync(RequestHistoryEntry entry, RequestHistoryDetail? detail, CancellationToken token = default);

        /// <summary>
        /// Read one request-history record by identifier with optional scope filters.
        /// </summary>
        Task<RequestHistoryRecord?> ReadAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Enumerate request-history entries using pagination and filters.
        /// </summary>
        Task<EnumerationResult<RequestHistoryEntry>> EnumerateAsync(RequestHistoryQuery query, CancellationToken token = default);

        /// <summary>
        /// Enumerate all matching entries for summary aggregation.
        /// </summary>
        Task<List<RequestHistoryEntry>> EnumerateForSummaryAsync(RequestHistoryQuery query, CancellationToken token = default);

        /// <summary>
        /// Delete one request-history record by identifier with optional scope filters.
        /// </summary>
        Task DeleteAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default);

        /// <summary>
        /// Delete all request-history records matching the supplied filters.
        /// </summary>
        Task<int> DeleteByFilterAsync(RequestHistoryQuery query, CancellationToken token = default);
    }
}
