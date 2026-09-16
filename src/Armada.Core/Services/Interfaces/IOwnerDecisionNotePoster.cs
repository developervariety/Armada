namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Posts an owner-addressed note to the coordination board. A typed decision that surfaces a
    /// question only the owner can answer uses this seam to put the question in front of the owner.
    /// The poster never dispatches, lands, or changes any other Armada record, and it is best-effort:
    /// an implementation must not throw into the caller, so a board that is unreachable never blocks
    /// or breaks the decision that consulted it.
    /// </summary>
    public interface IOwnerDecisionNotePoster
    {
        /// <summary>
        /// Post one owner-addressed note. The content states the question and the default the owner
        /// can accept; the note is informational and carries no authority.
        /// </summary>
        /// <param name="content">The note content. Empty content is ignored.</param>
        /// <param name="vesselId">The related vessel identifier, when known.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the post has been attempted.</returns>
        Task PostOwnerDecisionAsync(string content, string? vesselId, CancellationToken token);
    }
}
