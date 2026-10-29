namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
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
        /// mission of an ended voyage, with one exception (<see cref="PlanUpstreamFailureReopenAsync"/>): on a voyage that
        /// ended Failed, the stage that failed, or a downstream stage cancelled because an upstream stage failed, is
        /// restarted on the commit of the last stage that produced verified work, and its voyage is reopened. The refusal
        /// names the voyage and its status and points to a new voyage or a rescue instead.
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
        /// Plan the restart of a stage of an ended voyage. Allowed only when every condition holds: the voyage ended
        /// Failed (a Complete voyage landed its work and a Cancelled one was stopped by an operator); and the mission is
        /// either a stage that failed on its own (Failed, with no terminal-voyage reconciliation marker and not superseded
        /// by an earlier restart), or a stage Cancelled with the reason the dependency walker records for a stage
        /// stranded behind a failed upstream stage. A stage an operator stopped is the one exception to the voyage
        /// status: it is restarted when its voyage ended Failed or Cancelled. A stage that depends on another must reach, through stages that are
        /// Failed or Cancelled, a stage of the same voyage that handed off work with a commit: one that is WorkProduced,
        /// Complete or PullRequestOpen, or one the terminal-voyage rule moved from WorkProduced to Failed or Cancelled
        /// because its work was not landed. A cancelled stage needs at least one skipped stage between it and that stage.
        /// A skipped stage must not have moved the branch: it carries the anchor's commit, or it never started and
        /// carries none. Otherwise the restart would review a branch tip no stage verified. A failed first stage, with no
        /// dependency, runs again from its own start.
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

            bool strandedStage = mission.Status == MissionStatusEnum.Cancelled
                && (mission.FailureReason ?? String.Empty).StartsWith(MissionDependentCancellation.BlockedByFailedDependencyPrefix, StringComparison.Ordinal)
                && !String.IsNullOrWhiteSpace(mission.DependsOnMissionId);
            bool failedStage = mission.Status == MissionStatusEnum.Failed
                && !TerminalVoyageMissionRule.IsReconciledOutcome(mission)
                && !(mission.FailureReason ?? String.Empty).StartsWith(SupersededByRestartPrefix, StringComparison.Ordinal);
            // An operator stop ends the stage Cancelled and its voyage Failed or Cancelled; restarting that stage is the
            // operator's recovery, so it reopens the voyage the same way a failed stage does.
            bool stoppedStage = mission.Status == MissionStatusEnum.Cancelled
                && OperatorStop.IsOperatorStopped(mission.FailureReason)
                && (voyage.Status == VoyageStatusEnum.Failed || voyage.Status == VoyageStatusEnum.Cancelled);

            if (!stoppedStage && (voyage.Status != VoyageStatusEnum.Failed || (!strandedStage && !failedStage)))
            {
                return MissionRestartReopenPlan.Refuse(ended + pointer);
            }

            if ((failedStage || stoppedStage) && String.IsNullOrWhiteSpace(mission.DependsOnMissionId))
            {
                return MissionRestartReopenPlan.ReopenFromStart();
            }

            List<Mission> skipped = new List<Mission>();
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal) { mission.Id };
            string? nextId = mission.DependsOnMissionId;
            Mission? anchor = null;
            while (!String.IsNullOrWhiteSpace(nextId) && visited.Add(nextId!))
            {
                Mission? upstream = await _Database.Missions.ReadAsync(nextId!, token).ConfigureAwait(false);
                if (upstream == null || !String.Equals(upstream.VoyageId, voyage.Id, StringComparison.Ordinal)) break;
                if (MissionService.IsDependencySatisfyingStatus(upstream.Status) || IsUnlandedProducedWork(upstream))
                {
                    anchor = upstream;
                    break;
                }
                if (upstream.Status != MissionStatusEnum.Failed && upstream.Status != MissionStatusEnum.Cancelled) break;
                skipped.Add(upstream);
                nextId = upstream.DependsOnMissionId;
            }

            if (anchor == null || String.IsNullOrWhiteSpace(anchor.CommitHash) || (strandedStage && skipped.Count == 0))
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
        /// Whether a stage produced work that the terminal-voyage rule then moved to Failed or Cancelled only because
        /// its voyage ended without landing it. The rule moves only WorkProduced missions, so the marker with an
        /// unlanded reason and a recorded commit means the stage handed off that commit.
        /// </summary>
        private static bool IsUnlandedProducedWork(Mission stage)
        {
            return TerminalVoyageMissionRule.IsReconciledOutcome(stage)
                && String.Equals(stage.ReconciledReason, TerminalVoyageMissionRule.ReasonWorkUnlanded, StringComparison.Ordinal)
                && !String.IsNullOrWhiteSpace(stage.CommitHash);
        }

        /// <summary>
        /// Reset a Failed or Cancelled mission of a live voyage (or of no voyage) to Pending after reserving its work
        /// unit; the failed stage of a Failed voyage, or a downstream stage cancelled behind it, reopens that voyage
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
            CarryRejectedJudgeFindings(mission);
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
        /// Reason code recorded on the earlier Judge run that a restart keeps beside the mission output.
        /// </summary>
        internal const string RestartedRunReasonCode = "operator_restart";

        /// <summary>
        /// A restarted Judge whose earlier run rejected the work (FAIL or NEEDS_REVISION) carries a bounded,
        /// historical excerpt of that run in its description (<see cref="PriorJudgeFindingsBlock"/>), so the new
        /// run re-checks each finding instead of starting blind. The complete earlier output moves to the
        /// mission's preserved runs, where the mission-output read path lists it before the new run. A Judge whose
        /// earlier run did not reject, a Judge that never ran, and every other persona are left unchanged.
        /// </summary>
        private static void CarryRejectedJudgeFindings(Mission mission)
        {
            if (!PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge)) return;
            if (!MissionService.IsJudgeRejection(mission.AgentOutput)) return;

            mission.Description = PriorJudgeFindingsBlock.Apply(mission.Description, mission.AgentOutput, mission.Id);
            mission.PreservedRunOutput = MissionService.AppendPreservedRun(mission.PreservedRunOutput, mission.AgentOutput, RestartedRunReasonCode);
            mission.AgentOutput = null;
        }

        /// <summary>
        /// Apply a reopen plan. The restarted stage depends on the anchor stage, so the handoff prepares it on the
        /// anchor's branch and commit; an anchor the terminal-voyage rule moved off WorkProduced returns to
        /// WorkProduced with its earlier reason. Each skipped upstream stage is recorded Cancelled as superseded by this
        /// restart, keeping its earlier status and reason in its failure reason. Every later stage cancelled behind the
        /// restarted stage returns to Pending, so the rest of the chain runs again once the restarted stage hands off.
        /// The voyage returns to InProgress, and its Checks are armed again, because ending the voyage discarded the
        /// Pending ones.
        /// </summary>
        private async Task ApplyReopenAsync(Mission mission, Voyage voyage, Vessel vessel, MissionRestartReopenPlan plan, CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            string runsOn = plan.Anchor == null ? "its own start" : plan.Anchor.Id + " at " + plan.Anchor.CommitHash;

            if (plan.Anchor != null)
            {
                mission.DependsOnMissionId = plan.Anchor.Id;
                // Capture the verified source before the restart is saved. A reclaimed checkout
                // or deleted branch must not make later assignment choose the default branch.
                if (!HasHarborOwnedRestartSource(mission, plan.Anchor, vessel)
                    && String.Equals(mission.VesselId, plan.Anchor.VesselId, StringComparison.Ordinal)
                    && OwnershipPolicy.SameTenant(mission.TenantId, plan.Anchor.TenantId)
                    && String.Equals(OwnershipPolicy.UserOfRecord(mission.UserId), OwnershipPolicy.UserOfRecord(plan.Anchor.UserId), StringComparison.Ordinal))
                {
                    mission.StartFromRef = plan.Anchor.CommitHash;
                }
                if (IsUnlandedProducedWork(plan.Anchor))
                {
                    Mission anchor = plan.Anchor;
                    string? reason = anchor.FailureReason;
                    int separator = reason == null ? -1 : reason.IndexOf(TerminalVoyageMissionRule.PreviousReasonSeparator, StringComparison.Ordinal);
                    anchor.FailureReason = separator >= 0 ? reason!.Substring(separator + TerminalVoyageMissionRule.PreviousReasonSeparator.Length) : null;
                    anchor.Status = MissionStatusEnum.WorkProduced;
                    TerminalVoyageMissionRule.ClearReconciledOutcome(anchor);
                    anchor.LastUpdateUtc = now;
                    await _Database.Missions.UpdateAsync(anchor, token).ConfigureAwait(false);
                }
            }

            foreach (Mission stage in plan.Superseded)
            {
                string earlier = stage.Status + (String.IsNullOrWhiteSpace(stage.FailureReason) ? "" : ": " + stage.FailureReason);
                stage.Status = MissionStatusEnum.Cancelled;
                stage.FailureReason = SupersededByRestartPrefix + " of " + mission.Id + ", which runs on " + runsOn + ". It was " + earlier;
                stage.CompletedUtc = stage.CompletedUtc ?? now;
                stage.LastUpdateUtc = now;
                await _Database.Missions.UpdateAsync(stage, token).ConfigureAwait(false);
            }

            int resumed = await ResumeStrandedDependentsAsync(mission, now, token).ConfigureAwait(false);

            voyage.Status = VoyageStatusEnum.InProgress;
            voyage.CompletedUtc = null;
            voyage.LastUpdateUtc = now;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);

            int armed = await new VoyageCheckArmingService(_Database, _Settings, _Logging)
                .ArmAsync(voyage, vessel, "restart_reopen", token).ConfigureAwait(false);
            _Logging?.Info("[MissionRestartService] reopened voyage " + voyage.Id + " to restart " + mission.Id + " on "
                + runsOn + "; superseded " + plan.Superseded.Count + " stage(s); resumed " + resumed
                + " later stage(s); armed " + armed + " check(s)");
        }

        private bool HasHarborOwnedRestartSource(Mission mission, Mission anchor, Vessel vessel)
        {
            // Restart has not selected a captain. A captain-specific owned route can override
            // a native vessel route, including while Harbor is disabled. Keep its revision payload
            // unchanged until assignment can resolve the actual captain and checkout owner.
            if (!String.IsNullOrWhiteSpace(mission.HarborJobId)
                || !String.IsNullOrWhiteSpace(mission.DockProvisionAttemptId)
                || !String.IsNullOrWhiteSpace(anchor.HarborJobId)
                || !String.IsNullOrWhiteSpace(anchor.DockProvisionAttemptId)) return true;
            if (HarborMissionRouting.ResolveConfiguredForVessel(_Settings.Harbor, vessel.Id)?.CheckoutMode
                == HarborCheckoutModeEnum.HarborOwned) return true;
            if (_Settings.Harbor?.MissionRoutes != null)
            {
                foreach (HarborMissionRoute route in _Settings.Harbor.MissionRoutes)
                {
                    if (route != null && !String.IsNullOrWhiteSpace(route.CaptainId)
                        && route.CheckoutMode == HarborCheckoutModeEnum.HarborOwned) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Return to Pending every stage of the voyage that waits, directly or through other stages, on the restarted
        /// stage and was cancelled because a stage it depends on failed. A stage cancelled for any other reason, and
        /// everything behind it, is left as it is.
        /// </summary>
        private async Task<int> ResumeStrandedDependentsAsync(Mission restarted, DateTime now, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(restarted.VoyageId)) return 0;
            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(restarted.VoyageId!, token).ConfigureAwait(false);
            HashSet<string> chain = new HashSet<string>(StringComparer.Ordinal) { restarted.Id };
            int resumed = 0;
            bool discoveredMore = true;
            while (discoveredMore)
            {
                discoveredMore = false;
                foreach (Mission dependent in voyageMissions)
                {
                    if (dependent == null || chain.Contains(dependent.Id)) continue;
                    if (String.IsNullOrEmpty(dependent.DependsOnMissionId) || !chain.Contains(dependent.DependsOnMissionId!)) continue;
                    if (dependent.Status != MissionStatusEnum.Cancelled
                        || !(dependent.FailureReason ?? String.Empty).StartsWith(MissionDependentCancellation.BlockedByFailedDependencyPrefix, StringComparison.Ordinal))
                        continue;

                    chain.Add(dependent.Id);
                    discoveredMore = true;
                    dependent.Status = MissionStatusEnum.Pending;
                    dependent.FailureReason = null;
                    dependent.CaptainId = null;
                    dependent.DockId = null;
                    dependent.ProcessId = null;
                    dependent.CommitHash = null;
                    dependent.StartedUtc = null;
                    dependent.CompletedUtc = null;
                    TerminalVoyageMissionRule.ClearReconciledOutcome(dependent);
                    dependent.LastUpdateUtc = now;
                    await _Database.Missions.UpdateAsync(dependent, token).ConfigureAwait(false);
                    resumed++;
                }
            }
            return resumed;
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
