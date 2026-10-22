namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Restarts terminal missions through the same durable capacity gate as new dispatches.
    /// </summary>
    public sealed class MissionRestartService
    {
        private readonly DatabaseDriver _Database;
        private readonly FleetCapacityAdmission _Capacity;
        private readonly LoggingModule? _Logging;
        private readonly ArmadaSettings _Settings;

        /// <summary>Instantiate.</summary>
        public MissionRestartService(DatabaseDriver database, ArmadaSettings settings, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Capacity = new FleetCapacityAdmission(database, settings, logging);
        }

        /// <summary>
        /// Refusal code for a LandingFailed mission, whose produced work is landed with retry-landing instead.
        /// </summary>
        public const string UseRetryLandingCode = "use_retry_landing";

        /// <summary>
        /// Refusal code for a mission in any other status that cannot be restarted.
        /// </summary>
        public const string NotRestartableCode = "mission_not_restartable";

        /// <summary>
        /// Refusal code for a mission whose voyage has ended; its work is dispatched on a new voyage instead.
        /// </summary>
        public const string VoyageEndedCode = "voyage_ended";

        /// <summary>
        /// Failure-reason prefix recorded on an upstream stage that a restart of a later stage superseded.
        /// </summary>
        public const string SupersededByRestartPrefix = "Superseded by an operator restart";

        /// <summary>
        /// The one restart eligibility rule: only a Failed or Cancelled mission is restarted. A LandingFailed mission
        /// holds produced work that a restart would discard, so it is refused with a pointer to retry-landing.
        /// </summary>
        /// <param name="mission">Mission to restart.</param>
        /// <param name="code">Refusal code, or null when the mission may be restarted.</param>
        /// <returns>The refusal reason, or null when the mission may be restarted.</returns>
        public static string? FindIneligibility(Mission mission, out string? code)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            code = null;
            if (mission.Status == MissionStatusEnum.Failed || mission.Status == MissionStatusEnum.Cancelled) return null;
            if (mission.Status == MissionStatusEnum.LandingFailed)
            {
                code = UseRetryLandingCode;
                return "Mission " + mission.Id + " is LandingFailed and keeps its produced work; land it with retry-landing "
                    + "(POST /api/v1/missions/" + mission.Id + "/retry-landing, or armada_retry_landing) instead of restarting it.";
            }
            code = NotRestartableCode;
            return "Only Failed or Cancelled missions can be restarted (current: " + mission.Status + ").";
        }

        /// <summary>
        /// The voyage half of the restart rule. A mission whose voyage is live, or that has no voyage, may be restarted.
        /// A mission whose voyage has ended (Complete, Failed or Cancelled) is not, because assignment cancels a Pending
        /// mission of an ended voyage, with one exception (<see cref="PlanUpstreamFailureReopenAsync"/>): a downstream
        /// stage that was cancelled because an upstream stage failed, on a voyage that ended Failed, is restarted on the
        /// commit of the last stage that produced verified work, and its voyage is reopened. The refusal names the
        /// voyage and its status and points to a new voyage or a rescue instead.
        /// </summary>
        /// <param name="mission">Mission to restart.</param>
        /// <param name="voyage">The mission's voyage, or null when it has none.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The refusal reason, or null when the voyage allows the restart.</returns>
        public async Task<string?> FindVoyageIneligibilityAsync(Mission mission, Voyage? voyage, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (voyage == null || !TerminalVoyageMissionRule.IsTerminalVoyage(voyage.Status)) return null;
            MissionRestartReopenPlan plan = await PlanUpstreamFailureReopenAsync(mission, voyage, token).ConfigureAwait(false);
            return plan.Refusal;
        }

        /// <summary>
        /// Plan the restart of a downstream stage of an ended voyage. Allowed only when every condition holds:
        /// the voyage ended Failed (a Complete voyage landed its work and a Cancelled one was stopped by an operator);
        /// the mission is Cancelled with the reason the dependency walker records for a stage stranded behind a
        /// failed upstream stage; and its dependency chain reaches, through stages that are Failed or Cancelled, a stage
        /// of the same voyage that handed off work (WorkProduced, Complete or PullRequestOpen) with a commit. A skipped
        /// stage must not have moved the branch: it carries the anchor's commit, or it never started and carries none.
        /// Otherwise the restart would review a branch tip no stage verified.
        /// </summary>
        /// <param name="mission">Mission to restart.</param>
        /// <param name="voyage">The mission's ended voyage.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The plan, with <see cref="MissionRestartReopenPlan.Refusal"/> set when the restart is refused.</returns>
        public async Task<MissionRestartReopenPlan> PlanUpstreamFailureReopenAsync(Mission mission, Voyage voyage, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));

            string ended = "Mission " + mission.Id + " belongs to voyage " + voyage.Id + ", which is " + voyage.Status
                + "; a mission of an ended voyage is cancelled instead of assigned, so it is not restarted. ";
            const string pointer = "Dispatch a new voyage (or a rescue mission) for this work instead.";

            if (voyage.Status != VoyageStatusEnum.Failed
                || mission.Status != MissionStatusEnum.Cancelled
                || !(mission.FailureReason ?? String.Empty).StartsWith(MissionDependentCancellation.BlockedByFailedDependencyPrefix, StringComparison.Ordinal)
                || String.IsNullOrWhiteSpace(mission.DependsOnMissionId))
            {
                return MissionRestartReopenPlan.Refuse(ended + pointer);
            }

            List<Mission> skipped = new List<Mission>();
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal) { mission.Id };
            string? nextId = mission.DependsOnMissionId;
            Mission? anchor = null;
            while (!String.IsNullOrWhiteSpace(nextId) && visited.Add(nextId!))
            {
                Mission? upstream = await _Database.Missions.ReadAsync(nextId!, token).ConfigureAwait(false);
                if (upstream == null || !String.Equals(upstream.VoyageId, voyage.Id, StringComparison.Ordinal)) break;
                if (MissionService.IsDependencySatisfyingStatus(upstream.Status))
                {
                    anchor = upstream;
                    break;
                }
                if (upstream.Status != MissionStatusEnum.Failed && upstream.Status != MissionStatusEnum.Cancelled) break;
                skipped.Add(upstream);
                nextId = upstream.DependsOnMissionId;
            }

            if (anchor == null || String.IsNullOrWhiteSpace(anchor.CommitHash) || skipped.Count == 0)
            {
                return MissionRestartReopenPlan.Refuse(ended + "No earlier stage of its chain produced verified work to run it on. " + pointer);
            }

            foreach (Mission stage in skipped)
            {
                bool keptAnchorCommit = String.Equals(stage.CommitHash, anchor.CommitHash, StringComparison.OrdinalIgnoreCase);
                bool neverRan = String.IsNullOrWhiteSpace(stage.CommitHash) && stage.StartedUtc == null;
                if (!keptAnchorCommit && !neverRan)
                {
                    return MissionRestartReopenPlan.Refuse(ended + "Upstream stage " + stage.Id + " (" + stage.Status + ") "
                        + (String.IsNullOrWhiteSpace(stage.CommitHash) ? "ran and left no recorded commit" : "moved the branch to " + stage.CommitHash)
                        + " past " + anchor.Id + " at " + anchor.CommitHash + ", so no stage verified the branch tip. " + pointer);
                }
            }

            return MissionRestartReopenPlan.Reopen(anchor, skipped);
        }

        /// <summary>
        /// Reset a Failed or Cancelled mission of a live voyage (or of no voyage) to Pending after reserving its work
        /// unit; a downstream stage of a voyage that failed upstream reopens that voyage
        /// (<see cref="PlanUpstreamFailureReopenAsync"/>). A mission refused by <see cref="FindIneligibility"/> or
        /// <see cref="FindVoyageIneligibilityAsync"/> throws
        /// <see cref="InvalidOperationException"/> with the refusal reason and is not changed.
        /// </summary>
        /// <param name="mission">Mission to restart.</param>
        /// <param name="title">Optional new title; blank keeps the current one.</param>
        /// <param name="description">Optional new description; blank keeps the current one.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The restarted mission.</returns>
        public async Task<Mission> RestartAsync(
            Mission mission,
            string? title = null,
            string? description = null,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            string? ineligible = FindIneligibility(mission, out _);
            if (ineligible != null) throw new InvalidOperationException(ineligible);
            Voyage? voyage = await ReadVoyageAsync(mission, token).ConfigureAwait(false);
            MissionRestartReopenPlan? reopen = null;
            if (voyage != null && TerminalVoyageMissionRule.IsTerminalVoyage(voyage.Status))
            {
                reopen = await PlanUpstreamFailureReopenAsync(mission, voyage, token).ConfigureAwait(false);
                if (reopen.Refusal != null) throw new InvalidOperationException(reopen.Refusal);
            }
            if (String.IsNullOrWhiteSpace(mission.VesselId))
                throw new InvalidOperationException("Mission does not have an associated vessel.");

            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + mission.VesselId);

            await using FleetCapacityReservation admission = await _Capacity
                .AcquireAsync(vessel, mission.VoyageId, token).ConfigureAwait(false);

            if (!String.IsNullOrWhiteSpace(title)) mission.Title = title;
            if (!String.IsNullOrWhiteSpace(description)) mission.Description = description;
            mission.Status = MissionStatusEnum.Pending;
            mission.CreatedUtc = DateTime.UtcNow;
            mission.CaptainId = null;
            mission.BranchName = null;
            mission.PrUrl = null;
            mission.CommitHash = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.DiffSnapshot = null;
            mission.FailureReason = null;
            TerminalVoyageMissionRule.ClearReconciledOutcome(mission);
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            if (reopen != null && voyage != null) await ApplyReopenAsync(mission, voyage, vessel, reopen, token).ConfigureAwait(false);
            mission = await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            try
            {
                await admission.VerifyOwnershipAsync(token).ConfigureAwait(false);
            }
            catch
            {
                mission.Status = MissionStatusEnum.Cancelled;
                mission.FailureReason = "Mission restart cancelled because fleet-capacity admission was lost.";
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Restarted, "restarted", _Logging, token).ConfigureAwait(false);
            return mission;
        }

        /// <summary>
        /// Apply a reopen plan: the restarted stage depends on the anchor stage, so the handoff prepares it on the
        /// anchor's branch and commit; each skipped upstream stage is recorded Cancelled as superseded by this restart,
        /// keeping its earlier status and reason in its failure reason, so the voyage can complete once the restarted
        /// stage lands; the voyage returns to InProgress; and its Checks are armed again, because ending the voyage
        /// discarded the Pending ones.
        /// </summary>
        private async Task ApplyReopenAsync(Mission mission, Voyage voyage, Vessel vessel, MissionRestartReopenPlan plan, CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            mission.DependsOnMissionId = plan.Anchor!.Id;

            foreach (Mission stage in plan.Superseded)
            {
                string earlier = stage.Status + (String.IsNullOrWhiteSpace(stage.FailureReason) ? "" : ": " + stage.FailureReason);
                stage.Status = MissionStatusEnum.Cancelled;
                stage.FailureReason = SupersededByRestartPrefix + " of " + mission.Id + ", which runs on " + plan.Anchor.Id
                    + " at " + plan.Anchor.CommitHash + ". It was " + earlier;
                stage.CompletedUtc = stage.CompletedUtc ?? now;
                stage.LastUpdateUtc = now;
                await _Database.Missions.UpdateAsync(stage, token).ConfigureAwait(false);
            }

            voyage.Status = VoyageStatusEnum.InProgress;
            voyage.CompletedUtc = null;
            voyage.LastUpdateUtc = now;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);

            int armed = await new VoyageCheckArmingService(_Database, _Settings, _Logging)
                .ArmAsync(voyage, vessel, "restart_reopen", token).ConfigureAwait(false);
            _Logging?.Info("[MissionRestartService] reopened voyage " + voyage.Id + " to restart " + mission.Id + " on "
                + plan.Anchor.Id + " (" + plan.Anchor.CommitHash + "); superseded " + plan.Superseded.Count + " stage(s); armed " + armed + " check(s)");
        }

        /// <summary>
        /// Read the voyage a mission belongs to, as <see cref="FindVoyageIneligibilityAsync"/> judges it.
        /// </summary>
        /// <param name="mission">Mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The voyage, or null when the mission has none or it no longer exists.</returns>
        public async Task<Voyage?> ReadVoyageAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (String.IsNullOrWhiteSpace(mission.VoyageId)) return null;
            return await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
        }
    }
}
