namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private MissionIntegrationBarrier CreatePreparingIntegrationBarrier(
            Mission architect, Mission integration, IReadOnlyList<ParsedArchitectMission> parsed)
        {
            MissionIntegrationBarrier barrier = new MissionIntegrationBarrier
            {
                Phase = MissionIntegrationBarrierPhaseEnum.Preparing,
                ArchitectMissionId = architect.Id,
                PlanHash = ComputeArchitectPlanHash(parsed),
                IntegrationMissionId = integration.Id,
                TenantId = integration.TenantId,
                UserId = integration.UserId,
                VesselId = integration.VesselId,
                VoyageId = integration.VoyageId,
                OriginVoyageId = architect.VoyageId ?? integration.VoyageId ?? integration.Id,
                ExpectedBranchCount = parsed.Count - 1
            };
            return barrier;
        }

        private async Task<bool> EnsurePreparingIntegrationBarrierAsync(
            Mission architect, Mission integration, IReadOnlyList<ParsedArchitectMission> parsed, CancellationToken token)
        {
            if (parsed.Count == 0 || !parsed[parsed.Count - 1].WaitForOtherMissions) return true;
            if (integration.IntegrationBarrierInvalid) return false;
            string expectedHash = ComputeArchitectPlanHash(parsed);
            MissionIntegrationBarrier? current = integration.IntegrationBarrier;
            if (current != null)
            {
                return (current.Phase == MissionIntegrationBarrierPhaseEnum.Preparing
                        || current.Phase == MissionIntegrationBarrierPhaseEnum.Accepted)
                    && current.ArchitectMissionId == architect.Id
                    && current.PlanHash == expectedHash
                    && current.ExpectedBranchCount == parsed.Count - 1;
            }
            if (integration.IntegrationBarrierRawJsonPresent) return false;
            MissionIntegrationBarrier preparing = CreatePreparingIntegrationBarrier(architect, integration, parsed);
            if (await _Database.Missions.TryUpdateIntegrationBarrierAsync(integration, preparing, token).ConfigureAwait(false)) return true;
            Mission? latest = await _Database.Missions.ReadAsync(integration.Id, token).ConfigureAwait(false);
            return latest?.IntegrationBarrier != null
                && latest.IntegrationBarrier.Phase == MissionIntegrationBarrierPhaseEnum.Preparing
                && latest.IntegrationBarrier.ArchitectMissionId == architect.Id
                && latest.IntegrationBarrier.PlanHash == expectedHash
                && latest.IntegrationBarrier.ExpectedBranchCount == parsed.Count - 1;
        }

    }
}
