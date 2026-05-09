namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Memory;
    using Armada.Core.Models;

    /// <summary>
    /// Reads the current learned memory state used to build reflection briefs.
    /// </summary>
    public interface IReflectionMemoryService
    {
        /// <summary>
        /// Read the current learned-facts playbook content for a vessel.
        /// </summary>
        /// <param name="vessel">Vessel whose learned playbook should be read.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Current learned playbook markdown, or an empty marker when none exists.</returns>
        Task<string> ReadLearnedPlaybookContentAsync(Vessel vessel, CancellationToken token = default);

        /// <summary>
        /// Read recently rejected reflection proposal notes for a vessel.
        /// </summary>
        /// <param name="vessel">Vessel whose rejected proposals should be read.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rejected proposal notes. Empty until rejection persistence is available.</returns>
        Task<List<string>> ReadRejectedProposalNotesAsync(Vessel vessel, CancellationToken token = default);

        /// <summary>
        /// Accept a MemoryConsolidator proposal: update the vessel learned playbook, advance LastReflectionMissionId,
        /// and record a <c>reflection.accepted</c> event.
        /// </summary>
        /// <param name="missionId">MemoryConsolidator mission id.</param>
        /// <param name="editsMarkdown">Optional operator override body; when set, skips parser and uses this markdown.</param>
        /// <param name="parser">Parser for <paramref name="missionId"/> AgentOutput when <paramref name="editsMarkdown"/> is absent.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Structured result with <see cref="ReflectionAcceptProposalResult.Error"/> set on failure.</returns>
        Task<ReflectionAcceptProposalResult> AcceptMemoryProposalAsync(
            string missionId,
            string? editsMarkdown,
            IReflectionOutputParser parser,
            CancellationToken token = default);

        /// <summary>
        /// Reject a MemoryConsolidator proposal: record a <c>reflection.rejected</c> event with the reason.
        /// Does not update the learned playbook or <c>Vessel.LastReflectionMissionId</c>.
        /// </summary>
        /// <param name="missionId">MemoryConsolidator mission id.</param>
        /// <param name="reason">Rejection reason fed into the next reflection brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Stable error code on failure; null on success.</returns>
        Task<string?> RejectMemoryProposalAsync(
            string missionId,
            string reason,
            CancellationToken token = default);
    }
}
