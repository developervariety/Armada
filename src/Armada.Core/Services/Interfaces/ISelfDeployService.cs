namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Coordinates self-deploy after the self-hosted vessel lands to its default branch.
    /// </summary>
    public interface ISelfDeployService
    {
        /// <summary>
        /// Schedule a debounced self-deploy evaluation after a successful land.
        /// </summary>
        /// <param name="vesselId">Landed vessel id.</param>
        /// <param name="mergeEntryId">Merge entry that completed.</param>
        /// <param name="reason">Human-readable trigger reason for logs and events.</param>
        void ScheduleAfterLand(string? vesselId, string? mergeEntryId, string reason);

        /// <summary>
        /// Execute the self-deploy pipeline immediately. Intended for unit tests.
        /// </summary>
        /// <param name="vesselId">Landed vessel id.</param>
        /// <param name="mergeEntryId">Merge entry that completed.</param>
        /// <param name="reason">Trigger reason.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a supervised restart was requested.</returns>
        Task<bool> ExecuteAsync(
            string? vesselId,
            string? mergeEntryId,
            string reason,
            CancellationToken token = default);
    }
}
