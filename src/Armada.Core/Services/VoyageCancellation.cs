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
    /// Cancels a voyage and its still-runnable missions. The scheduler and the operator dispatch
    /// service both retire the duplicate voyage a dispatch race produces, so the retirement
    /// lives here once instead of in each caller.
    /// </summary>
    public static class VoyageCancellation
    {
        /// <summary>
        /// Mark the voyage Cancelled and cancel every mission that is still Pending or Assigned.
        /// Missions already running are left to their captain, matching the voyage cancel paths.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyage">Voyage to cancel.</param>
        /// <param name="reason">Failure reason recorded on each cancelled mission.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task CancelVoyageAsync(
            DatabaseDriver database,
            Voyage voyage,
            string? reason,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));

            if (voyage.Status == VoyageStatusEnum.Cancelled || voyage.Status == VoyageStatusEnum.Complete)
                return;

            voyage.Status = VoyageStatusEnum.Cancelled;
            voyage.CompletedUtc = DateTime.UtcNow;
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);

            List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            foreach (Mission mission in missions)
            {
                if (mission.Status == MissionStatusEnum.Pending || mission.Status == MissionStatusEnum.Assigned)
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
                }
            }
        }
    }
}