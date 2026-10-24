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
        private async Task<bool> BeginReroutedHarborCompletionAsync(
            Captain captain, Mission mission, string failureReason, string label, string benchReason,
            DateTime? benchUntilUtc, string rerouteEventKind, int plannedRecoveryAttempts, CancellationToken token)
        {
            HarborJobSnapshot? job = await ReadHarborCompletionJobAsync(mission, token).ConfigureAwait(false);
            if (job == null) return false;
            DateTime quarantineUntilUtc = benchUntilUtc.HasValue && benchUntilUtc.Value > DateTime.UtcNow
                ? benchUntilUtc.Value.ToUniversalTime()
                : DateTime.UtcNow.AddSeconds(_Settings.CaptainQuarantine.DefaultBackoffSeconds);
            HarborCompletionPayload payload = new HarborCompletionPayload
            {
                MissionId = mission.Id, CaptainId = captain.Id, DockId = job.DockId,
                VesselId = mission.VesselId, VoyageId = mission.VoyageId,
                RecoveryAttempts = plannedRecoveryAttempts, FactType = MissionAttemptFactTypeEnum.Retried,
                EventType = rerouteEventKind, EventMessage = "Mission re-routed after " + label + ": " + mission.Title,
                SignalMessage = "Mission " + mission.Id + " re-routed after " + label
                    + " (captain quarantined, attempt " + plannedRecoveryAttempts + ")", CreatedUtc = DateTime.UtcNow
            };
            HarborCompletionIntent? intent = await CreateHarborCompletionIntentAsync(job, mission,
                HarborCompletionOutcomeEnum.Rerouted, failureReason, rerouteEventKind, CaptainStateEnum.Quarantined,
                quarantineUntilUtc, benchReason, payload, token, plannedRecoveryAttempts).ConfigureAwait(false);
            return intent != null && await ResumeConfirmedCompletionAsync(job, token).ConfigureAwait(false);
        }
    }
}
