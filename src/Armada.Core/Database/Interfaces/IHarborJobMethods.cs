namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Durable operations for jobs launched on Harbor runners.</summary>
    public interface IHarborJobMethods
    {
        /// <summary>Store a new job record.</summary>
        /// <param name="record">Record to store.</param>
        /// <param name="token">Cancellation token.</param>
        Task CreateAsync(HarborJobRecord record, CancellationToken token = default);

        /// <summary>
        /// Replace a stored record only when the stored revision is lower than the record's revision, so a delayed
        /// write can never overwrite a newer state.
        /// </summary>
        /// <param name="record">Record carrying its new revision.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the stored record changed.</returns>
        Task<bool> TryUpdateAsync(HarborJobRecord record, CancellationToken token = default);

        /// <summary>Read one job record.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The record, or null when unknown.</returns>
        Task<HarborJobRecord?> ReadAsync(string jobId, CancellationToken token = default);

        /// <summary>Read job records newest first.</summary>
        /// <param name="query">Filter.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Matching records, at most the query limit.</returns>
        Task<List<HarborJobRecord>> EnumerateAsync(HarborJobQuery query, CancellationToken token = default);

        /// <summary>
        /// Mark every job that is not terminal as lost with a named reason. The Admiral calls this at start: a job
        /// from an earlier Admiral process has no link or mission process left to report for it.
        /// </summary>
        /// <param name="reason">Stable reason.</param>
        /// <param name="nowUtc">Time of the change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of jobs marked lost.</returns>
        Task<int> FailActiveAsync(string reason, DateTime nowUtc, CancellationToken token = default);
    }
}
