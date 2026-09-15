namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Discards the Pending armed Checks of a voyage that ended before they ran. The executor never runs a
    /// Check whose voyage is Cancelled or Failed, so without this rule the record stays Pending forever and
    /// is counted as a required pending Check. Every path that ends a voyage calls this one rule.
    /// </summary>
    public static class VoyageCheckDiscard
    {
        #region Public-Members

        /// <summary>
        /// Reason recorded on a Pending Check whose voyage was cancelled.
        /// </summary>
        public const string VoyageCancelledReason = "voyage_cancelled";

        /// <summary>
        /// Reason recorded on a Pending Check whose voyage failed.
        /// </summary>
        public const string VoyageFailedReason = "voyage_failed";

        #endregion

        #region Private-Members

        private const int _MaxPasses = 50;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns the discard reason for a voyage status, or null when the voyage has not ended in a way
        /// that strands its Pending Checks.
        /// </summary>
        /// <param name="status">Voyage status.</param>
        /// <returns>The reason, or null.</returns>
        public static string? ReasonFor(VoyageStatusEnum status)
        {
            if (status == VoyageStatusEnum.Cancelled) return VoyageCancelledReason;
            if (status == VoyageStatusEnum.Failed) return VoyageFailedReason;
            return null;
        }

        /// <summary>
        /// Mark every Pending Check of <paramref name="voyageId"/> Canceled with <paramref name="reason"/>.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage whose Checks are discarded.</param>
        /// <param name="reason">Named reason recorded in each Check's summary.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of Checks discarded.</returns>
        public static async Task<int> DiscardPendingAsync(DatabaseDriver database, string voyageId, string reason, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));

            int discarded = 0;
            for (int pass = 0; pass < _MaxPasses; pass++)
            {
                // Always page one: each discard removes the record from the Pending filter.
                EnumerationResult<CheckRun> page = await database.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    VoyageId = voyageId,
                    Status = CheckRunStatusEnum.Pending,
                    PageNumber = 1,
                    PageSize = 200
                }, token).ConfigureAwait(false);
                if (page.Objects.Count == 0) break;

                foreach (CheckRun run in page.Objects)
                {
                    await DiscardAsync(database, run, reason, token).ConfigureAwait(false);
                    discarded++;
                }
            }

            return discarded;
        }

        /// <summary>
        /// Mark one Pending Check Canceled with <paramref name="reason"/>. A Check in any other state is left as it is.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="run">Check to discard.</param>
        /// <param name="reason">Named reason recorded in the Check's summary.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the Check was discarded.</returns>
        public static async Task<bool> DiscardAsync(DatabaseDriver database, CheckRun run, string reason, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (run.Status != CheckRunStatusEnum.Pending) return false;

            DateTime now = DateTime.UtcNow;
            run.Status = CheckRunStatusEnum.Canceled;
            run.Summary = reason + ": the voyage ended before this Check ran.";
            run.CompletedUtc = now;
            run.LastUpdateUtc = now;
            await database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            return true;
        }

        #endregion
    }
}
