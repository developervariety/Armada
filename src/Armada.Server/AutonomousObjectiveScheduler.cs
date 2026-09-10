namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
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
        // The fleet-wide dispatch hold, read once per sweep so an engaged hold is reported by its own
        // name and never as a dispatch fault. Null in tests that do not model a hold.
        private readonly DispatchHold? _DispatchHold;

        private readonly DatabaseDriver _Database;
        private readonly ObjectiveService _Objectives;
        private readonly IAdmiralService _Admiral;
        private readonly IMergeQueueService _MergeQueue;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly ICodeIndexService? _CodeIndex;
        private readonly IObjectiveDispatchPreviewService? _ObjectiveDispatchPreview;
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
        /// <param name="dispatchHold">Optional fleet-wide dispatch hold.</param>
        /// <param name="objectiveDispatchPreview">Optional shared objective dispatch preflight.</param>
        public AutonomousObjectiveScheduler(
            DatabaseDriver database,
            ObjectiveService objectives,
            IAdmiralService admiral,
            IMergeQueueService mergeQueue,
            ArmadaSettings settings,
            LoggingModule logging,
            ICodeIndexService? codeIndex = null,
            DispatchHold? dispatchHold = null,
            IObjectiveDispatchPreviewService? objectiveDispatchPreview = null)
        {
            _DispatchHold = dispatchHold;
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _MergeQueue = mergeQueue ?? throw new ArgumentNullException(nameof(mergeQueue));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _CodeIndex = codeIndex;
            _ObjectiveDispatchPreview = objectiveDispatchPreview;

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
                List<Objective> eligible = _ObjectiveDispatchPreview == null
                    ? AutonomousObjectiveSelector.SelectEligible(snapshot)
                    : AutonomousObjectiveSelector.SelectCandidates(snapshot);

                // Diagnose dependency blocks before global capacity, dispatch holds, or sibling-lane
                // occupancy can hide them. Cache each preview so a candidate that reaches dispatch is
                // not evaluated twice in one sweep.
                Dictionary<string, ObjectiveDispatchPreview> previews = new Dictionary<string, ObjectiveDispatchPreview>(StringComparer.Ordinal);
                Dictionary<string, int> skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
                if (_ObjectiveDispatchPreview != null)
                {
                    List<Objective> dependencyReady = new List<Objective>();
                    foreach (Objective objective in eligible)
                    {
                        ObjectiveDispatchPreview preview = await _ObjectiveDispatchPreview
                            .PreviewAsync(BuildAuth(objective), objective, token: token)
                            .ConfigureAwait(false);
                        previews[objective.Id] = preview;
                        if (IsDependencyBlocked(preview))
                        {
                            RecordSkip(skipReasons, "dependency_blocked");
                            await EmitDispatchPreviewSkipAsync(objective, preview, true, token).ConfigureAwait(false);
                            continue;
                        }

                        dependencyReady.Add(objective);
                    }
                    eligible = dependencyReady;
                }

                ActiveVoyageSummary active = await CountActiveDispatchedAsync(token).ConfigureAwait(false);
                ActiveDispatchedCount = active.Total;
                int capacity = MaxConcurrentVoyages - active.Total;

                if (eligible.Count == 0 && skipReasons.Count > 0)
                {
                    LastSkipReason = DescribeSkips(skipReasons);
                    LastResultSummary = "reconciled=" + reconciledCount + " dispatched=0 skipped=" + LastSkipReason;
                    _Logging.Info(_Header + "sweep complete: " + LastResultSummary + ".");
                    return;
                }

                if (capacity <= 0)
                {
                    string concurrencyDetail = active.Total + " active voyage(s) contain repository work, "
                        + "including unlinked voyages dispatched by an operator; limit is " + MaxConcurrentVoyages;
                    _Logging.Debug(_Header + "sweep: concurrency limit reached (" + concurrencyDetail + ").");
                    await EmitSystemEventAsync("objective_scheduler.skipped_max_concurrent",
                        "Autonomous objective scheduler dispatch skipped: " + concurrencyDetail + ".", token).ConfigureAwait(false);
                    if (skipReasons.Count == 0)
                    {
                        LastSkipReason = "max_concurrent";
                        LastResultSummary = "reconciled=" + reconciledCount + " dispatched=0 (max_concurrent)";
                    }
                    else
                    {
                        RecordSkip(skipReasons, "max_concurrent");
                        LastSkipReason = DescribeSkips(skipReasons);
                        LastResultSummary = "reconciled=" + reconciledCount + " dispatched=0 skipped=" + LastSkipReason;
                    }
                    return;
                }

                // An engaged dispatch hold refuses every dispatch, so it is read ONCE here rather than
                // discovered per candidate as an exception. Reported by name: while a deploy window
                // is open, LastSkipReason must not read as a fault, or a real fault arriving during
                // the window is indistinguishable from the hold and is never investigated.
                DispatchHoldSnapshot? hold = _DispatchHold?.Snapshot();
                if (hold != null)
                {
                    string holdDetail = "dispatch hold engaged by " + (String.IsNullOrWhiteSpace(hold.SetBy) ? "unknown" : hold.SetBy)
                        + " at " + hold.SetByUtc.ToString("u") + ": " + hold.Reason;
                    _Logging.Info(_Header + "sweep: " + holdDetail + "; " + eligible.Count + " eligible objective(s) wait.");
                    await EmitSystemEventAsync("objective_scheduler.skipped_dispatch_hold",
                        "Autonomous objective scheduler dispatch skipped: " + holdDetail + ".", token).ConfigureAwait(false);
                    if (skipReasons.Count == 0)
                    {
                        LastSkipReason = "dispatch_hold";
                        LastResultSummary = "reconciled=" + reconciledCount + " dispatched=0 (dispatch_hold)";
                    }
                    else
                    {
                        RecordSkip(skipReasons, "dispatch_hold");
                        LastSkipReason = DescribeSkips(skipReasons);
                        LastResultSummary = "reconciled=" + reconciledCount + " dispatched=0 skipped=" + LastSkipReason;
                    }
                    return;
                }

                int dispatched = 0;
                int vesselConcurrencySkips = 0;
                // Every skip is counted by reason. A sweep that dispatches nothing must be
                // able to say why; reporting dispatched=0 with no reason reads as an idle
                // fleet, and hid two permanently undispatchable objectives for days.
                VesselLaneMap lanes = await BuildLanesAsync(token).ConfigureAwait(false);
                List<MergeEntry> mergeQueue = await _MergeQueue.ListAsync(token: token).ConfigureAwait(false);

                foreach (Objective objective in eligible)
                {
                    if (dispatched >= capacity) break;
                    token.ThrowIfCancellationRequested();

                    if (objective.VesselIds.Count == 1)
                    {
                        string candidateVesselId = objective.VesselIds[0];
                        // The per-vessel ceiling applies to the LANE: every vessel joined to this
                        // one by a build-participating sibling declaration. A wave that writes a
                        // consumer's call sites is a writer on the consumer too, and the ceiling
                        // could not see that when it counted the owning vessel alone.
                        IReadOnlySet<string> lane = lanes.MembersFor(candidateVesselId);
                        int activeOnVessel = CountActiveInLane(active, lane);
                        if (activeOnVessel >= MaxConcurrentVoyagesPerVessel)
                        {
                            vesselConcurrencySkips++;
                            bool sharedLane = lane.Count > 1;
                            string laneName = String.Join("+", lane.OrderBy(v => v, StringComparer.Ordinal));
                            RecordSkip(skipReasons, sharedLane ? "lane_busy:" + laneName : "vessel_concurrency");
                            await EmitObjectiveEventAsync(
                                sharedLane ? "objective_scheduler.skipped_lane_busy" : "objective_scheduler.skipped_vessel_concurrency",
                                "Autonomous scheduler skipped objective " + objective.Id + ": "
                                    + (sharedLane ? "lane " + laneName : "vessel " + candidateVesselId)
                                    + " already has " + activeOnVessel
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
                        previews.TryGetValue(objective.Id, out ObjectiveDispatchPreview? preview);
                        await DispatchObjectiveAsync(objective, mergeQueue, preview, token).ConfigureAwait(false);
                        dispatched++;
                        if (objective.VesselIds.Count == 1)
                        {
                            string dispatchedVesselId = objective.VesselIds[0];
                            active.Add("sweep:" + objective.Id, new[] { dispatchedVesselId });
                        }
                    }
                    catch (ObjectiveSkippedException skipped)
                    {
                        RecordSkip(skipReasons, skipped.Reason);
                    }
                    catch (StartFromRefMissingException missing)
                    {
                        // A ref that does not resolve is the objective's fault, not the fleet's.
                        // Report it by name so an operator can correct the row; a generic
                        // dispatch_error would send them looking at the captains.
                        RecordSkip(skipReasons, "start_from_ref_missing");
                        _Logging.Warn(_Header + "objective " + objective.Id + " skipped: " + missing.Message);
                        await EmitObjectiveEventAsync("objective_scheduler.start_from_ref_missing",
                            "Autonomous scheduler skipped objective " + objective.Id + ": " + missing.Message,
                            objective, null, token).ConfigureAwait(false);
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
            Dictionary<string, Voyage> voyagesById = new Dictionary<string, Voyage>(StringComparer.Ordinal);
            Dictionary<string, List<Mission>> missionsByVoyage = new Dictionary<string, List<Mission>>(StringComparer.Ordinal);
            foreach (string voyageId in objective.VoyageIds.Distinct(StringComparer.Ordinal))
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage == null) return false;
                voyagesById[voyage.Id] = voyage;
                missionsByVoyage[voyage.Id] = await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            }

            Dictionary<string, Mission> missionsById = missionsByVoyage.Values
                .SelectMany(missions => missions)
                .Where(mission => !String.IsNullOrWhiteSpace(mission.Id))
                .ToDictionary(mission => mission.Id, mission => mission, StringComparer.Ordinal);
            Dictionary<string, Mission> dependencyMissionsById = new Dictionary<string, Mission>(missionsById, StringComparer.Ordinal);
            Queue<string> dependencyIds = new Queue<string>(missionsById.Values
                .Select(mission => mission.DependsOnMissionId)
                .Where(id => !String.IsNullOrWhiteSpace(id))
                .Select(id => id!));
            while (dependencyIds.Count > 0)
            {
                string dependencyId = dependencyIds.Dequeue();
                if (dependencyMissionsById.ContainsKey(dependencyId)) continue;
                Mission? dependency = await _Database.Missions.ReadAsync(dependencyId, token).ConfigureAwait(false);
                if (dependency == null) return false;
                dependencyMissionsById[dependency.Id] = dependency;
                if (!String.IsNullOrWhiteSpace(dependency.DependsOnMissionId))
                    dependencyIds.Enqueue(dependency.DependsOnMissionId);
            }

            // Rescue attempts are evidence for an original chain, not new objective obligations.
            // Every rescue root points through ParentMissionId to a mission in another linked
            // voyage. A failed historical rescue must not keep the objective open after a later
            // attempt resolves the original failure.
            HashSet<string> rescueVoyageIds = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string voyageId, List<Mission> missions) in missionsByVoyage)
            {
                if (missions.Any(mission =>
                    RescueMissionMarker.IsAutoRescue(mission)
                    && !String.IsNullOrWhiteSpace(mission.ParentMissionId)
                    && missionsById.TryGetValue(mission.ParentMissionId, out Mission? parent)
                    && (parent.Status == MissionStatusEnum.Failed
                        || parent.Status == MissionStatusEnum.LandingFailed)
                    && !String.Equals(parent.VoyageId, voyageId, StringComparison.Ordinal)))
                {
                    rescueVoyageIds.Add(voyageId);
                }
            }

            List<Voyage> originalVoyages = voyagesById.Values
                .Where(voyage => !rescueVoyageIds.Contains(voyage.Id))
                .ToList();
            if (originalVoyages.Count == 0) return false;

            bool anyComplete = voyagesById.Values.Any(voyage => voyage.Status == VoyageStatusEnum.Complete);
            foreach (Voyage voyage in originalVoyages)
            {
                if (voyage.Status == VoyageStatusEnum.Complete)
                {
                    if (!IsSuccessfulVoyageMissionGraph(missionsByVoyage[voyage.Id], dependencyMissionsById)) return false;
                    continue;
                }

                // A voyage still running blocks completion.
                if (IsActiveVoyageStatus(voyage.Status)) return false;

                if (!AreFailedChainsRecovered(
                    missionsByVoyage[voyage.Id],
                    rescueVoyageIds,
                    voyagesById,
                    missionsByVoyage,
                    dependencyMissionsById)) return false;
            }

            // Require at least one voyage to have actually landed: an objective all of whose voyages
            // are Cancelled (nothing landed) is not complete.
            return anyComplete;
        }

        private static bool IsSuccessfulVoyageMissionGraph(
            List<Mission> missions,
            Dictionary<string, Mission> dependencyMissionsById)
        {
            if (missions.Count == 0 || !HasValidMissionDependencies(missions, dependencyMissionsById)) return false;
            HashSet<string> localIds = missions.Select(mission => mission.Id).ToHashSet(StringComparer.Ordinal);
            return missions.All(mission => mission.Status == MissionStatusEnum.Complete)
                && DependenciesOutsideVoyageAreComplete(missions, localIds, dependencyMissionsById);
        }

        private static bool HasValidMissionDependencies(
            List<Mission> missions,
            Dictionary<string, Mission> dependencyMissionsById)
        {
            if (missions.Any(mission => String.IsNullOrWhiteSpace(mission.Id))) return false;

            foreach (Mission mission in missions)
            {
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                Mission current = mission;
                while (!String.IsNullOrWhiteSpace(current.DependsOnMissionId))
                {
                    if (!visited.Add(current.Id)
                        || !dependencyMissionsById.TryGetValue(current.DependsOnMissionId, out Mission? dependency)) return false;
                    current = dependency;
                }
            }
            return true;
        }

        private static bool DependenciesOutsideVoyageAreComplete(
            List<Mission> missions,
            HashSet<string> localIds,
            Dictionary<string, Mission> dependencyMissionsById)
        {
            foreach (Mission mission in missions)
            {
                Mission current = mission;
                while (!String.IsNullOrWhiteSpace(current.DependsOnMissionId)
                    && dependencyMissionsById.TryGetValue(current.DependsOnMissionId, out Mission? dependency))
                {
                    if (!localIds.Contains(dependency.Id) && dependency.Status != MissionStatusEnum.Complete) return false;
                    current = dependency;
                }
            }
            return true;
        }

        /// <summary>
        /// Require a completed linked rescue for every actual failure anchor in one original
        /// voyage. Cancelled dependents are consequences of an upstream failure and do not create
        /// extra obligations. A cancelled-only voyage has no proven recovery and fails closed.
        /// </summary>
        private static bool AreFailedChainsRecovered(
            List<Mission> originalMissions,
            HashSet<string> rescueVoyageIds,
            Dictionary<string, Voyage> voyagesById,
            Dictionary<string, List<Mission>> missionsByVoyage,
            Dictionary<string, Mission> dependencyMissionsById)
        {
            if (originalMissions.Count == 0
                || !HasValidMissionDependencies(originalMissions, dependencyMissionsById)) return false;

            Dictionary<string, Mission> originalsById = originalMissions
                .Where(mission => !String.IsNullOrWhiteSpace(mission.Id))
                .ToDictionary(mission => mission.Id, mission => mission, StringComparer.Ordinal);
            foreach (Mission cancelled in originalMissions.Where(mission => mission.Status == MissionStatusEnum.Cancelled))
            {
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                Mission current = cancelled;
                bool causedByFailure = false;
                while (!String.IsNullOrWhiteSpace(current.DependsOnMissionId)
                    && visited.Add(current.Id)
                    && originalsById.TryGetValue(current.DependsOnMissionId, out Mission? dependency))
                {
                    if (dependency.Status == MissionStatusEnum.Failed
                        || dependency.Status == MissionStatusEnum.LandingFailed)
                    {
                        causedByFailure = true;
                        break;
                    }
                    current = dependency;
                }
                if (!causedByFailure) return false;
            }

            HashSet<string> failureAnchors = originalMissions
                .Where(mission => mission.Status == MissionStatusEnum.Failed
                    || mission.Status == MissionStatusEnum.LandingFailed)
                .Where(mission => !HasFailureAncestor(mission, originalsById))
                .Select(mission => mission.Id)
                .Where(id => !String.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            if (failureAnchors.Count == 0) return false;

            HashSet<string> failureAncestorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string failureAnchor in failureAnchors)
            {
                Mission current = originalsById[failureAnchor];
                while (!String.IsNullOrWhiteSpace(current.DependsOnMissionId)
                    && originalsById.TryGetValue(current.DependsOnMissionId, out Mission? dependency))
                {
                    failureAncestorIds.Add(dependency.Id);
                    current = dependency;
                }
            }
            if (originalMissions.Any(mission => mission.Status != MissionStatusEnum.Complete
                && mission.Status != MissionStatusEnum.Failed
                && mission.Status != MissionStatusEnum.LandingFailed
                && mission.Status != MissionStatusEnum.Cancelled
                && (mission.Status != MissionStatusEnum.WorkProduced || !failureAncestorIds.Contains(mission.Id)))) return false;

            List<Mission> successfulRescueRoots = rescueVoyageIds
                .Where(voyageId => voyagesById[voyageId].Status == VoyageStatusEnum.Complete
                    && IsSuccessfulVoyageMissionGraph(missionsByVoyage[voyageId], dependencyMissionsById))
                .SelectMany(voyageId => missionsByVoyage[voyageId])
                .Where(mission => RescueMissionMarker.IsAutoRescue(mission)
                    && mission.Status == MissionStatusEnum.Complete
                    && !String.IsNullOrWhiteSpace(mission.ParentMissionId))
                .ToList();

            Dictionary<string, Mission> allMissionsById = missionsByVoyage.Values
                .SelectMany(missions => missions)
                .Where(mission => !String.IsNullOrWhiteSpace(mission.Id))
                .GroupBy(mission => mission.Id, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            HashSet<string> recoveredAnchors = new HashSet<string>(StringComparer.Ordinal);
            foreach (Mission rescueRoot in successfulRescueRoots)
            {
                string? parentId = rescueRoot.ParentMissionId;
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                while (!String.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
                {
                    if (failureAnchors.Contains(parentId))
                    {
                        recoveredAnchors.Add(parentId);
                        break;
                    }
                    if (!allMissionsById.TryGetValue(parentId, out Mission? parent)
                        || !RescueMissionMarker.IsAutoRescue(parent)) break;
                    parentId = parent.ParentMissionId;
                }
            }

            return failureAnchors.IsSubsetOf(recoveredAnchors);
        }

        private static bool HasFailureAncestor(Mission mission, Dictionary<string, Mission> missionsById)
        {
            Mission current = mission;
            while (!String.IsNullOrWhiteSpace(current.DependsOnMissionId)
                && missionsById.TryGetValue(current.DependsOnMissionId, out Mission? dependency))
            {
                if (dependency.Status == MissionStatusEnum.Failed
                    || dependency.Status == MissionStatusEnum.LandingFailed) return true;
                current = dependency;
            }
            return false;
        }

        /// <summary>
        /// Two vessels are one lane when either declares the other as a sibling its build or tests
        /// can touch (<see cref="SiblingRepo.BuildParticipant"/>); lanes are the transitive closure
        /// of that relation. A read-only sibling (a decompiled or artifact tree) joins no lane, so a
        /// research objective on a glossary vessel never waits for a voyage on the port it reads.
        /// </summary>
        private async Task<VesselLaneMap> BuildLanesAsync(CancellationToken token)
        {
            List<Vessel> vessels;
            try
            {
                vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read vessels for lane derivation; counting each vessel alone: " + ex.Message);
                return VesselLaneMap.Build(Array.Empty<Vessel>());
            }
            return VesselLaneMap.Build(vessels,
                message => _Logging.Warn(_Header + message));
        }

        private static int CountActiveInLane(ActiveVoyageSummary active, IReadOnlySet<string> lane)
        {
            HashSet<string> voyageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string vesselId in lane)
            {
                if (active.VoyageIdsByVessel.TryGetValue(vesselId, out HashSet<string>? activeOnVessel))
                    voyageIds.UnionWith(activeOnVessel);
            }
            return voyageIds.Count;
        }

        private static bool IsDependencyBlocked(ObjectiveDispatchPreview preview)
        {
            return preview.Issues.Any(issue =>
                        String.Equals(issue.Code, "objective_dependencies_incomplete", StringComparison.Ordinal)
                        || String.Equals(issue.Code, "objective_dependency_missing", StringComparison.Ordinal)
                        || String.Equals(issue.Code, "objective_dependency_cycle", StringComparison.Ordinal));
        }

        private async Task EmitDispatchPreviewSkipAsync(
            Objective objective,
            ObjectiveDispatchPreview preview,
            bool dependencyBlocked,
            CancellationToken token)
        {
            string issueCodes = String.Join(", ", preview.Issues
                .Where(issue => issue.Severity == ReadinessSeverityEnum.Error)
                .Select(issue => issue.Code));
            string chains = preview.BlockingChains.Count > 0
                ? " Blocking chains: " + String.Join("; ", preview.BlockingChains.Select(chain => String.Join(" -> ", chain))) + "."
                : String.Empty;
            await EmitObjectiveEventAsync(
                dependencyBlocked ? "objective_scheduler.skipped_dependency" : "objective_scheduler.skipped_dispatch_preflight",
                "Autonomous scheduler skipped objective " + objective.Id + ": " + issueCodes + "." + chains,
                objective,
                preview.VesselId,
                token).ConfigureAwait(false);
        }

        private async Task<ActiveVoyageSummary> CountActiveDispatchedAsync(CancellationToken token)
        {
            ActiveVoyageSummary summary = new ActiveVoyageSummary();
            List<Voyage> voyages = (await _Database.Voyages
                .EnumerateByStatusAsync(VoyageStatusEnum.Open, token).ConfigureAwait(false))
                .Concat(await _Database.Voyages
                    .EnumerateByStatusAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false))
                .GroupBy(voyage => voyage.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            foreach (Voyage voyage in voyages)
            {
                List<MissionSummary> missions = await _Database.Missions
                    .EnumerateMissionSummariesByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
                HashSet<string> vesselIds = missions
                    .Where(mission => !String.IsNullOrWhiteSpace(mission.VesselId))
                    .Select(mission => mission.VesselId!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (vesselIds.Count == 0) continue;

                summary.Total++;
                summary.Add(voyage.Id, vesselIds);
            }

            return summary;
        }

        private async Task DispatchObjectiveAsync(
            Objective objective,
            List<MergeEntry> mergeQueue,
            ObjectiveDispatchPreview? cachedPreview,
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

            if (_ObjectiveDispatchPreview != null)
            {
                ObjectiveDispatchPreview preview = cachedPreview ?? await _ObjectiveDispatchPreview
                    .PreviewAsync(BuildAuth(objective), objective, token: token)
                    .ConfigureAwait(false);
                if (!preview.IsReady)
                {
                    bool dependencyBlocked = IsDependencyBlocked(preview);
                    string reason = dependencyBlocked ? "dependency_blocked" : "dispatch_preflight";
                    await EmitDispatchPreviewSkipAsync(objective, preview, dependencyBlocked, token).ConfigureAwait(false);
                    throw new ObjectiveSkippedException(reason);
                }
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

            string missionDescription = ObjectiveBriefRenderer.Render(objective);
            MissionDescription md = new MissionDescription(objective.Title, missionDescription)
            {
                CodeContextMode = _Settings.CodeIndex.Enabled ? "auto" : "off",
                StartFromRef = objective.StartFromRef,
                Mode = DeriveMissionMode(objective.Kind)
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

        /// <summary>
        /// Derive the mission mode for an autonomously dispatched objective from its Kind. Delegates
        /// to the shared <see cref="MissionModes.FromObjectiveKind"/> so the autonomous scheduler and
        /// the operator dispatch path apply one rule: a Research objective runs read-only (no commit
        /// required, the Judge accepts an unchanged branch), and every other Kind keeps the
        /// Implementation default.
        /// </summary>
        /// <param name="kind">The objective kind.</param>
        /// <returns>"Research" for a Research objective, otherwise null (Implementation default).</returns>
        public static string? DeriveMissionMode(ObjectiveKindEnum kind)
        {
            return MissionModes.FromObjectiveKind(kind);
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

            public Dictionary<string, HashSet<string>> VoyageIdsByVessel { get; } =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            public void Add(string voyageId, IEnumerable<string> vesselIds)
            {
                foreach (string vesselId in vesselIds)
                {
                    if (!VoyageIdsByVessel.TryGetValue(vesselId, out HashSet<string>? voyages))
                    {
                        voyages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        VoyageIdsByVessel[vesselId] = voyages;
                    }
                    voyages.Add(voyageId);
                }
            }
        }

        #endregion
    }
}
