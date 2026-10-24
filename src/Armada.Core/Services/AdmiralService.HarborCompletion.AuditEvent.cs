namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class AdmiralService
    {
        private async Task<bool> EnsureHarborCompletionEventAsync(
            Mission mission, HarborCompletionIntent intent, HarborCompletionPayload payload, CancellationToken token)
        {
            ArmadaEvent? existing = await _Database.Events.ReadAsync(intent.EventId, token).ConfigureAwait(false);
            if (existing == null)
            {
                existing = await _Database.Events.CreateAsync(new ArmadaEvent(payload.EventType, payload.EventMessage)
                {
                    Id = intent.EventId, TenantId = mission.TenantId, UserId = mission.UserId,
                    EntityType = "mission", EntityId = mission.Id, CaptainId = payload.CaptainId,
                    MissionId = mission.Id, VesselId = mission.VesselId, VoyageId = mission.VoyageId,
                    CreatedUtc = payload.CreatedUtc
                }, token).ConfigureAwait(false);
            }
            return String.Equals(existing.EventType, payload.EventType, StringComparison.Ordinal)
                && String.Equals(existing.MissionId, mission.Id, StringComparison.Ordinal)
                && String.Equals(existing.CaptainId, payload.CaptainId, StringComparison.Ordinal)
                && String.Equals(existing.Message, payload.EventMessage, StringComparison.Ordinal);
        }
    }
}
