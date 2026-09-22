namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Cancels a voyage and its active missions. Every voyage cancel goes through this one
    /// operation: the operator entry points (REST, WebSocket, MCP, remote control) and the
    /// internal retirement of a voyage a dispatch race or a failed dispatch leaves behind.
    /// </summary>
    public static class VoyageCancellation
    {
        /// <summary>
        /// Failure reason recorded on missions an operator cancels by cancelling their voyage.
        /// </summary>
        public const string OperatorCancelReason = "Voyage cancelled by operator.";

        /// <summary>
        /// Mark the voyage Cancelled and cancel every mission that is Pending, Assigned, or InProgress.
        /// This helper is used to retire a newly created voyage that cannot be admitted or linked, so
        /// no mission from that orphan may remain active in the database.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyage">Voyage to cancel.</param>
        /// <param name="reason">Failure reason recorded on each cancelled mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="recallCaptain">Optional process-stop and dock-reclaim action for assigned work.</param>
        public static async Task CancelVoyageAsync(
            DatabaseDriver database,
            Voyage voyage,
            string? reason,
            CancellationToken token = default,
            Func<string, CancellationToken, Task>? recallCaptain = null)
        {
            await CancelAsync(database, voyage, reason, recallCaptain, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel a voyage and every mission in it that is Pending, Assigned, or InProgress, and report
        /// what changed.
        /// <para>
        /// When <paramref name="recallCaptain"/> is supplied, every captain holding an Assigned or
        /// InProgress mission of the voyage is recalled first, which stops its agent process, and the
        /// voyage stays active if a recall throws. Without it, running agent processes are not stopped,
        /// so an operator cancel always supplies it. A voyage that is already Cancelled or Complete is
        /// returned unchanged with no cancelled missions.
        /// </para>
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyage">Voyage to cancel, already read under the caller's authorization scope.</param>
        /// <param name="reason">Failure reason recorded on each cancelled mission.</param>
        /// <param name="recallCaptain">Process-stop and dock-reclaim action for a captain id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored voyage and the missions this call cancelled.</returns>
        public static async Task<VoyageCancellationResult> CancelAsync(
            DatabaseDriver database,
            Voyage voyage,
            string? reason,
            Func<string, CancellationToken, Task>? recallCaptain,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));

            VoyageCancellationResult result = new VoyageCancellationResult { Voyage = voyage };
            if (voyage.Status == VoyageStatusEnum.Cancelled || voyage.Status == VoyageStatusEnum.Complete)
                return result;

            List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            if (recallCaptain != null)
            {
                string[] assignedCaptains = missions
                    .Where(mission => !String.IsNullOrEmpty(mission.CaptainId)
                        && (mission.Status == MissionStatusEnum.Assigned
                            || mission.Status == MissionStatusEnum.InProgress))
                    .Select(mission => mission.CaptainId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (string captainId in assignedCaptains)
                {
                    // Keep the voyage nonterminal until every live writer is stopped. If recall
                    // fails, the exception escapes and occupancy remains conservative.
                    await recallCaptain(captainId, CancellationToken.None).ConfigureAwait(false);
                }
            }

            voyage.Status = VoyageStatusEnum.Cancelled;
            voyage.CompletedUtc = DateTime.UtcNow;
            voyage.LastUpdateUtc = DateTime.UtcNow;
            result.Voyage = await database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);

            // A cancelled voyage never runs its armed Checks; leaving them Pending counts them as required forever.
            await VoyageCheckDiscard.DiscardPendingAsync(database, voyage.Id, VoyageCheckDiscard.VoyageCancelledReason, token).ConfigureAwait(false);

            foreach (Mission mission in missions)
            {
                if (mission.Status == MissionStatusEnum.Pending
                    || mission.Status == MissionStatusEnum.Assigned
                    || mission.Status == MissionStatusEnum.InProgress)
                {
                    if (!String.IsNullOrEmpty(mission.CaptainId))
                    {
                        Captain? captain = await database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false);
                        if (captain != null && String.Equals(captain.CurrentMissionId, mission.Id, StringComparison.Ordinal))
                        {
                            List<Mission> otherActive = await database.Missions
                                .EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false);
                            if (!otherActive.Any(other =>
                                !String.Equals(other.Id, mission.Id, StringComparison.Ordinal)
                                && (other.Status == MissionStatusEnum.InProgress || other.Status == MissionStatusEnum.Assigned)))
                            {
                                captain.State = CaptainStateEnum.Idle;
                                captain.CurrentMissionId = null;
                                captain.CurrentDockId = null;
                                captain.ProcessId = null;
                                captain.RecoveryAttempts = 0;
                                captain.LastUpdateUtc = DateTime.UtcNow;
                                await database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                            }
                        }
                    }

                    mission.Status = MissionStatusEnum.Cancelled;
                    mission.FailureReason = reason;
                    mission.ProcessId = null;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    result.CancelledMissions.Add(mission);
                }
            }

            return result;
        }
    }
}
