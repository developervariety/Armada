namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private static bool HasBarrierOwner(Mission mission, MissionIntegrationBarrier barrier)
        {
            return String.Equals(mission.TenantId, barrier.TenantId, StringComparison.Ordinal)
                && String.Equals(mission.UserId, barrier.UserId, StringComparison.Ordinal)
                && String.Equals(mission.VesselId, barrier.VesselId, StringComparison.Ordinal);
        }

        private async Task<bool> PrepareIntegrationBarrierAssignmentAsync(
            Mission mission, Captain captain, Vessel vessel, CancellationToken token)
        {
            if (mission.IntegrationBarrierInvalid || mission.IntegrationBarrier == null)
            {
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, mission.Id,
                    "The integration barrier record is missing or invalid. The mission will not start until an operator repairs it.", token).ConfigureAwait(false);
                return false;
            }

            MissionIntegrationBarrier barrier = mission.IntegrationBarrier;
            if (barrier.Phase != MissionIntegrationBarrierPhaseEnum.Accepted
                || barrier.IntegrationMissionId != mission.Id
                || barrier.Members.Count != barrier.ExpectedBranchCount
                || barrier.VesselId != mission.VesselId || barrier.VoyageId != mission.VoyageId)
            {
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, mission.Id,
                    "The accepted integration barrier does not match this mission. The mission will not start until an operator repairs it.", token).ConfigureAwait(false);
                return false;
            }

            Voyage? voyage = String.IsNullOrWhiteSpace(mission.VoyageId)
                ? null : await _Database.Voyages.ReadAsync(mission.VoyageId!, token).ConfigureAwait(false);
            LandingConfiguration landing = LandingConfigurationResolver.Resolve(_Settings, vessel, voyage);
            if (barrier.Members.Count > 0 && landing.LandingMode != LandingModeEnum.LocalMerge
                && landing.LandingMode != LandingModeEnum.MergeQueue)
            {
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, mission.VesselId,
                    "The integration barrier requires LocalMerge or MergeQueue so every branch can land before integration. The configured landing mode does not prove that.", token).ConfigureAwait(false);
                return false;
            }

            HarborMissionRoute? route = HarborMissionRouting.ResolveConfigured(_Settings.Harbor, captain, mission);
            if (route?.CheckoutMode == HarborCheckoutModeEnum.HarborOwned)
            {
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, mission.VesselId,
                    "The configured Harbor checkout cannot prove the local landing target commit for this integration barrier.", token).ConfigureAwait(false);
                return false;
            }
            string? targetSha = await ResolveIntegrationTargetShaAsync(mission, vessel, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(targetSha)) return false;

            if (!await CheckIntegrationBarrierMembersAsync(mission, captain, vessel, barrier, targetSha!, token).ConfigureAwait(false)) return false;

            mission.BranchName = null;
            mission.StartFromRef = targetSha;
            return true;
        }

    }
}
