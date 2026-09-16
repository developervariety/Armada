namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// The side-effect seam the D12 <c>followup_routing</c> adapter uses to give a Judge follow-up a
    /// home. The adapter decides the home from the model's typed answers; this router performs the one
    /// additive, non-destructive action for that home. Every method is best-effort and must not throw
    /// into the adapter: a router failure leaves the follow-up unrouted, never breaks capture.
    ///
    /// The router NEVER creates a voyage, dispatches, lands, or approves anything: a blocking follow-up
    /// is only flagged for the operator. A created objective is Triaged with auto-dispatch OFF.
    /// </summary>
    public interface IFollowUpRouter
    {
        /// <summary>
        /// Return the open objectives on the vessel that a follow-up might duplicate, most-recent
        /// first, so the adapter can offer their titles to the model's <c>same_as</c> question. The
        /// caller bounds the count.
        /// </summary>
        /// <param name="vesselId">The vessel to search, when known.</param>
        /// <param name="limit">Maximum candidates to return.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The candidate open objectives; never null.</returns>
        Task<IReadOnlyList<FollowUpDuplicateCandidate>> GetDuplicateCandidatesAsync(string? vesselId, int limit, CancellationToken token);

        /// <summary>
        /// Create a Triaged objective (auto-dispatch OFF) carrying the follow-up as real work to do.
        /// </summary>
        /// <param name="request">The routing request describing the follow-up.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created objective id, or null when creation was not possible.</returns>
        Task<string?> CreateTriagedObjectiveAsync(FollowUpRouteRequest request, CancellationToken token);

        /// <summary>
        /// Append an evidence line recording the follow-up as a note, without creating new work.
        /// </summary>
        /// <param name="request">The routing request describing the follow-up.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the evidence line has been appended (best-effort).</returns>
        Task AppendEvidenceNoteAsync(FollowUpRouteRequest request, CancellationToken token);

        /// <summary>
        /// Link the follow-up to an existing open objective it duplicates, instead of creating new work.
        /// </summary>
        /// <param name="request">The routing request describing the follow-up.</param>
        /// <param name="existingObjectiveId">The open objective the follow-up duplicates.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the link has been recorded (best-effort).</returns>
        Task LinkDuplicateAsync(FollowUpRouteRequest request, string existingObjectiveId, CancellationToken token);

        /// <summary>
        /// Flag a blocking follow-up for the operator. The router NEVER creates a voyage for it; a
        /// human decides. Typically an operator-addressed board note.
        /// </summary>
        /// <param name="request">The routing request describing the follow-up.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the operator has been flagged (best-effort).</returns>
        Task FlagBlockingForOperatorAsync(FollowUpRouteRequest request, CancellationToken token);
    }

    /// <summary>
    /// One open objective a follow-up might duplicate, offered to the model's title-similarity question.
    /// </summary>
    public sealed class FollowUpDuplicateCandidate
    {
        /// <summary>The objective id.</summary>
        public required string ObjectiveId { get; init; }

        /// <summary>The objective title, used for similarity.</summary>
        public required string Title { get; init; }
    }

    /// <summary>
    /// The follow-up context handed to the router for one item. Ids are for the router's own
    /// bookkeeping; text fields are already the source follow-up text.
    /// </summary>
    public sealed class FollowUpRouteRequest
    {
        /// <summary>The Judge follow-up record this item came from.</summary>
        public required JudgeFollowUp FollowUp { get; init; }

        /// <summary>The single follow-up item text.</summary>
        public required string ItemText { get; init; }

        /// <summary>The reviewed objective title, when known, for objective context.</summary>
        public string? ObjectiveTitle { get; init; }
    }
}
