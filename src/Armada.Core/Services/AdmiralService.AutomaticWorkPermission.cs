namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class AdmiralService
    {
        // Failure policy must not spend another opted-in attempt or admit downstream work before an
        // operator decides what the actual exit means. Review retains the dock and has no rescue sweep.
        private async Task<bool> TryHoldUnexpectedAutomaticExitAsync(Captain captain, Mission observed,
            int? exitCode, string detail, CancellationToken token)
        {
            using (IDisposable boundary = await MissionExecutionBoundary.Locks.AcquireAsync(observed.Id, token).ConfigureAwait(false))
            {
                Mission? current = await _Database.Missions.ReadAsync(observed.Id, token).ConfigureAwait(false);
                bool optedIn = observed.AutomaticWorkPermission != null || observed.AutomaticWorkPermissionInvalid
                    || current?.AutomaticWorkPermission != null || current?.AutomaticWorkPermissionInvalid == true;
                if (!optedIn) return false;
                if (current == null || IsPostWorkOrTerminal(current.Status)
                    || MissionAutomaticWorkPermissionPolicy.IsUnexpectedExitHeld(current)
                    || current.ProcessId != observed.ProcessId || current.StartedUtc != observed.StartedUtc
                    || current.CaptainId != observed.CaptainId || current.CommitHash != observed.CommitHash
                    || current.TenantId != observed.TenantId || current.UserId != observed.UserId)
                    return true;
                Captain? owner = await _Database.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
                if (owner == null || owner.CurrentMissionId != current.Id || owner.ProcessId != captain.ProcessId)
                    return true;

                Mission held = JsonSerializer.Deserialize<Mission>(JsonSerializer.Serialize(current))!;
                string reason = MissionAutomaticWorkPermissionPolicy.UnexpectedExitHoldPrefix
                    + " process " + (current.ProcessId?.ToString() ?? "unrecorded") + " exited with code "
                    + (exitCode?.ToString() ?? "unknown") + ". " + detail;
                held.Status = MissionStatusEnum.Review;
                held.ProcessId = null;
                held.HeldForOperatorReview = true;
                held.HeldForOperatorReviewReason = reason;
                held.FailureReason = reason;
                held.ReviewRequestedUtc = DateTime.UtcNow;
                held.LastUpdateUtc = DateTime.UtcNow;
                if (String.IsNullOrEmpty(held.AgentOutput) && _Missions.OnGetMissionOutput != null)
                {
                    string? output = _Missions.OnGetMissionOutput(held.Id);
                    if (!String.IsNullOrEmpty(output)) held.AgentOutput = RuntimeLogFormatter.RedactSecrets(output);
                }
                if (!await _Database.Missions.TryUpdateCompletionAsync(held, current, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Unexpected-exit hold conflicted with a current mission write; no retry or failure policy was admitted.");

                // Releasing the captain does not reclaim the mission's retained dock or change its counters.
                await _Captains.ReleaseAsync(owner, token).ConfigureAwait(false);
                await EmitEventAsync("mission.automatic_work_unexpected_exit_held", reason,
                    entityType: "mission", entityId: held.Id, captainId: captain.Id, missionId: held.Id,
                    vesselId: held.VesselId, voyageId: held.VoyageId, token: token).ConfigureAwait(false);
                return true;
            }
        }
    }
}
