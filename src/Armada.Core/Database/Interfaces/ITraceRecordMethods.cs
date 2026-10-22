namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for trace records and the cursors the trace sweep resumes from.
    /// </summary>
    public interface ITraceRecordMethods
    {
        /// <summary>
        /// Store a trace record and its outgoing links, replacing the stored record of the same id.
        /// </summary>
        /// <param name="record">Record to store.</param>
        /// <param name="token">Cancellation token.</param>
        Task UpsertAsync(TraceRecord record, CancellationToken token = default);

        /// <summary>
        /// Read the trace record of one id.
        /// </summary>
        /// <param name="id">Described record id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The record, or null when none is stored.</returns>
        Task<TraceRecord?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate the ids of stored records that start with a prefix, such as the short form of an id.
        /// </summary>
        /// <param name="prefix">Id prefix.</param>
        /// <param name="limit">Maximum ids returned.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Matching ids, ordered.</returns>
        Task<List<string>> EnumerateIdsByPrefixAsync(string prefix, int limit, CancellationToken token = default);

        /// <summary>
        /// Enumerate the ids of records whose described record last changed before a cutoff.
        /// </summary>
        /// <param name="cutoffUtc">Cutoff.</param>
        /// <param name="limit">Maximum ids returned.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Ids, oldest first.</returns>
        Task<List<string>> EnumerateIdsLastUpdatedBeforeAsync(DateTime cutoffUtc, int limit, CancellationToken token = default);

        /// <summary>
        /// Delete the records of the given ids and their outgoing links.
        /// </summary>
        /// <param name="ids">Ids to delete.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rows deleted.</returns>
        Task<int> DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken token = default);

        /// <summary>
        /// Enumerate the stored records that link to a target id. Each returned link names the linking
        /// record (its kind and id) and the relation it declared.
        /// </summary>
        /// <param name="targetId">Linked-to id.</param>
        /// <param name="limit">Maximum links returned.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The linking records, ordered by id.</returns>
        Task<List<TraceLink>> EnumerateLinksToAsync(string targetId, int limit, CancellationToken token = default);

        /// <summary>
        /// Count stored records.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Row count.</returns>
        Task<long> CountAsync(CancellationToken token = default);

        /// <summary>
        /// Read a named cursor.
        /// </summary>
        /// <param name="name">Cursor name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The cursor value, or null when the cursor was never written.</returns>
        Task<string?> ReadCursorAsync(string name, CancellationToken token = default);

        /// <summary>
        /// Write a named cursor.
        /// </summary>
        /// <param name="name">Cursor name.</param>
        /// <param name="value">Cursor value.</param>
        /// <param name="token">Cancellation token.</param>
        Task WriteCursorAsync(string name, string value, CancellationToken token = default);
    }
}
