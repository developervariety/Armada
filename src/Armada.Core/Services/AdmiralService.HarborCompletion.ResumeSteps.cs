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
        private async Task<bool> ValidateHarborCompletionDockAsync(HarborJobSnapshot job, Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(job.DockId)) return true;
            Dock? dock = await _Database.Docks.ReadAsync(job.OwnerTenantId, job.DockId, token).ConfigureAwait(false);
            return dock != null && String.Equals(dock.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                && String.Equals(dock.VesselId, mission.VesselId, StringComparison.Ordinal)
                && (String.IsNullOrWhiteSpace(dock.CaptainId) || String.Equals(dock.CaptainId, job.CaptainId, StringComparison.Ordinal));
        }

        private async Task<Mission?> ResetHarborCompletionMissionAsync(
            HarborJobSnapshot job, Mission mission, HarborCompletionIntent intent, CancellationToken token)
        {
            if (mission.Status == MissionStatusEnum.Pending && String.IsNullOrWhiteSpace(mission.CaptainId)
                && String.IsNullOrWhiteSpace(mission.DockId) && !mission.ProcessId.HasValue) return mission;
            if (mission.Status == MissionStatusEnum.Cancelled || IsPostWorkOrTerminal(mission.Status)) return null;
            MissionStatusEnum expectedStatus = mission.Status;
            mission.Status = MissionStatusEnum.Pending;
            bool harborHold = mission.AssignmentState == MissionAssignmentStateEnum.WaitingForDependency
                && String.Equals(mission.AssignmentHoldReason, MissionAssignmentHold.HarborJobUnconfirmed, StringComparison.Ordinal)
                && String.Equals(mission.AssignmentHoldEntityId, job.JobId, StringComparison.Ordinal);
            mission.AssignmentState = harborHold ? MissionAssignmentStateEnum.WaitingForDependency : MissionAssignmentStateEnum.Pending;
            mission.FailureReason = intent.FailureReason;
            mission.CaptainId = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            if (intent.Outcome == HarborCompletionOutcomeEnum.Rerouted && intent.RecoveryAttempts.HasValue)
                mission.RecoveryAttempts = intent.RecoveryAttempts.Value;
            mission.LastUpdateUtc = DateTime.UtcNow;
            if (await _Database.Missions.TryUpdateIfStatusAsync(mission, expectedStatus, token).ConfigureAwait(false)) return mission;
            Mission? reread = await _Database.Missions.ReadAsync(job.MissionId, token).ConfigureAwait(false);
            if (reread == null || reread.Status != MissionStatusEnum.Pending
                || !String.Equals(reread.HarborJobId, job.JobId, StringComparison.Ordinal)
                || !String.IsNullOrWhiteSpace(reread.CaptainId) || !String.IsNullOrWhiteSpace(reread.DockId)
                || reread.ProcessId.HasValue || !String.Equals(reread.FailureReason, intent.FailureReason, StringComparison.Ordinal)) return null;
            return reread;
        }

        private static bool IsPendingHarborCompletionMission(Mission mission, HarborJobSnapshot job, HarborCompletionIntent intent)
        {
            bool pending = mission.AssignmentState == MissionAssignmentStateEnum.Pending
                && mission.AssignmentHoldReason == null && mission.AssignmentHoldEntityId == null;
            bool held = mission.AssignmentState == MissionAssignmentStateEnum.WaitingForDependency
                && String.Equals(mission.AssignmentHoldReason, MissionAssignmentHold.HarborJobUnconfirmed, StringComparison.Ordinal)
                && String.Equals(mission.AssignmentHoldEntityId, job.JobId, StringComparison.Ordinal);
            return mission.Status == MissionStatusEnum.Pending && (pending || held)
                && String.IsNullOrWhiteSpace(mission.CaptainId) && String.IsNullOrWhiteSpace(mission.DockId)
                && !mission.ProcessId.HasValue && !mission.StartedUtc.HasValue && !mission.CompletedUtc.HasValue
                && String.Equals(mission.FailureReason, intent.FailureReason, StringComparison.Ordinal);
        }

        private async Task<bool> ReclaimHarborCompletionDockAsync(HarborJobSnapshot job, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(job.DockId)) return true;
            await _Docks.ReclaimAsync(job.DockId, job.OwnerTenantId, token).ConfigureAwait(false);
            Dock? dock = await _Database.Docks.ReadAsync(job.OwnerTenantId, job.DockId, token).ConfigureAwait(false);
            return dock == null || (!dock.Active && String.IsNullOrWhiteSpace(dock.CaptainId));
        }

        private async Task<bool> ReleaseHarborCompletionCaptainAsync(
            HarborJobSnapshot job, HarborCompletionIntent intent, CancellationToken token)
        {
            Captain? captain = await _Database.Captains.ReadAsync(job.CaptainId!, token).ConfigureAwait(false);
            if (captain == null || !String.Equals(captain.TenantId, job.OwnerTenantId, StringComparison.Ordinal)
                || !String.Equals(captain.UserId, job.OwnerUserId, StringComparison.Ordinal)) return false;
            if (captain.ProcessId == job.AdmiralProcessId || String.Equals(captain.CurrentMissionId, job.MissionId, StringComparison.Ordinal))
            {
                captain.State = intent.CaptainState;
                captain.CurrentMissionId = null;
                captain.CurrentDockId = null;
                captain.ProcessId = null;
                captain.ProcessStartedUtc = null;
                captain.QuarantineUntilUtc = intent.QuarantineUntilUtc;
                captain.QuarantineReason = intent.QuarantineReason;
                return await _Database.Captains.TryReleaseHarborAttemptAsync(job, captain, token).ConfigureAwait(false);
            }
            if (captain.ProcessId.HasValue || !String.IsNullOrWhiteSpace(captain.CurrentMissionId)
                || !String.IsNullOrWhiteSpace(captain.CurrentDockId)) return false;
            return captain.State == intent.CaptainState && captain.QuarantineUntilUtc == intent.QuarantineUntilUtc
                && String.Equals(captain.QuarantineReason, intent.QuarantineReason, StringComparison.Ordinal);
        }
    }
}
