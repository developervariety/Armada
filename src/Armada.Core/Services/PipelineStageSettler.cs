namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Settles a finished voyage's intermediate pipeline stages. A stage that handed its work to a later stage stays
    /// WorkProduced while the pipeline runs; once the voyage is complete those stages are done, so they are marked
    /// Complete instead of looking unfinished. A WorkProduced mission no other mission builds on is left alone: it
    /// can be a final result left on its branch (landing mode None).
    /// </summary>
    public static class PipelineStageSettler
    {
        #region Public-Methods

        /// <summary>
        /// Mark the voyage's handed-off intermediate stages Complete.
        /// </summary>
        /// <param name="database">Database.</param>
        /// <param name="missions">The voyage's missions.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of missions settled.</returns>
        /// <exception cref="ArgumentNullException">Thrown when database or missions is null.</exception>
        public static async Task<int> SettleAsync(DatabaseDriver database, List<Mission> missions, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (missions == null) throw new ArgumentNullException(nameof(missions));

            HashSet<string> dependedOn = new HashSet<string>(
                missions.Where(m => !String.IsNullOrEmpty(m.DependsOnMissionId)).Select(m => m.DependsOnMissionId!),
                StringComparer.Ordinal);
            int settled = 0;
            foreach (Mission mission in missions)
            {
                if (mission.Status != MissionStatusEnum.WorkProduced || !dependedOn.Contains(mission.Id)) continue;
                mission.Status = MissionStatusEnum.Complete;
                if (!mission.CompletedUtc.HasValue) mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                settled++;
            }

            return settled;
        }

        #endregion
    }
}
