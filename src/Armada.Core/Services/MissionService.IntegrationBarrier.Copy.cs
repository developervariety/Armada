namespace Armada.Core.Services
{
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<bool> RecordIntegrationBarrierRescueAsync(Mission mission,
            MissionIntegrationBarrier barrier, MissionIntegrationBarrierMember member,
            Mission replacement, CancellationToken token)
        {
            MissionIntegrationBarrier updated = CopyBarrier(barrier);
            updated.Members.First(item => item.BranchKey == member.BranchKey).CurrentTerminalMissionId = replacement.Id;
            if (!await _Database.Missions.TryUpdateIntegrationBarrierAsync(mission, updated, token).ConfigureAwait(false))
            {
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, member.BranchKey,
                    "The accepted rescue branch changed while the barrier was being updated. The mission will retry after recovery.", token).ConfigureAwait(false);
                return false;
            }
            await HoldForAssignmentAsync(mission, IntegrationBarrierWaiting, member.BranchKey,
                "Accepted rescue for branch " + member.BranchKey + " was recorded. The landing target will be resolved again on the next assignment pass.", token).ConfigureAwait(false);
            return false;
        }

        private static MissionIntegrationBarrier CopyBarrier(MissionIntegrationBarrier source)
        {
            return new MissionIntegrationBarrier
            {
                Version = source.Version,
                Phase = source.Phase,
                ArchitectMissionId = source.ArchitectMissionId,
                PlanHash = source.PlanHash,
                IntegrationMissionId = source.IntegrationMissionId,
                TenantId = source.TenantId,
                UserId = source.UserId,
                VesselId = source.VesselId,
                VoyageId = source.VoyageId,
                OriginVoyageId = source.OriginVoyageId,
                ExpectedBranchCount = source.ExpectedBranchCount,
                Members = source.Members.Select(member => new MissionIntegrationBarrierMember
                {
                    BranchKey = member.BranchKey,
                    RootMissionId = member.RootMissionId,
                    AcceptedTerminalMissionId = member.AcceptedTerminalMissionId,
                    CurrentTerminalMissionId = member.CurrentTerminalMissionId
                }).ToList()
            };
        }

    }
}
