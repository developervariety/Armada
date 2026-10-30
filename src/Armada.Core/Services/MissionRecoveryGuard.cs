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

    /// <summary>Serializes recovery and operator restart of the same failed mission family.</summary>
    public static class MissionRecoveryGuard
    {
        private static readonly KeyedAsyncLock _Locks = new KeyedAsyncLock(StringComparer.Ordinal);

        /// <summary>Number of recovery families currently held or awaited.</summary>
        public static int LockCount => _Locks.Count;

        /// <summary>Acquire the original failure's lock, including for a rescue of a rescue.</summary>
        /// <param name="database">Database.</param>
        /// <param name="missionId">Mission being recovered or restarted.</param>
        /// <param name="token">Cancellation.</param>
        /// <returns>The handle to dispose after all recovery or restart writes.</returns>
        public static async Task<IDisposable> AcquireAsync(DatabaseDriver database, string missionId,
            CancellationToken token = default)
        {
            Mission? mission = await database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            Mission? anchor = mission == null ? null : await FindAnchorAsync(database, mission, token).ConfigureAwait(false);
            return await _Locks.AcquireAsync(anchor?.Id ?? missionId, token).ConfigureAwait(false);
        }

        /// <summary>Find an active rescue descendant, including a completed root with a running review chain.</summary>
        /// <param name="database">Database.</param>
        /// <param name="mission">Mission being restarted.</param>
        /// <param name="token">Cancellation.</param>
        /// <returns>The active descendant, or null. Call while holding the family lock.</returns>
        public static async Task<string?> FindActiveDescendantAsync(DatabaseDriver database, Mission mission,
            CancellationToken token = default)
        {
            Mission anchor = await FindAnchorAsync(database, mission, token).ConfigureAwait(false);
            Queue<string> parents = new Queue<string>();
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> voyages = new HashSet<string>(StringComparer.Ordinal);
            parents.Enqueue(anchor.Id);
            while (parents.Count > 0)
            {
                string parentId = parents.Dequeue();
                if (!visited.Add(parentId)) continue;
                List<MissionSummary> descendants = await database.Missions
                    .EnumerateMissionSummariesByParentAsync(parentId, token).ConfigureAwait(false);
                foreach (MissionSummary descendant in descendants)
                {
                    Mission? child = await database.Missions.ReadAsync(descendant.Id, token).ConfigureAwait(false);
                    if (child == null || !SameOwner(anchor, child)) continue;
                    if (child.Id != mission.Id && !IsTerminal(child.Status)) return child.Id;
                    parents.Enqueue(child.Id);
                    if (String.IsNullOrWhiteSpace(child.VoyageId) || !voyages.Add(child.VoyageId)) continue;
                    List<Mission> stages = await database.Missions.EnumerateByVoyageAsync(child.VoyageId, token).ConfigureAwait(false);
                    foreach (Mission stage in stages.Where(item => SameOwner(anchor, item)))
                    {
                        if (stage.Id != mission.Id && !IsTerminal(stage.Status)) return stage.Id;
                        parents.Enqueue(stage.Id);
                    }
                }
            }
            return null;
        }

        private static bool IsTerminal(MissionStatusEnum status) => status == MissionStatusEnum.Complete
            || status == MissionStatusEnum.Failed || status == MissionStatusEnum.Cancelled
            || status == MissionStatusEnum.LandingFailed;

        private static bool SameOwner(Mission first, Mission second) => first.VesselId == second.VesselId
            && OwnershipPolicy.SameTenant(first.TenantId, second.TenantId)
            && OwnershipPolicy.UserOfRecord(first.UserId) == OwnershipPolicy.UserOfRecord(second.UserId);

        private static async Task<Mission> FindAnchorAsync(DatabaseDriver database, Mission mission, CancellationToken token)
        {
            Mission current = mission;
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(current.Id))
            {
                string? parentId = current.ParentMissionId;
                if (String.IsNullOrWhiteSpace(parentId) && !String.IsNullOrWhiteSpace(current.VoyageId))
                {
                    List<Mission> stages = await database.Missions.EnumerateByVoyageAsync(current.VoyageId, token).ConfigureAwait(false);
                    Mission? root = stages.FirstOrDefault(item => RescueMissionMarker.IsAutoRescue(item)
                        && !String.IsNullOrWhiteSpace(item.ParentMissionId) && SameOwner(mission, item));
                    parentId = root?.ParentMissionId;
                }
                if (String.IsNullOrWhiteSpace(parentId)) return current;
                Mission? parent = await database.Missions.ReadAsync(parentId, token).ConfigureAwait(false);
                if (parent == null || !SameOwner(mission, parent))
                    throw new InvalidOperationException("recovery_parent_unavailable: cannot serialize the recovery family");
                current = parent;
            }
            throw new InvalidOperationException("recovery_parent_cycle: cannot serialize the recovery family");
        }
    }
}
