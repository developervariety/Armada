namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Service for managing the merge queue — test-before-merge with batch support.
    /// </summary>
    public interface IMergeQueueService
    {
        /// <summary>
        /// Enqueue a branch for testing and merging.
        /// </summary>
        /// <param name="entry">Merge entry to enqueue.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created merge entry.</returns>
        Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default);

        /// <summary>
        /// Process the next batch of queued entries: merge into integration branch, run tests, land if green.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        Task ProcessQueueAsync(CancellationToken token = default);

        /// <summary>
        /// Cancel a queued entry.
        /// </summary>
        /// <param name="entryId">Merge entry identifier.</param>
        /// <param name="token">Cancellation token.</param>
        Task CancelAsync(string entryId, CancellationToken token = default);

        /// <summary>
        /// List all entries in the queue.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of merge entries.</returns>
        Task<List<MergeEntry>> ListAsync(CancellationToken token = default);

        /// <summary>
        /// Process a single merge queue entry by ID.
        /// </summary>
        Task<MergeEntry?> ProcessSingleAsync(string entryId, CancellationToken token = default);

        /// <summary>
        /// Get a specific merge entry.
        /// </summary>
        /// <param name="entryId">Merge entry identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The merge entry or null.</returns>
        Task<MergeEntry?> GetAsync(string entryId, CancellationToken token = default);

        /// <summary>
        /// Permanently delete a merge entry from the database.
        /// Only entries in terminal states (Cancelled, Landed, Failed) can be deleted.
        /// </summary>
        /// <param name="entryId">Merge entry identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if deleted, false if not found or not in a terminal state.</returns>
        Task<bool> DeleteAsync(string entryId, CancellationToken token = default);
    }
}
