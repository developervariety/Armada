namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class AdmiralService
    {
        private async Task<bool> EnsureHarborCompletionFactAsync(
            Mission mission, HarborCompletionIntent intent, HarborCompletionPayload payload,
            MissionAttemptFactRecorder.LineageResult lineage, string? reasonCode, CancellationToken token)
        {
            MissionAttemptFact? fact = await _Database.MissionAttemptFacts.ReadAsync(intent.FactId, token).ConfigureAwait(false);
            if (fact == null)
            {
                fact = await _Database.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
                {
                    Id = intent.FactId, TenantId = mission.TenantId, UserId = mission.UserId,
                    MissionId = mission.Id, VoyageId = mission.VoyageId, VesselId = mission.VesselId,
                    RootMissionId = lineage.RootMissionId, ParentMissionId = lineage.ParentMissionId,
                    FactType = payload.FactType, IsRescue = RescueMissionMarker.CarriesDescriptionMarker(mission.Description),
                    ReasonCode = reasonCode, CreatedUtc = payload.CreatedUtc
                }, token).ConfigureAwait(false);
            }
            return String.Equals(fact.MissionId, mission.Id, StringComparison.Ordinal)
                && String.Equals(fact.TenantId, mission.TenantId, StringComparison.Ordinal)
                && String.Equals(fact.UserId, mission.UserId, StringComparison.Ordinal)
                && fact.FactType == payload.FactType && String.Equals(fact.ReasonCode, reasonCode, StringComparison.Ordinal);
        }
    }
}
