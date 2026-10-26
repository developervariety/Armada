namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<HashSet<string>?> GetVerifiedAcceptedBranchPathAsync(
            MissionIntegrationBarrierMember member, MissionIntegrationBarrier barrier, CancellationToken token)
        {
            HashSet<string> path = new HashSet<string>(StringComparer.Ordinal) { member.RootMissionId };
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Mission? cursor = await _Database.Missions.ReadAsync(member.AcceptedTerminalMissionId, token).ConfigureAwait(false);
            bool reachedRoot = false;
            while (cursor != null && visited.Add(cursor.Id) && path.Count < 128)
            {
                if (!HasBarrierOwner(cursor, barrier)
                    || !String.Equals(cursor.VoyageId, barrier.OriginVoyageId, StringComparison.Ordinal)) return null;
                path.Add(cursor.Id);
                if (cursor.Id == member.RootMissionId)
                {
                    reachedRoot = true;
                    break;
                }
                if (IsOtherBarrierRoot(cursor.Id, member, barrier) || String.IsNullOrWhiteSpace(cursor.DependsOnMissionId)) return null;
                cursor = await _Database.Missions.ReadAsync(cursor.DependsOnMissionId!, token).ConfigureAwait(false);
            }
            return reachedRoot ? path : null;
        }

        private async Task<HashSet<string>?> GetVerifiedCurrentRescuePathAsync(
            MissionIntegrationBarrierMember member, MissionIntegrationBarrier barrier,
            HashSet<string> acceptedPath, CancellationToken token)
        {
            HashSet<string> rescuePath = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Mission? cursor = await _Database.Missions.ReadAsync(member.CurrentTerminalMissionId, token).ConfigureAwait(false);
            for (int depth = 0; depth < 24 && cursor != null && visited.Add(cursor.Id); depth++)
            {
                if (!HasBarrierOwner(cursor, barrier)) return null;
                if (acceptedPath.Contains(cursor.Id))
                    return String.Equals(cursor.VoyageId, barrier.OriginVoyageId, StringComparison.Ordinal) ? rescuePath : null;
                if (IsOtherBarrierRoot(cursor.Id, member, barrier)
                    || !RescueMissionMarker.IsAutoRescue(cursor)
                    || String.IsNullOrWhiteSpace(cursor.VoyageId)) return null;
                rescuePath.Add(cursor.Id);
                string? parentId = !String.IsNullOrWhiteSpace(cursor.ParentMissionId)
                    ? cursor.ParentMissionId : cursor.DependsOnMissionId;
                if (String.IsNullOrWhiteSpace(parentId)) return null;
                cursor = await _Database.Missions.ReadAsync(parentId!, token).ConfigureAwait(false);
            }
            return null;
        }

        private static bool IsOtherBarrierRoot(
            string missionId, MissionIntegrationBarrierMember member, MissionIntegrationBarrier barrier)
        {
            return barrier.Members.Exists(other => other.BranchKey != member.BranchKey && other.RootMissionId == missionId);
        }
    }
}
