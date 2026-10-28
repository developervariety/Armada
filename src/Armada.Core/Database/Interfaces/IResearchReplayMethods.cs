namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Atomic full Research chain replay and immutable prior-attempt history.</summary>
    public interface IResearchReplayMethods
    {
        /// <summary>Read an exact committed request before acquiring new-work capacity.</summary>
        /// <param name="tenantId">Exact authenticated owner tenant.</param>
        /// <param name="userId">Exact authenticated owner user.</param>
        /// <param name="voyageId">Existing voyage.</param>
        /// <param name="request">Original idempotency key and expected state.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The prior receipt, or null when the key is unused.</returns>
        Task<ResearchReplayPlan?> ReadReceiptAsync(string tenantId, string userId, string voyageId,
            ResearchReplayRequest request, CancellationToken token = default);

        /// <summary>Read a preview, or archive and reset all six stages in one transaction.</summary>
        /// <param name="tenantId">Exact authenticated owner tenant.</param>
        /// <param name="userId">Exact authenticated owner user.</param>
        /// <param name="voyageId">Existing failed voyage.</param>
        /// <param name="request">Preview or fingerprint-bound idempotent replay request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Safe preview or committed receipt metadata.</returns>
        Task<ResearchReplayPlan> ExecuteAsync(string tenantId, string userId, string voyageId,
            ResearchReplayRequest request, CancellationToken token = default);

        /// <summary>Read an owned historical mission snapshot for the standard redacted output pager.</summary>
        /// <param name="tenantId">Exact owner tenant.</param>
        /// <param name="userId">Exact owner user.</param>
        /// <param name="replayId">Committed replay identity.</param>
        /// <param name="missionId">Original mission identity.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The immutable prior attempt, or null when it is not visible.</returns>
        Task<Mission?> ReadPriorMissionAsync(string tenantId, string userId, string replayId,
            string missionId, CancellationToken token = default);
    }
}
