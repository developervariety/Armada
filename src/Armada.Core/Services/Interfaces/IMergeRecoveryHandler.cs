namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Admiral-side handler that fires when a merge-queue entry transitions to Failed.
    /// Reads the persisted classification, consults the recovery router, and executes
    /// the chosen terminal action (redispatch, rebase-captain, or surface).
    /// </summary>
    public interface IMergeRecoveryHandler
    {
        /// <summary>
        /// Handle the failed merge-queue entry.
        /// </summary>
        /// <param name="mergeEntryId">Identifier of the failed merge-queue entry.</param>
        /// <param name="token">Cancellation token.</param>
        Task OnMergeFailedAsync(string mergeEntryId, CancellationToken token = default);
    }
}
