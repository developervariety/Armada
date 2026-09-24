namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Cancels one mission. Every operator cancel of a single mission goes through this one operation, so the
    /// finished-mission refusal, the captain recall and the dependent-stage cascade apply wherever it is requested.
    /// </summary>
    public static class MissionCancellation
    {
        /// <summary>
        /// Failure reason recorded on a mission an operator cancels.
        /// </summary>
        public const string OperatorCancelReason = "Mission cancelled by operator.";

        /// <summary>
        /// Refusal code for a mission whose status has no transition to Cancelled.
        /// </summary>
        public const string NotCancellableCode = "mission_not_cancellable";

        /// <summary>
        /// Refusal code for a mission whose captain could not be recalled; the mission stays live.
        /// </summary>
        public const string RecallFailedCode = "captain_recall_failed";

        /// <summary>
        /// Cancel a mission.
        /// <para>
        /// A mission whose status has no transition to Cancelled (Complete, Failed, Cancelled) is refused and keeps its
        /// outcome. When the mission's captain is working it and holds no other Assigned or InProgress mission, the
        /// captain is recalled first, which stops its agent process and releases it; if the recall throws, the cancel
        /// is refused and the mission stays live. The mission is then written Cancelled, and every stage waiting on it
        /// through its voyage's dependency chain is cancelled with it.
        /// </para>
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="mission">Mission, already read under the caller's authorization scope.</param>
        /// <param name="reason">Failure reason recorded on the mission.</param>
        /// <param name="recallCaptain">Process-stop and release action for a captain id; without it the captain's
        /// database state is released but no process is stopped.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What changed, or the refusal.</returns>
        public static async Task<MissionCancellationResult> CancelAsync(
            DatabaseDriver database,
            Mission mission,
            string? reason,
            Func<string, CancellationToken, Task>? recallCaptain,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (!MissionStateMachine.IsValidTransition(mission.Status, MissionStatusEnum.Cancelled))
            {
                return MissionCancellationResult.Refused(mission, NotCancellableCode,
                    "Mission " + mission.Id + " is " + mission.Status + " and cannot be cancelled; a finished mission keeps its outcome.");
            }

            string? recalledCaptainId = null;
            if (!String.IsNullOrEmpty(mission.CaptainId))
            {
                Captain? captain = await database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false);
                if (captain != null && String.Equals(captain.CurrentMissionId, mission.Id, StringComparison.Ordinal))
                {
                    List<Mission> captainMissions = await database.Missions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false);
                    bool holdsOtherWork = captainMissions.Any(other =>
                        !String.Equals(other.Id, mission.Id, StringComparison.Ordinal)
                        && (other.Status == MissionStatusEnum.InProgress || other.Status == MissionStatusEnum.Assigned));
                    if (!holdsOtherWork)
                    {
                        if (recallCaptain != null)
                        {
                            try
                            {
                                await recallCaptain(captain.Id, CancellationToken.None).ConfigureAwait(false);
                                recalledCaptainId = captain.Id;
                            }
                            catch (Exception ex)
                            {
                                return MissionCancellationResult.Refused(mission, RecallFailedCode,
                                    "Captain " + captain.Id + " could not be recalled, so mission " + mission.Id + " stays live: " + ex.Message);
                            }
                        }
                        else
                        {
                            captain.State = CaptainStateEnum.Idle;
                            captain.CurrentMissionId = null;
                            captain.CurrentDockId = null;
                            captain.ProcessId = null;
                            captain.RecoveryAttempts = 0;
                            captain.LastUpdateUtc = DateTime.UtcNow;
                            await database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                        }
                    }
                }
            }

            mission.Status = MissionStatusEnum.Cancelled;
            if (!String.IsNullOrWhiteSpace(reason)) mission.FailureReason = reason;
            mission.ProcessId = null;
            mission.CompletedUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            Mission stored = await database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            List<Mission> dependents = await MissionDependentCancellation.CancelBlockedAsync(
                database,
                stored,
                "Blocked by cancelled dependency " + stored.Id + ".",
                token).ConfigureAwait(false);

            return MissionCancellationResult.Cancelled(stored, dependents, recalledCaptainId);
        }
    }
}
