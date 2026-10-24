namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class AdmiralService
    {
        /// <inheritdoc />
        public async Task<bool> ResumeConfirmedCompletionAsync(HarborJobSnapshot job, CancellationToken token = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (!HarborJobRecord.IsTerminal(job.State) || !job.StopConfirmed || String.IsNullOrWhiteSpace(job.MissionId)) return false;
            HarborCompletionIntent? intent = await _Database.HarborJobs.ReadCompletionIntentAsync(job.JobId, token).ConfigureAwait(false);
            if (intent == null) return false;
            HarborCompletionPayload? payload;
            try { payload = JsonSerializer.Deserialize<HarborCompletionPayload>(intent.Payload); }
            catch (JsonException) { return false; }
            if (payload == null || !String.Equals(payload.MissionId, job.MissionId, StringComparison.Ordinal)
                || !String.Equals(payload.CaptainId, job.CaptainId, StringComparison.Ordinal)
                || !String.Equals(payload.DockId, job.DockId, StringComparison.Ordinal)
                || payload.RecoveryAttempts != intent.RecoveryAttempts) return false;

            Mission? mission = await _Database.Missions.ReadAsync(job.MissionId, token).ConfigureAwait(false);
            if (mission == null || !String.Equals(mission.HarborJobId, job.JobId, StringComparison.Ordinal)
                || !String.Equals(mission.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                || !String.Equals(mission.UserId, job.OwnerUserId, StringComparison.Ordinal)) return false;
            if (intent.Completed)
            {
                if (mission.Status == MissionStatusEnum.Pending) _RetryDispatchNeeded = true;
                return true;
            }
            if (!await ValidateHarborCompletionDockAsync(job, mission, token).ConfigureAwait(false)) return false;
            mission = await ResetHarborCompletionMissionAsync(job, mission, intent, token).ConfigureAwait(false);
            if (mission == null || !IsPendingHarborCompletionMission(mission, job, intent)) return false;
            if (intent.Outcome == HarborCompletionOutcomeEnum.Rerouted && intent.RecoveryAttempts.HasValue
                && mission.RecoveryAttempts != intent.RecoveryAttempts.Value)
            {
                mission.RecoveryAttempts = intent.RecoveryAttempts.Value;
                mission.LastUpdateUtc = DateTime.UtcNow;
                if (!await _Database.Missions.TryUpdateIfStatusAsync(mission, MissionStatusEnum.Pending, token).ConfigureAwait(false)) return false;
            }
            if (!await ReclaimHarborCompletionDockAsync(job, token).ConfigureAwait(false)
                || !await ReleaseHarborCompletionCaptainAsync(job, intent, token).ConfigureAwait(false)) return false;
            if (!await EnsureHarborCompletionAuditAsync(job, mission, intent, payload, token).ConfigureAwait(false)) return false;
            if (!intent.Completed && !await _Database.HarborJobs.TryCompleteCompletionIntentAsync(job.JobId, token).ConfigureAwait(false)) return false;
            if (!String.IsNullOrWhiteSpace(payload.VesselId))
                QueueVoyageAssignments(payload.VoyageId, payload.VesselId!, new System.Collections.Generic.List<string> { payload.MissionId });
            else
                _RetryDispatchNeeded = true;
            return true;
        }
    }
}
