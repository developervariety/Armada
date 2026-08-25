namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Background sweep service that selects eligible objectives, applies guardrails,
    /// auto-dispatches each through AdmiralService, links the resulting voyage, and
    /// reconciles objectives whose linked voyage has completed to Completed status.
    /// </summary>
    public sealed class AutonomousObjectiveScheduler
    {
        #region Public-Members

        /// <summary>
        /// Whether the scheduler is allowed to auto-dispatch eligible objectives.
        /// </summary>
        public bool Enabled { get; private set; }

        /// <summary>
        /// Whether the scheduler is temporarily paused.
        /// </summary>
        public bool Paused { get; private set; }

        /// <summary>
        /// Participant key of the session that set the pause, or null when unattributed.
        /// </summary>
        public string? PausedBy { get; private set; }

        /// <summary>
        /// UTC time the pause was set, or null when unattributed.
        /// </summary>
        public DateTime? PausedUtc { get; private set; }

        /// <summary>
        /// Why the pause was set, or null.
        /// </summary>
        public string? PauseReason { get; private set; }

        /// <summary>
        /// Minutes between scheduled sweep ticks.
        /// </summary>
        public int IntervalMinutes { get; private set; }

        /// <summary>
        /// Minutes the pausing session must be absent before its pause may be cleared as stale.
        /// Read from settings on each call so a settings edit takes effect without a restart.
        /// </summary>
        public int StalePauseAbsenceMinutes => _Settings.AutonomousObjectiveScheduler.StalePauseAbsenceMinutes;

        /// <summary>
        /// Maximum number of objectives with simultaneously active linked voyages.
        /// </summary>
        public int MaxConcurrentVoyages { get; private set; }

        /// <summary>
        /// Maximum number of active objective voyages allowed on one vessel.
        /// </summary>
        public int MaxConcurrentVoyagesPerVessel { get; private set; }

        /// <summary>
        /// UTC timestamp of the last completed sweep tick, or null if no tick has run.
        /// </summary>
        public DateTime? LastTickUtc { get; private set; }

        /// <summary>
        /// Short plain-text summary of the last sweep result.
        /// </summary>
        public string? LastResultSummary { get; private set; }

        /// <summary>
        /// Number of objectives that have an active linked voyage, as of the last sweep tick.
        /// </summary>
        /// <remarks>
        /// This counts EVERY active linked voyage, including one an operator dispatched by hand --
        /// not only voyages this scheduler dispatched. That is deliberate: the number exists to
        /// apply back-pressure, and a second autonomous voyage against an objective a human is
        /// already working duplicates the work rather than adding throughput. The consequence is
        /// that the count can exceed MaxConcurrentVoyages, because the limit gates what the
        /// SCHEDULER starts and cannot gate what an operator starts.
        /// </remarks>
        public int ActiveDispatchedCount { get; private set; }

        /// <summary>
        /// Reason the last sweep skipped dispatch (e.g. "disabled", "paused", "max_concurrent"),
        /// or null if the last sweep dispatched normally.
        /// </summary>
        public string? LastSkipReason { get; private set; }

        #endregion

        #region Private-Members

        private const string _Header = "[AutonomousObjectiveScheduler] ";
        private readonly DatabaseDriver _Database;
        private readonly ObjectiveService _Objectives;
        private readonly IAdmiralService _Admiral;
        private readonly IMergeQueueService _MergeQueue;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly ICodeIndexService? _CodeIndex;
        private readonly SemaphoreSlim _SweepLock = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the scheduler with required dependencies.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="objectives">Objective service.</param>
        /// <param name="admiral">Admiral service for voyage dispatch.</param>
        /// <param name="mergeQueue">Merge queue service for back-pressure gating.</param>
        /// <param name="settings">Armada settings (seed values for runtime state).</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="codeIndex">Optional code index service for index-update gating.</param>
        public AutonomousObjectiveScheduler(
            DatabaseDriver database,
            ObjectiveService objectives,
            IAdmiralService admiral,
            IMergeQueueService mergeQueue,
            ArmadaSettings settings,
            LoggingModule logging,
            ICodeIndexService? codeIndex = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _MergeQueue = mergeQueue ?? throw new ArgumentNullException(nameof(mergeQueue));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _CodeIndex = codeIndex;

            Enabled = settings.AutonomousObjectiveScheduler.Enabled;
            Paused = settings.AutonomousObjectiveScheduler.Paused;
            PausedBy = settings.AutonomousObjectiveScheduler.PausedBy;
            PausedUtc = settings.AutonomousObjectiveScheduler.PausedUtc;
            PauseReason = settings.AutonomousObjectiveScheduler.PauseReason;
            IntervalMinutes = settings.AutonomousObjectiveScheduler.IntervalMinutes;
            MaxConcurrentVoyages = settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages;
            MaxConcurrentVoyagesPerVessel = settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Mirror the current runtime state into settings and write them to disk.
        /// </summary>
        /// <remarks>
        /// Runtime state does not survive a restart. A scheduler enabled over MCP therefore reverts
        /// to the file's value at the next Admiral start, and an autonomous campaign stops with no
        /// failure for anyone to notice -- the tool reported success and the setting was real until
        /// the process ended. Writing the file is the second half of the change, not an
        /// optimisation, so every caller that changes runtime state must call this.
        /// </remarks>
        /// <returns>True when the settings file was written.</returns>
        public async Task<bool> TryPersistAsync()
        {
            try
            {
                _Settings.AutonomousObjectiveScheduler.Enabled = Enabled;
                _Settings.AutonomousObjectiveScheduler.Paused = Paused;
                _Settings.AutonomousObjectiveScheduler.PausedBy = PausedBy;
                _Settings.AutonomousObjectiveScheduler.PausedUtc = PausedUtc;
                _Settings.AutonomousObjectiveScheduler.PauseReason = PauseReason;
                _Settings.AutonomousObjectiveScheduler.IntervalMinutes = IntervalMinutes;
                _Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = MaxConcurrentVoyages;
                _Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = MaxConcurrentVoyagesPerVessel;
                await _Settings.SaveAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not persist scheduler settings: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Allow the scheduler to dispatch objectives on subsequent sweeps.
        /// </summary>
        public void Enable() => Enabled = true;

        /// <summary>
        /// Prevent the scheduler from dispatching objectives.
        /// </summary>
        public void Disable() => Enabled = false;

        /// <summary>
        /// Temporarily suspend dispatch without clearing the Enabled flag. Record who set the
        /// pause, when and why: a pause outlives the session that set it, and without an owner
        /// nobody can tell a live deploy window from a departed peer's leftover.
        /// </summary>
        /// <param name="pausedBy">Participant key of the pausing session, or null.</param>
        /// <param name="reason">Why the pause is set, or null.</param>
        public void Pause(string? pausedBy = null, string? reason = null)
        {
            Paused = true;
            PausedBy = String.IsNullOrWhiteSpace(pausedBy) ? null : pausedBy.Trim();
            PausedUtc = DateTime.UtcNow;
            PauseReason = String.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        }

        /// <summary>
        /// Resume from a paused state and drop the pause attribution.
        /// </summary>
        public void Resume()
        {
            Paused = false;
            PausedBy = null;
            PausedUtc = null;
            PauseReason = null;
        }

        /// <summary>
        /// Set the sweep interval, clamped to 1-1440 minutes.
        /// </summary>
        /// <param name="minutes">New interval in minutes.</param>
        public void SetIntervalMinutes(int minutes)
        {
            IntervalMinutes = Math.Max(1, Math.Min(1440, minutes));
        }

        /// <summary>
        /// Set the maximum number of concurrently active objective voyages, clamped to 1-50.
        /// </summary>
        /// <param name="max">New concurrency cap.</param>
        public void SetMaxConcurrentVoyages(int max)
        {
            MaxConcurrentVoyages = Math.Max(1, Math.Min(50, max));
        }

        /// <summary>
        /// Set the maximum active objective voyages on one vessel, clamped to 1-50.
        /// </summary>
        /// <param name="max">New per-vessel concurrency cap.</param>
        public void SetMaxConcurrentVoyagesPerVessel(int max)
        {
            MaxConcurrentVoyagesPerVessel = Math.Max(1, Math.Min(50, max));
        }

        /// <summary>
        /// Fire-and-forget background sweep. The caller is never blocked.
        /// OperationCanceledException is swallowed silently; all other errors are logged as warnings.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SweepAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "background sweep failed: " + ex.Message);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Run one bounded scheduling sweep: reconcile completed objectives, then dispatch eligible ones.
        /// Non-reentrant; concurrent calls return immediately without running a second sweep.
        /// Skips the work portion if the last tick ran within IntervalMinutes, so the health loop
        /// can call this on every heartbeat without over-triggering.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task SweepAsync(CancellationToken token = default)
        {
            if (!await _SweepLock.WaitAsync(0, token).ConfigureAwait(false)) return;

            try
            {
                if (LastTickUtc.HasValue
                    && (DateTime.UtcNow - LastTickUtc.Value).TotalMinutes < IntervalMinutes)
                {
                    _Logging.Debug(_Header + "sweep skipped: interval not elapsed (" + IntervalMinutes + " min).");
                    return;
                }

                LastTickUtc = DateTime.UtcNow;

                if (!Enabled)
                {
                    _Logging.Debug(_Header + "sweep skipped: scheduler is disabled.");
                    await EmitSystemEventAsync("objective_scheduler.skipped_disabled",
                        "Autonomous objective scheduler sweep skipped: scheduler is disabled.", token).ConfigureAwait(false);
                    LastSkipReason = "disabled";
                    LastResultSummary = "skipped (disabled)";
                    return;
                }

                if (Paused)
                {
                    _Logging.Debug(_Header + "sweep skipped: scheduler is paused.");
                    await EmitSystemEventAsync("objective_scheduler.skipped_paused",
                        "Autonomous objective scheduler sweep skipped: scheduler is paused.", token).ConfigureAwait(false);
                    LastSkipReason = "paused";
                    LastResultSummary = "skipped (paused)";
                    return;
                }

                AuthContext systemAuth = BuildSystemAuth();
                List<Objective> snapshot = await ReadAllObjectivesAsync(systemAuth, token).ConfigureAwait(false);

                int reconciledCount = await ReconcileCompletedObjectivesAsync(systemAuth, snapshot, token).ConfigureAwait(false);

                snapshot = await ReadAllObjectivesAsync(systemAuth, token).ConfigureAwait(false);
                List<Objective> eligible = AutonomousObjectiveSelector.SelectEligible(snapshot);

                ActiveVoyageSummary active = await CountActiveDispatchedAsync(snapshot, token).ConfigureAwait(false);
                ActiveDispatchedCount = active.Total;
                int capacity = MaxConcurrentVoyages - active.Total;

                if (capacity <= 0)
                {
                    string concurrencyDetail = active.Total + " objective(s) have an active linked voyage, "
                        + "including any dispatched by an operator; limit is " + MaxConcurrentVoyages;
                    _Logging.Debug(_Header + "sweep: concurrency limit reached (" + concurrencyDetail + ").");
                    await EmitSystemEventAsync("objective_scheduler.skipped_max_concurrent",
                        "Autonomous objective scheduler dispatch skipped: " + concurrencyDetail + ".", token).ConfigureAwait(false);
                    LastSkipReason = "max_concurrent";
                    LastResultSummary = "reconciled=" + reconciledCount + " dispatched=0 (max_concurrent)";
                    return;
                }

                int dispatched = 0;
                int vesselConcurrencySkips = 0;
                // Every skip is counted by reason. A sweep that dispatches nothing must be
                // able to say why; reporting dispatched=0 with no reason reads as an idle
                // fleet, and hid two permanently undispatchable objectives for days.
                Dictionary<string, int> skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
                List<MergeEntry> mergeQueue = await _MergeQueue.ListAsync(token: token).ConfigureAwait(false);

                foreach (Objective objective in eligible)
                {
                    if (dispatched >= capacity) break;
                    token.ThrowIfCancellationRequested();

                    if (objective.VesselIds.Count == 1)
                    {
                        string candidateVesselId = objective.VesselIds[0];
                        active.ByVessel.TryGetValue(candidateVesselId, out int activeOnVessel);
                        if (activeOnVessel >= MaxConcurrentVoyagesPerVessel)
                        {
                            vesselConcurrencySkips++;
                            RecordSkip(skipReasons, "vessel_concurrency");
                            await EmitObjectiveEventAsync(
                                "objective_scheduler.skipped_vessel_concurrency",
                                "Autonomous scheduler skipped objective " + objective.Id + ": vessel "
                                    + candidateVesselId + " already has " + activeOnVessel
                                    + " active objective voyage(s); per-vessel limit is "
                                    + MaxConcurrentVoyagesPerVessel + ".",
                                objective,
                                candidateVesselId,
                                token).ConfigureAwait(false);
                            continue;
                        }
                    }

                    try
                    {
                        await DispatchObjectiveAsync(objective, mergeQueue, token).ConfigureAwait(false);
                        dispatched++;
                        if (objective.VesselIds.Count == 1)
                        {
                            string dispatchedVesselId = objective.VesselIds[0];
                            active.ByVessel.TryGetValue(dispatchedVesselId, out int activeOnVessel);
                            active.ByVessel[dispatchedVesselId] = activeOnVessel + 1;
                        }
                    }
                    catch (ObjectiveSkippedException skipped)
                    {
                        RecordSkip(skipReasons, skipped.Reason);
                    }
                    catch (Exception ex)
                    {
                        RecordSkip(skipReasons, "dispatch_error");
                        _Logging.Warn(_Header + "dispatch failed for objective " + objective.Id + ": " + ex.Message);
                    }
                }

                // A null skip reason must mean "work was dispatched", never "nothing
                // happened and I cannot say why". An empty eligible set is itself a
                // reportable state: it separates an idle fleet from a blocked one.
                LastSkipReason = dispatched > 0
                    ? null
                    : (skipReasons.Count > 0 ? DescribeSkips(skipReasons) : "no_eligible_objectives");

                LastResultSummary = "reconciled=" + reconciledCount + " dispatched=" + dispatched
                    + (skipReasons.Count > 0 ? " skipped=" + DescribeSkips(skipReasons) : String.Empty);
                _Logging.Info(_Header + "sweep complete: reconciled=" + reconciledCount
                    + " dispatched=" + dispatched + " capacity=" + capacity
                    + (skipReasons.Count > 0 ? " skipped=" + DescribeSkips(skipReasons) : String.Empty) + ".");
            }
            finally
            {
                _SweepLock.Release();
            }
        }

        #endregion

        #region Private-Methods

        private async Task<int> ReconcileCompletedObjectivesAsync(AuthContext systemAuth, List<Objective> snapshot, CancellationToken token)
        {
            int reconciled = 0;
            List<Objective> inProgress = snapshot
                .Where(o => o.Status == ObjectiveStatusEnum.InProgress && o.VoyageIds.Count > 0)
                .ToList();

            foreach (Objective objective in inProgress)
            {
                try
                {
                    bool allLanded = await AllLinkedVoyagesCompletedAsync(objective, token).ConfigureAwait(false);
                    if (!allLanded) continue;

                    AuthContext objectiveAuth = BuildAuth(objective);
                    ObjectiveUpsertRequest req = new ObjectiveUpsertRequest
                    {
                        Title = objective.Title,
                        Status = ObjectiveStatusEnum.Completed
                    };
                    await _Objectives.UpdateAsync(objectiveAuth, objective.Id, req, token).ConfigureAwait(false);

                    await EmitObjectiveEventAsync("objective_scheduler.objective_completed",
                        "Autonomous scheduler reconciled objective " + objective.Id + " to Completed: all linked voyages landed.",
                        objective, null, token).ConfigureAwait(false);

                    reconciled++;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "reconcile failed for objective " + objective.Id + ": " + ex.Message);
                }
            }

            return reconciled;
        }

        private async Task<bool> AllLinkedVoyagesCompletedAsync(Objective objective, CancellationToken token)
        {
            foreach (string voyageId in objective.VoyageIds)
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage == null) continue;
                if (voyage.Status != VoyageStatusEnum.Complete) return false;
            }

            return true;
        }

        private async Task<ActiveVoyageSummary> CountActiveDispatchedAsync(List<Objective> snapshot, CancellationToken token)
        {
            ActiveVoyageSummary summary = new ActiveVoyageSummary();
            foreach (Objective objective in snapshot)
            {
                if (!await HasActiveLinkedVoyageAsync(objective, token).ConfigureAwait(false)) continue;

                summary.Total++;
                if (objective.VesselIds.Count == 1)
                {
                    string vesselId = objective.VesselIds[0];
                    summary.ByVessel.TryGetValue(vesselId, out int activeOnVessel);
                    summary.ByVessel[vesselId] = activeOnVessel + 1;
                }
            }

            return summary;
        }

        private async Task DispatchObjectiveAsync(
            Objective objective,
            List<MergeEntry> mergeQueue,
            CancellationToken token)
        {
            if (objective.Status == ObjectiveStatusEnum.Completed || objective.Status == ObjectiveStatusEnum.Cancelled)
            {
                _Logging.Debug(_Header + "objective " + objective.Id + " skipped: terminal status " + objective.Status + ".");
                await EmitObjectiveEventAsync("objective_scheduler.skipped_terminal_status",
                    "Autonomous scheduler skipped objective " + objective.Id + ": status is " + objective.Status + ".",
                    objective, null, token).ConfigureAwait(false);
                throw new ObjectiveSkippedException("terminal_status");
            }

            if (objective.VoyageIds.Count > 0)
            {
                if (await HasActiveLinkedVoyageAsync(objective, token).ConfigureAwait(false))
                {
                    _Logging.Debug(_Header + "objective " + objective.Id + " skipped: active linked voyage exists.");
                    await EmitObjectiveEventAsync("objective_scheduler.skipped_active_voyage",
                        "Autonomous scheduler skipped objective " + objective.Id + ": an active linked voyage already exists.",
                        objective, null, token).ConfigureAwait(false);
                    throw new ObjectiveSkippedException("active_voyage");
                }

                // Every linked voyage has ended. Linking a voyage promotes the objective to
                // InProgress, so a Scoped or Planned row that still carries ended voyages is one an
                // operator requeued after they failed, were cancelled, or landed. Holding it here
                // would keep it undispatchable for ever: reconcile only completes InProgress rows,
                // so nothing else would ever release it.
                _Logging.Info(_Header + "objective " + objective.Id + " is a requeue: " + objective.VoyageIds.Count
                    + " linked voyage(s) have all ended; dispatching a new voyage.");
                await EmitObjectiveEventAsync("objective_scheduler.requeue_after_ended_voyages",
                    "Autonomous scheduler is dispatching requeued objective " + objective.Id + ": its "
                    + objective.VoyageIds.Count + " linked voyage(s) have all ended.",
                    objective, null, token).ConfigureAwait(false);
            }

            if (objective.VesselIds.Count != 1)
            {
                string vesselDetail = "auto-dispatch needs exactly one vessel, it has "
                    + objective.VesselIds.Count
                    + ". Set VesselIds to the vessel whose repository receives the commit.";
                _Logging.Warn(_Header + "objective " + objective.Id + " skipped: " + vesselDetail);
                await EmitObjectiveEventAsync("objective_scheduler.skipped_vessel_count",
                    "Autonomous scheduler skipped objective " + objective.Id + ": " + vesselDetail,
                    objective, null, token).ConfigureAwait(false);
                throw new ObjectiveSkippedException("vessel_count");
            }

            string vesselId = objective.VesselIds[0];

            bool hasBackPressure = mergeQueue.Any(e =>
                String.Equals(e.VesselId, vesselId, StringComparison.OrdinalIgnoreCase)
                && !IsMergeTerminal(e.Status));

            if (hasBackPressure)
            {
                _Logging.Debug(_Header + "objective " + objective.Id + " skipped: merge queue back-pressure for vessel " + vesselId + ".");
                await EmitObjectiveEventAsync("objective_scheduler.skipped_backpressure",
                    "Autonomous scheduler skipped objective " + objective.Id + ": merge queue back-pressure for vessel " + vesselId + ".",
                    objective, vesselId, token).ConfigureAwait(false);
                throw new ObjectiveSkippedException("backpressure");
            }

            if (_Settings.CodeIndex.Enabled && _CodeIndex != null)
            {
                try
                {
                    CodeIndexStatus indexStatus = await _CodeIndex.GetStatusAsync(vesselId, token).ConfigureAwait(false);
                    if (indexStatus.UpdateInProgress)
                    {
                        _Logging.Debug(_Header + "objective " + objective.Id + " skipped: code index update in progress for vessel " + vesselId + ".");
                        await EmitObjectiveEventAsync("objective_scheduler.skipped_index_update",
                            "Autonomous scheduler skipped objective " + objective.Id + ": code index update in progress for vessel " + vesselId + ".",
                            objective, vesselId, token).ConfigureAwait(false);
                        throw new ObjectiveSkippedException("index_update");
                    }
                }
                catch (ObjectiveSkippedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not read code index status for vessel " + vesselId + ", proceeding without index gate: " + ex.Message);
                }
            }

            string missionDescription = BuildMissionDescription(objective);
            MissionDescription md = new MissionDescription(objective.Title, missionDescription)
            {
                CodeContextMode = _Settings.CodeIndex.Enabled ? "auto" : "off"
            };

            List<MissionDescription> missionDescriptions = new List<MissionDescription> { md };

            Voyage voyage = await _Admiral.DispatchVoyageAsync(
                objective.Title,
                missionDescription,
                vesselId,
                missionDescriptions,
                objective.SuggestedPipelineId,
                objective.SuggestedPlaybooks.Count > 0 ? objective.SuggestedPlaybooks : null,
                token).ConfigureAwait(false);

            AuthContext objectiveAuth = BuildAuth(objective);
            await _Objectives.LinkVoyageAsync(objectiveAuth, objective.Id, voyage.Id, token).ConfigureAwait(false);

            // Arm this voyage's Checks through the same seam the operator dispatch paths use. The
            // scheduler dispatches through the admiral directly rather than through
            // VoyageDispatchService, so without this call an autonomously dispatched voyage reaches
            // its Judge with no Check attached, and a Judge PASS is rejected for want of a green
            // independent Check that nothing was ever going to produce.
            Vessel? armingVessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (armingVessel == null)
            {
                _Logging.Warn(_Header + "could not arm Checks for voyage " + voyage.Id + ": vessel " + vesselId + " not found.");
            }
            else
            {
                VoyageCheckArmingService arming = new VoyageCheckArmingService(_Database, _Settings, _Logging);
                await arming.ArmAsync(voyage, armingVessel, "scheduler", token).ConfigureAwait(false);
            }

            await EmitObjectiveEventAsync("objective_scheduler.objective_dispatched",
                "Autonomous scheduler dispatched objective " + objective.Id + " as voyage " + voyage.Id + " on vessel " + vesselId + ".",
                objective, vesselId, token).ConfigureAwait(false);

            _Logging.Info(_Header + "dispatched objective " + objective.Id + " as voyage " + voyage.Id + " on vessel " + vesselId + ".");
        }

        private static string BuildMissionDescription(Objective objective)
        {
            StringBuilder sb = new StringBuilder();
            if (!String.IsNullOrWhiteSpace(objective.Description))
            {
                sb.AppendLine(objective.Description.Trim());
                sb.AppendLine();
            }

            if (objective.AcceptanceCriteria.Count > 0)
            {
                sb.AppendLine("## Acceptance Criteria");
                foreach (string criterion in objective.AcceptanceCriteria)
                    sb.AppendLine("- " + criterion);
                sb.AppendLine();
            }

            if (objective.NonGoals.Count > 0)
            {
                sb.AppendLine("## Non-Goals");
                foreach (string nonGoal in objective.NonGoals)
                    sb.AppendLine("- " + nonGoal);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static bool IsMergeTerminal(MergeStatusEnum status)
        {
            return status == MergeStatusEnum.Landed
                || status == MergeStatusEnum.Failed
                || status == MergeStatusEnum.Cancelled;
        }

        private async Task<bool> HasActiveLinkedVoyageAsync(Objective objective, CancellationToken token)
        {
            foreach (string voyageId in objective.VoyageIds)
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage != null && IsActiveVoyageStatus(voyage.Status))
                    return true;
            }

            return false;
        }

        private static bool IsActiveVoyageStatus(VoyageStatusEnum status)
        {
            return status == VoyageStatusEnum.Open || status == VoyageStatusEnum.InProgress;
        }

        private async Task EmitSystemEventAsync(string eventType, string message, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message)
                {
                    EntityType = "scheduler",
                    EntityId = "autonomous_objective_scheduler"
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit event " + eventType + ": " + ex.Message);
            }
        }

        private async Task EmitObjectiveEventAsync(string eventType, string message, Objective objective, string? vesselId, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message)
                {
                    TenantId = objective.TenantId,
                    UserId = objective.UserId,
                    EntityType = "objective",
                    EntityId = objective.Id,
                    VesselId = vesselId
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit event " + eventType + " for objective " + objective.Id + ": " + ex.Message);
            }
        }

        private async Task<List<Objective>> ReadAllObjectivesAsync(AuthContext auth, CancellationToken token)
        {
            List<Objective> all = new List<Objective>();
            int pageNumber = 1;
            const int pageSize = 500;
            while (true)
            {
                EnumerationResult<Objective> page = await _Objectives.EnumerateAsync(auth, new ObjectiveQuery
                {
                    PageNumber = pageNumber,
                    PageSize = pageSize
                }, token).ConfigureAwait(false);
                all.AddRange(page.Objects);
                if (page.Objects.Count < pageSize) break;
                pageNumber++;
            }

            return all;
        }

        private static AuthContext BuildSystemAuth()
        {
            return AuthContext.Authenticated(
                Constants.DefaultTenantId,
                Constants.DefaultUserId,
                true,
                true,
                "AutonomousObjectiveScheduler",
                principalDisplay: "Armada Autonomous Objective Scheduler");
        }

        private static AuthContext BuildAuth(Objective objective)
        {
            return AuthContext.Authenticated(
                objective.TenantId ?? Constants.DefaultTenantId,
                objective.UserId ?? Constants.DefaultUserId,
                false,
                true,
                "AutonomousObjectiveScheduler",
                principalDisplay: "Armada Autonomous Objective Scheduler");
        }

        private static void RecordSkip(Dictionary<string, int> skipReasons, string reason)
        {
            string key = String.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
            skipReasons.TryGetValue(key, out int count);
            skipReasons[key] = count + 1;
        }

        /// <summary>
        /// Render skip counts as a stable, readable summary, for example
        /// "multi_vessel=2,backpressure=1". Ordered by count then name so the same sweep
        /// always produces the same string.
        /// </summary>
        private static string DescribeSkips(Dictionary<string, int> skipReasons)
        {
            List<KeyValuePair<string, int>> ordered = skipReasons.ToList();
            ordered.Sort((left, right) =>
            {
                int byCount = right.Value.CompareTo(left.Value);
                return byCount != 0 ? byCount : String.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            List<string> parts = new List<string>(ordered.Count);
            foreach (KeyValuePair<string, int> entry in ordered)
                parts.Add(entry.Key + "=" + entry.Value.ToString(CultureInfo.InvariantCulture));
            return String.Join(",", parts);
        }

        /// <summary>
        /// Thrown when one objective cannot be dispatched on this sweep. The reason
        /// travels WITH the exception: it was previously discarded at the catch, so a
        /// sweep that skipped every eligible objective reported dispatched=0 with a null
        /// skip reason and no event, and the cause was invisible to the operator.
        /// </summary>
        private sealed class ObjectiveSkippedException : Exception
        {
            /// <summary>Short machine-readable skip reason, for example "multi_vessel".</summary>
            public string Reason { get; }

            /// <summary>
            /// Instantiate.
            /// </summary>
            /// <param name="reason">Short machine-readable skip reason.</param>
            public ObjectiveSkippedException(string reason) : base("objective skipped: " + reason)
            {
                Reason = String.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
            }
        }

        private sealed class ActiveVoyageSummary
        {
            public int Total { get; set; }

            public Dictionary<string, int> ByVessel { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion
    }
}
