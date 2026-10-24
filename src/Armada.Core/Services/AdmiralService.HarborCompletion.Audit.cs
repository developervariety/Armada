namespace Armada.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class AdmiralService
    {
        private async Task<bool> EnsureHarborCompletionAuditAsync(
            HarborJobSnapshot job, Mission mission, HarborCompletionIntent intent,
            HarborCompletionPayload payload, CancellationToken token)
        {
            MissionAttemptFactRecorder.LineageResult lineage = await MissionAttemptFactRecorder.ResolveLineageAsync(_Database, mission, token).ConfigureAwait(false);
            string? reasonCode = MissionAttemptFactReasonCode.Normalize(intent.ReasonCode)
                ?? MissionAttemptFactReasonCode.Normalize(lineage.ReasonCode);
            return await EnsureHarborCompletionFactAsync(mission, intent, payload, lineage, reasonCode, token).ConfigureAwait(false)
                && await EnsureHarborCompletionEventAsync(mission, intent, payload, token).ConfigureAwait(false)
                && await EnsureHarborCompletionSignalAsync(mission, intent, payload, token).ConfigureAwait(false);
        }
    }
}
