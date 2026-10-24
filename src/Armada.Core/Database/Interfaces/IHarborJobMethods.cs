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

        /// <summary>Replace a record only at the exact expected revision, advancing it by one.</summary>
        /// <param name="record">Record with the next revision.</param>
        /// <param name="expectedRevision">Exact stored revision.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the exact revision fence won.</returns>
        Task<bool> TryUpdateAtRevisionAsync(HarborJobRecord record, long expectedRevision, CancellationToken token = default);

        /// <summary>Acknowledge completed lifecycle delivery only at the exact stored terminal revision.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="expectedRevision">Revision delivered to the lifecycle handler.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this delivery acknowledgment won.</returns>
        Task<bool> TryAcknowledgeCompletionDeliveredAsync(string jobId, long expectedRevision, CancellationToken token = default);

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

        /// <summary>Persist the immutable continuation intent for one confirmed Harbor completion.</summary>
        /// <param name="intent">Computed outcome and stable audit identifiers.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this job had no earlier intent.</returns>
        Task<bool> TryCreateCompletionIntentAsync(HarborCompletionIntent intent, CancellationToken token = default);

        /// <summary>Read a previously stored Harbor completion continuation intent.</summary>
        /// <param name="jobId">Durable Harbor job identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The intent, or null when none exists.</returns>
        Task<HarborCompletionIntent?> ReadCompletionIntentAsync(string jobId, CancellationToken token = default);

        /// <summary>Mark a stored Harbor completion intent complete. Repeating this operation is safe.</summary>
        /// <param name="jobId">Durable Harbor job identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the intent exists and is complete after this call.</returns>
        Task<bool> TryCompleteCompletionIntentAsync(string jobId, CancellationToken token = default);

        /// <summary>
        /// Mark active jobs as lost with a named reason. This does not confirm that their remote processes stopped.
        /// </summary>
        /// <param name="reason">Stable reason.</param>
        /// <param name="nowUtc">Time of the change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of jobs marked lost.</returns>
        Task<int> FailActiveAsync(string reason, DateTime nowUtc, CancellationToken token = default);
    }
}
