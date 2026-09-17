namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Posts one coordination-board note that names the voyage it refers to. The voyage identifier
    /// is what carries the note into that voyage's next stage brief, which is the sanctioned
    /// handoff-time channel for an advisory correction. The poster changes no other Armada record,
    /// carries no authority, and is best-effort: an implementation must not throw into the caller.
    /// </summary>
    public interface IVoyageTaggedNotePoster
    {
        /// <summary>
        /// Post one note tagged with the supplied voyage.
        /// </summary>
        /// <param name="content">Note content. Empty content is ignored.</param>
        /// <param name="voyageId">Voyage the note refers to, when known.</param>
        /// <param name="missionId">Mission the note refers to, when known.</param>
        /// <param name="vesselId">Vessel the note refers to, when known.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the post has been attempted.</returns>
        Task PostVoyageNoteAsync(string content, string? voyageId, string? missionId, string? vesselId, CancellationToken token);
    }
}
