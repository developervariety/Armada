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
        private async Task<bool> BeginInterruptedHarborCompletionAsync(Captain captain, Mission mission, string reason, CancellationToken token)
        {
            HarborJobSnapshot? job = await ReadHarborCompletionJobAsync(mission, token).ConfigureAwait(false);
            if (job == null) return false;
            HarborCompletionPayload payload = new HarborCompletionPayload
            {
                MissionId = mission.Id, CaptainId = captain.Id, DockId = job.DockId,
                VesselId = mission.VesselId, VoyageId = mission.VoyageId,
                FactType = MissionAttemptFactTypeEnum.Retried, EventType = _InterruptedRedispatchEventType,
                EventMessage = reason + " " + mission.Title, CreatedUtc = DateTime.UtcNow
            };
            HarborCompletionIntent? intent = await CreateHarborCompletionIntentAsync(job, mission,
                HarborCompletionOutcomeEnum.Interrupted, reason, "interrupted_process_exit", CaptainStateEnum.Idle,
                NormalizeHarborUtc(captain.QuarantineUntilUtc), captain.QuarantineReason, payload, token).ConfigureAwait(false);
            return intent != null && await ResumeConfirmedCompletionAsync(job, token).ConfigureAwait(false);
        }

        private async Task<bool> BeginTransientHarborCompletionAsync(
            Captain captain, Mission mission, string failureReason, string reasonCode, CancellationToken token)
        {
            HarborJobSnapshot? job = await ReadHarborCompletionJobAsync(mission, token).ConfigureAwait(false);
            if (job == null) return false;
            bool creditFailure = ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(failureReason);
            bool quarantine = ProviderQuotaLimitDetector.IsQuotaLimitSignal(failureReason) || creditFailure;
            DateTime? retryAfterUtc = quarantine
                ? ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(failureReason, captain.Runtime, DateTime.UtcNow) : null;
            string? quarantineReason = creditFailure ? _CreditAuthQuarantineReason : failureReason;
            if (quarantine && (!retryAfterUtc.HasValue || retryAfterUtc.Value <= DateTime.UtcNow))
                retryAfterUtc = DateTime.UtcNow.AddSeconds(_Settings.CaptainQuarantine.DefaultBackoffSeconds);
            HarborCompletionPayload payload = new HarborCompletionPayload
            {
                MissionId = mission.Id, CaptainId = captain.Id, DockId = job.DockId,
                VesselId = mission.VesselId, VoyageId = mission.VoyageId,
                FactType = MissionAttemptFactTypeEnum.Retried, EventType = "mission.retry_requeued",
                EventMessage = "Mission requeued after transient captain failure: " + mission.Title + " (" + failureReason + ")",
                SignalMessage = "Mission " + mission.Id + " requeued after transient captain failure: " + failureReason
                    + (quarantine ? " (captain quarantined)" : " (captain stalled)"), CreatedUtc = DateTime.UtcNow
            };
            HarborCompletionIntent? intent = await CreateHarborCompletionIntentAsync(job, mission,
                HarborCompletionOutcomeEnum.Transient, failureReason, reasonCode,
                quarantine ? CaptainStateEnum.Quarantined : CaptainStateEnum.Stalled,
                NormalizeHarborUtc(quarantine ? retryAfterUtc : captain.QuarantineUntilUtc),
                quarantine ? quarantineReason : captain.QuarantineReason, payload, token).ConfigureAwait(false);
            return intent != null && await ResumeConfirmedCompletionAsync(job, token).ConfigureAwait(false);
        }
    }
}
