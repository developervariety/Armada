namespace Armada.Core.Services
{
    using System.Linq;
    using Armada.Core.Models;

    public partial class MissionService
    {
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
