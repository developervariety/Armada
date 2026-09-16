namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Posts a broadcast note to the coordination board, addressed to no single participant. A typed
    /// decision that reads a condition every operator session should see (for example a provider
    /// account fault) uses this seam. The poster never dispatches, lands, benches, or changes any other
    /// Armada record, and it is best-effort: an implementation must not throw into the caller.
    /// </summary>
    public interface IBoardNotePoster
    {
        /// <summary>
        /// Post one broadcast note. The note is informational and carries no authority.
        /// </summary>
        /// <param name="content">The note content. Empty content is ignored.</param>
        /// <param name="vesselId">The related vessel identifier, when known.</param>
        /// <param name="missionId">The related mission identifier, when known.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the post has been attempted.</returns>
        Task PostBroadcastAsync(string content, string? vesselId, string? missionId, CancellationToken token);
    }
}
