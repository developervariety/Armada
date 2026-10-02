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
    /// Finds the stage that replaced a cancelled or failed mission when its voyage was recovered by an
    /// autonomous rescue. A mission in another voyage that waited on the original stage waits on that
    /// replacement instead; the original stage and its failure stay in the record.
    /// </summary>
    public static class RescueDependencyResolver
    {
        #region Private-Members

        private const int _MaxRescueDepth = 8;

        #endregion

        #region Public-Methods

        /// <summary>
        /// The completed stage, of the same persona, in a completed rescue voyage descended from the
        /// dependency's voyage; null when the dependency is not cancelled or failed, or no completed
        /// rescue replaced it. When several rescues completed, the latest one wins.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="dependency">The mission a dependant names.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The replacement stage, or null.</returns>
        public static async Task<Mission?> FindReplacementAsync(DatabaseDriver database, Mission dependency, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (dependency == null) throw new ArgumentNullException(nameof(dependency));
            if (dependency.Status != MissionStatusEnum.Cancelled
                && dependency.Status != MissionStatusEnum.Failed
                && dependency.Status != MissionStatusEnum.LandingFailed) return null;
            if (String.IsNullOrWhiteSpace(dependency.VoyageId) || String.IsNullOrWhiteSpace(dependency.VesselId)) return null;

            List<Mission> vesselMissions = await database.Missions.EnumerateByVesselAsync(dependency.VesselId!, token).ConfigureAwait(false);
            Dictionary<string, Mission> byId = vesselMissions
                .Where(item => !String.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            HashSet<string> rescueVoyages = new HashSet<string>(StringComparer.Ordinal);
            foreach (Mission rescue in vesselMissions.Where(item => RescueMissionMarker.IsAutoRescue(item) && !String.IsNullOrWhiteSpace(item.VoyageId)))
            {
                if (DescendsFromVoyage(rescue, dependency.VoyageId!, byId)) rescueVoyages.Add(rescue.VoyageId!);
            }
            if (rescueVoyages.Count == 0) return null;

            Mission? replacement = null;
            foreach (string voyageId in rescueVoyages)
            {
                Voyage? voyage = await database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage == null || voyage.Status != VoyageStatusEnum.Complete) continue;
                Mission? stage = vesselMissions
                    .Where(item => String.Equals(item.VoyageId, voyageId, StringComparison.Ordinal)
                        && item.Status == MissionStatusEnum.Complete
                        && String.Equals(item.Persona, dependency.Persona, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.CompletedUtc ?? DateTime.MinValue)
                    .FirstOrDefault();
                if (stage == null) continue;
                if (replacement == null || (stage.CompletedUtc ?? DateTime.MinValue) > (replacement.CompletedUtc ?? DateTime.MinValue))
                    replacement = stage;
            }
            return replacement;
        }

        #endregion

        #region Private-Methods

        // A rescue descends from a voyage when its parent chain, through rescue missions only, reaches a
        // mission of that voyage.
        private static bool DescendsFromVoyage(Mission rescue, string voyageId, Dictionary<string, Mission> byId)
        {
            string? parentId = rescue.ParentMissionId;
            for (int depth = 0; depth < _MaxRescueDepth && !String.IsNullOrWhiteSpace(parentId); depth++)
            {
                if (!byId.TryGetValue(parentId!, out Mission? parent)) return false;
                if (String.Equals(parent.VoyageId, voyageId, StringComparison.Ordinal)) return true;
                if (!RescueMissionMarker.IsAutoRescue(parent)) return false;
                parentId = parent.ParentMissionId;
            }
            return false;
        }

        #endregion
    }
}
