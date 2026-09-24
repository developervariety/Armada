namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// The one operator cancel of a merge entry. REST <c>POST /api/v1/merge-queue/{id}/cancel</c> (and the cancel branch
    /// of <c>DELETE /api/v1/merge-queue/{id}</c>), WebSocket <c>cancel_merge</c> and MCP <c>armada_cancel_merge</c> call it,
    /// so an unknown entry is refused, a finished entry keeps its outcome, and a cancel writes the same event.
    /// </summary>
    public sealed class MergeEntryCancellation
    {
        #region Public-Members

        /// <summary>
        /// Refusal code for an entry that does not exist in the caller's scope.
        /// </summary>
        public const string NotFoundCode = "merge_entry_not_found";

        /// <summary>
        /// Refusal code for an entry that already finished (Landed, Failed, Cancelled).
        /// </summary>
        public const string FinishedCode = "merge_entry_finished";

        #endregion

        #region Private-Members

        private readonly IMergeQueueService _Queue;
        private readonly OperationNotifier _Notifier;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="queue">Merge queue service.</param>
        /// <param name="notifier">Event sink.</param>
        public MergeEntryCancellation(IMergeQueueService queue, OperationNotifier? notifier)
        {
            _Queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _Notifier = notifier ?? OperationNotifier.None;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Cancel an active merge entry and write a <c>merge.cancelled</c> event.
        /// </summary>
        /// <param name="entryId">Merge entry identifier.</param>
        /// <param name="tenantId">Caller's tenant for a scoped read; null for a global administrator.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The cancelled entry, or the refusal.</returns>
        public async Task<MergeEntryCancellationResult> CancelAsync(string entryId, string? tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(entryId))
                return MergeEntryCancellationResult.Refused(null, NotFoundCode, "Merge entry not found");

            MergeEntry? entry = await _Queue.GetAsync(entryId, tenantId, token).ConfigureAwait(false);
            if (entry == null)
                return MergeEntryCancellationResult.Refused(null, NotFoundCode, "Merge entry not found");
            if (MergeStatusRules.IsTerminal(entry.Status))
            {
                return MergeEntryCancellationResult.Refused(entry, FinishedCode,
                    "Merge entry " + entry.Id + " already finished as " + entry.Status + " and keeps that outcome; only an active entry can be cancelled.");
            }

            await _Queue.CancelAsync(entry.Id, tenantId, token).ConfigureAwait(false);
            MergeEntry cancelled = await _Queue.GetAsync(entry.Id, tenantId, token).ConfigureAwait(false) ?? entry;
            await _Notifier.EmitAsync("merge.cancelled", "Merge entry " + cancelled.Id + " cancelled by operator",
                "merge_entry", cancelled.Id, null, cancelled.MissionId, cancelled.VesselId, null).ConfigureAwait(false);
            return MergeEntryCancellationResult.Cancelled(cancelled);
        }

        #endregion
    }
}
