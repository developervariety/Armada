namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<bool> IsAcceptedBranchTerminalAsync(Mission terminal,
            MissionIntegrationBarrierMember member, MissionIntegrationBarrier barrier, CancellationToken token)
        {
            HashSet<string> otherRoots = new HashSet<string>(barrier.Members
                .Where(item => item.BranchKey != member.BranchKey)
                .Select(item => item.RootMissionId), StringComparer.Ordinal);
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Mission? cursor = terminal;
            while (cursor != null && visited.Add(cursor.Id) && visited.Count <= 128)
            {
                if (!HasBarrierOwner(cursor, barrier)) return false;
                if (cursor.Id == member.RootMissionId)
                    return String.Equals(cursor.VoyageId, barrier.OriginVoyageId, StringComparison.Ordinal);
                if (otherRoots.Contains(cursor.Id)
                    || !String.Equals(cursor.VoyageId, barrier.OriginVoyageId, StringComparison.Ordinal)
                    || String.IsNullOrWhiteSpace(cursor.DependsOnMissionId)) return false;
                cursor = await _Database.Missions.ReadAsync(cursor.DependsOnMissionId!, token).ConfigureAwait(false);
            }
            return false;
        }

        private async Task<Mission?> FindAcceptedBranchRescueAsync(Mission failedTerminal,
            MissionIntegrationBarrierMember member, MissionIntegrationBarrier barrier, CancellationToken token)
        {
            Mission? broad = await RescueDependencyResolver.FindReplacementAsync(_Database, failedTerminal, token).ConfigureAwait(false);
            if (broad != null && await IsAcceptedBranchRescueAsync(broad, member, barrier, token).ConfigureAwait(false)) return broad;

            List<Mission> candidates = await _Database.Missions.EnumerateByVesselAsync(barrier.VesselId!, token).ConfigureAwait(false);
            Mission? match = null;
            foreach (Mission candidate in candidates)
            {
                if (candidate.Status == MissionStatusEnum.Failed || candidate.Status == MissionStatusEnum.Cancelled
                    || candidate.Status == MissionStatusEnum.LandingFailed || !RescueMissionMarker.IsAutoRescue(candidate)
                    || !String.Equals(candidate.Persona, failedTerminal.Persona, StringComparison.OrdinalIgnoreCase)
                    || (failedTerminal.StageOrder.HasValue && candidate.StageOrder != failedTerminal.StageOrder)
                    || !HasBarrierOwner(candidate, barrier)
                    || !await IsAcceptedBranchRescueAsync(candidate, member, barrier, token, false).ConfigureAwait(false)) continue;
                if (match == null || candidate.CreatedUtc > match.CreatedUtc) match = candidate;
            }
            return match;
        }

        private async Task<bool> IsAcceptedBranchRescueAsync(Mission replacement,
            MissionIntegrationBarrierMember member, MissionIntegrationBarrier barrier, CancellationToken token,
            bool requireComplete = true)
        {
            if ((requireComplete && replacement.Status != MissionStatusEnum.Complete)
                || !HasBarrierOwner(replacement, barrier)
                || !RescueMissionMarker.IsAutoRescue(replacement)
                || (String.IsNullOrWhiteSpace(replacement.ParentMissionId)
                    && String.IsNullOrWhiteSpace(replacement.DependsOnMissionId))) return false;

            HashSet<string> acceptedPath = new HashSet<string>(StringComparer.Ordinal)
            {
                member.RootMissionId,
                member.AcceptedTerminalMissionId,
                member.CurrentTerminalMissionId
            };
            Mission? cursor = await _Database.Missions.ReadAsync(member.AcceptedTerminalMissionId, token).ConfigureAwait(false);
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            bool reachedRoot = false;
            while (cursor != null && visited.Add(cursor.Id) && acceptedPath.Count < 128)
            {
                if (cursor.Id == member.RootMissionId)
                {
                    reachedRoot = String.Equals(cursor.VoyageId, barrier.OriginVoyageId, StringComparison.Ordinal);
                    break;
                }
                if (String.IsNullOrWhiteSpace(cursor.DependsOnMissionId)) break;
                cursor = await _Database.Missions.ReadAsync(cursor.DependsOnMissionId!, token).ConfigureAwait(false);
                if (cursor != null) acceptedPath.Add(cursor.Id);
            }
            if (!reachedRoot) return false;

            Mission? lineage = replacement;
            visited.Clear();
            for (int depth = 0; depth < 24 && lineage != null && visited.Add(lineage.Id); depth++)
            {
                if (acceptedPath.Contains(lineage.Id)) return true;
                if (barrier.Members.Any(other => other.BranchKey != member.BranchKey && other.RootMissionId == lineage.Id)) return false;
                string? parentId = !String.IsNullOrWhiteSpace(lineage.ParentMissionId)
                    ? lineage.ParentMissionId : lineage.DependsOnMissionId;
                if (String.IsNullOrWhiteSpace(parentId)) return false;
                Mission? parent = await _Database.Missions.ReadAsync(parentId!, token).ConfigureAwait(false);
                if (parent == null) return false;
                if (!acceptedPath.Contains(parent.Id) && !RescueMissionMarker.IsAutoRescue(lineage)) return false;
                lineage = parent;
            }
            return false;
        }

        private async Task CancelIntegrationBarrierMissionAsync(Mission mission,
            MissionIntegrationBarrierMember member, Mission failedTerminal, CancellationToken token)
        {
            mission.Status = MissionStatusEnum.Cancelled;
            mission.FailureReason = MissionDependentCancellation.BlockedByFailedDependencyPrefix + " "
                + failedTerminal.Id + "; integration barrier branch " + member.BranchKey + " has no accepted rescue replacement";
            mission.CompletedUtc = DateTime.UtcNow;
            mission.ProcessId = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            if (!await _Database.Missions.TryUpdateIfStatusAsync(mission, MissionStatusEnum.Pending, token).ConfigureAwait(false)) return;
            await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
        }
    }
}
