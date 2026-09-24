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
    /// Restores the launch identities of agent processes from the stored captain and mission records, so a process
    /// launched before the admiral process restarted is verified by its stored start time and can be stopped. A record
    /// that holds a process identifier without a start time restores nothing: its process stays unverified and is
    /// never killed by identifier alone.
    /// </summary>
    public static class ProcessLaunchIdentityRestore
    {
        #region Public-Methods

        /// <summary>
        /// Restore every stored launch identity: captain records first, then missions that are still in progress.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What was restored and what stayed unverified.</returns>
        public static async Task<ProcessLaunchIdentityRestoreResult> RestoreAsync(DatabaseDriver database, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            ProcessLaunchIdentityRestoreResult result = new ProcessLaunchIdentityRestoreResult();
            List<Captain> captains = await database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            foreach (Captain captain in captains)
            {
                Restore(captain.ProcessId, captain.ProcessStartedUtc, result);
            }

            List<Mission> missions = await database.Missions.EnumerateByStatusAsync(MissionStatusEnum.InProgress, token).ConfigureAwait(false);
            foreach (Mission mission in missions)
            {
                Restore(mission.ProcessId, mission.ProcessStartedUtc, result);
            }

            return result;
        }

        #endregion

        #region Private-Methods

        private static void Restore(int? processId, DateTime? startedUtc, ProcessLaunchIdentityRestoreResult result)
        {
            if (!processId.HasValue || processId.Value <= 0 || processId.Value >= ProcessSupervisor.SyntheticProcessIdFloor) return;
            if (!startedUtc.HasValue)
            {
                if (!result.UnverifiedProcessIds.Contains(processId.Value)) result.UnverifiedProcessIds.Add(processId.Value);
                return;
            }

            if (ProcessSupervisor.RestoreLaunchedProcess(processId.Value, startedUtc.Value)) result.Restored++;
        }

        #endregion
    }
}
