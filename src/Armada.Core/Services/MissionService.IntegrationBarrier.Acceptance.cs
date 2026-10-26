namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task AcceptArchitectIntegrationBarrierAsync(
            Mission architect, IReadOnlyList<ParsedArchitectMission> parsed, List<Mission> roots,
            List<Mission> acceptedTerminals, CancellationToken token)
        {
            if (parsed.Count == 0 || !parsed[parsed.Count - 1].WaitForOtherMissions) return;
            Mission integrationRoot = roots[roots.Count - 1];
            if (integrationRoot.IntegrationBarrierInvalid)
                throw new ArchitectRecoveryUnsafeException("the integration barrier record is invalid");
            Mission stored = await _Database.Missions.ReadAsync(integrationRoot.Id, token).ConfigureAwait(false)
                ?? throw new ArchitectRecoveryUnsafeException("the integration root disappeared before its barrier was accepted");
            MissionIntegrationBarrier? preparing = stored.IntegrationBarrier;
            if (preparing == null)
            {
                if (!await EnsurePreparingIntegrationBarrierAsync(architect, stored, parsed, token).ConfigureAwait(false))
                    throw new ArchitectRecoveryUnsafeException("the integration root has no matching preparing barrier");
                stored = await _Database.Missions.ReadAsync(integrationRoot.Id, token).ConfigureAwait(false)
                    ?? throw new ArchitectRecoveryUnsafeException("the integration root disappeared while its barrier was initialized");
                preparing = stored.IntegrationBarrier;
            }
            if (preparing == null || preparing.ArchitectMissionId != architect.Id
                || preparing.PlanHash != ComputeArchitectPlanHash(parsed)
                || preparing.ExpectedBranchCount != parsed.Count - 1)
                throw new ArchitectRecoveryUnsafeException("the integration root has no matching preparing barrier");

            List<MissionIntegrationBarrierMember> members = new List<MissionIntegrationBarrierMember>();
            if (acceptedTerminals.Count != parsed.Count - 1)
                throw new ArchitectRecoveryUnsafeException("accepted branch terminal count does not match the plan");
            for (int index = 0; index < roots.Count - 1; index++)
            {
                Mission terminal = acceptedTerminals[index];
                members.Add(new MissionIntegrationBarrierMember
                {
                    BranchKey = "M" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    RootMissionId = roots[index].Id,
                    AcceptedTerminalMissionId = terminal.Id,
                    CurrentTerminalMissionId = terminal.Id
                });
            }

            if (preparing.Phase == MissionIntegrationBarrierPhaseEnum.Accepted)
            {
                bool same = preparing.Members.Count == members.Count && preparing.Members.Zip(members, (left, right) =>
                    left.BranchKey == right.BranchKey && left.RootMissionId == right.RootMissionId
                    && left.AcceptedTerminalMissionId == right.AcceptedTerminalMissionId).All(value => value);
                if (!same) throw new ArchitectRecoveryUnsafeException("accepted integration membership differs from the parsed plan");
                return;
            }
            if (preparing.Phase != MissionIntegrationBarrierPhaseEnum.Preparing)
                throw new ArchitectRecoveryUnsafeException("the integration barrier phase is unsupported");

            MissionIntegrationBarrier accepted = new MissionIntegrationBarrier
            {
                Version = preparing.Version,
                Phase = MissionIntegrationBarrierPhaseEnum.Accepted,
                ArchitectMissionId = preparing.ArchitectMissionId,
                PlanHash = preparing.PlanHash,
                IntegrationMissionId = preparing.IntegrationMissionId,
                TenantId = preparing.TenantId,
                UserId = preparing.UserId,
                VesselId = preparing.VesselId,
                VoyageId = preparing.VoyageId,
                OriginVoyageId = preparing.OriginVoyageId,
                ExpectedBranchCount = preparing.ExpectedBranchCount,
                Members = members
            };
            if (!await _Database.Missions.TryUpdateIntegrationBarrierAsync(stored, accepted, token).ConfigureAwait(false))
                throw new ArchitectRecoveryUnsafeException("the integration barrier changed while its accepted branches were stored");
        }

    }
}
