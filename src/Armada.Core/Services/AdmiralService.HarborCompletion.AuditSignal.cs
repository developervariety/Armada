namespace Armada.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class AdmiralService
    {
        private async Task<bool> EnsureHarborCompletionSignalAsync(
            Mission mission, HarborCompletionIntent intent, HarborCompletionPayload payload, CancellationToken token)
        {
            if (payload.SignalMessage == null) return true;
            Signal? existing = await _Database.Signals.ReadAsync(intent.SignalId, token).ConfigureAwait(false);
            if (existing == null)
            {
                existing = await _Database.Signals.CreateAsync(new Signal(SignalTypeEnum.Progress, payload.SignalMessage)
                {
                    Id = intent.SignalId, TenantId = mission.TenantId, UserId = mission.UserId,
                    FromCaptainId = payload.CaptainId, CreatedUtc = payload.CreatedUtc
                }, token).ConfigureAwait(false);
            }
            return existing.Type == SignalTypeEnum.Progress && existing.FromCaptainId == payload.CaptainId
                && existing.TenantId == mission.TenantId && existing.UserId == mission.UserId
                && existing.Payload == payload.SignalMessage;
        }
    }
}
