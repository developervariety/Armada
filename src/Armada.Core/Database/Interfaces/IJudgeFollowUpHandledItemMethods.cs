namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for the markers that record a Judge follow-up item was given a home.
    /// </summary>
    public interface IJudgeFollowUpHandledItemMethods
    {
        /// <summary>
        /// Store a marker. Returns false and stores nothing when the item already has a marker.
        /// </summary>
        /// <param name="item">Marker to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the marker was stored.</returns>
        Task<bool> CreateAsync(JudgeFollowUpHandledItem item, CancellationToken token = default);

        /// <summary>
        /// Replace the marker of one item, storing it when the item has none.
        /// </summary>
        /// <param name="item">Marker to store.</param>
        /// <param name="token">Cancellation token.</param>
        Task ReplaceAsync(JudgeFollowUpHandledItem item, CancellationToken token = default);

        /// <summary>
        /// Read the marker of one item.
        /// </summary>
        /// <param name="followUpId">Judge follow-up id.</param>
        /// <param name="itemNumber">One-based item number.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The marker, or null when the item is open.</returns>
        Task<JudgeFollowUpHandledItem?> ReadAsync(string followUpId, int itemNumber, CancellationToken token = default);

        /// <summary>
        /// Enumerate the markers of the given follow-ups.
        /// </summary>
        /// <param name="followUpIds">Judge follow-up ids.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The markers, ordered by follow-up id and item number.</returns>
        Task<List<JudgeFollowUpHandledItem>> EnumerateForFollowUpsAsync(IReadOnlyCollection<string> followUpIds, CancellationToken token = default);
    }
}
