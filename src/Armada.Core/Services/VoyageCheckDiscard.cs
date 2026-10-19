namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Discards the Pending armed Checks of a voyage that ended before they ran. The executor never runs a
    /// Check whose voyage is Cancelled or Failed, so without this rule the record stays Pending forever and
    /// is counted as a required pending Check. Every path that ends a voyage calls this one rule. A discard
    /// writes its reason into the Check's Summary and Output and records a <see cref="DiscardedEventType"/>
    /// event. A Check an operator is executing (<see cref="CheckRunService.IsOperatorRun"/>) is not
    /// discarded: the operator asked for that run after the voyage ended.
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

        /// <summary>
        /// Event type recorded for every Check discarded before it ran.
        /// </summary>
        public const string DiscardedEventType = "check.discarded";

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
                // Always page one: each discard removes the record from the Pending filter. A pass that
                // discards nothing (every remaining record is an operator run) ends the loop.
                EnumerationResult<CheckRun> page = await database.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    VoyageId = voyageId,
                    Status = CheckRunStatusEnum.Pending,
                    PageNumber = 1,
                    PageSize = 200
                }, token).ConfigureAwait(false);
                if (page.Objects.Count == 0) break;

                int passDiscarded = 0;
                foreach (CheckRun run in page.Objects)
                {
                    if (await DiscardAsync(database, run, reason, token).ConfigureAwait(false))
                        passDiscarded++;
                }

                discarded += passDiscarded;
                if (passDiscarded == 0) break;
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
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            CheckRun discarded;
            SemaphoreSlim stateLock = CheckRunService.GetRunStateLock(run.Id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckRun? current = await database.CheckRuns.ReadAsync(run.Id, token: token).ConfigureAwait(false);
                if (current == null || current.Status != CheckRunStatusEnum.Pending) return false;
                if (CheckRunService.IsOperatorRun(current.Id)) return false;

                DateTime now = DateTime.UtcNow;
                current.Status = CheckRunStatusEnum.Canceled;
                current.Summary = reason + ": the voyage ended before this Check ran.";
                current.Output = current.Summary;
                current.CompletedUtc = now;
                current.LastUpdateUtc = now;
                discarded = await database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }

            await WriteDiscardedEventAsync(database, discarded, reason, token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Record the <see cref="DiscardedEventType"/> event for a Check cancelled before it ran.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="run">The cancelled Check.</param>
        /// <param name="reason">Named reason the Check never ran.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task WriteDiscardedEventAsync(DatabaseDriver database, CheckRun run, string reason, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (run == null) throw new ArgumentNullException(nameof(run));

            ArmadaEvent evt = new ArmadaEvent(DiscardedEventType, "Check discarded before it ran (" + reason + "): " + (run.Label ?? run.Type.ToString()))
            {
                TenantId = run.TenantId,
                UserId = run.UserId,
                EntityType = "check_run",
                EntityId = run.Id,
                MissionId = run.MissionId,
                VesselId = run.VesselId,
                VoyageId = run.VoyageId,
                Payload = JsonSerializer.Serialize(new { run.Id, Type = run.Type.ToString(), run.Label, Reason = reason, run.Summary })
            };
            await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        #endregion
    }
}
