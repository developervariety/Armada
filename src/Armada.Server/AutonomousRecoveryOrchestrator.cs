namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
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
    /// Armada-native policy service for failed mission incidents, runbook records, rescue dispatch,
    /// and bounded Mail nudges for live stalled work.
    /// </summary>
    public sealed class AutonomousRecoveryOrchestrator
    {
        private const string _Header = "[AutonomousRecoveryOrchestrator] ";
        private const string _RecoveryRunbookFileName = "system/mission-recovery.md";
        // The literal lives in Armada.Core.Services.RescueMissionMarker; this alias keeps the
        // existing call sites readable without giving the rule a second definition.
        private const string _RescueMarker = RescueMissionMarker.Marker;
        private const string _NudgeMarker = "[ARMADA_AUTO_NUDGE]";

        /// <summary>
        /// Decision reason recorded when a rescue failed its definition-of-done gate on the same
        /// complete set of tests as its parent, so a further rescue would repeat an unchanged failure.
        /// </summary>
        internal const string RepeatedIdenticalTestFailureReason = "repeated_identical_test_failure";

        /// <summary>
        /// Decision reason recorded when the failed stage ended with <c>[ARMADA:RESULT] BLOCKED</c>: it waits on
        /// an owner answer, and a rescue would re-run it without one.
        /// </summary>
        internal const string BlockedQuestionRecoveryReason =
            "captain_blocked: the stage ended with [ARMADA:RESULT] BLOCKED on a question only the owner can answer; a rescue would re-run it without the answer";

        private static readonly Regex _JudgePassLinePattern = new Regex(
            @"^\[(?:ARMADA:)?VERDICT\]\s+PASS\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly IncidentService _Incidents;
        private readonly RunbookService _Runbooks;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly IMergeQueueService? _MergeQueue;
        private readonly IGitService? _Git;
        private readonly IAutoLandEvaluator? _AutoLandEvaluator;
        private readonly IConventionChecker? _ConventionChecker;
        private readonly ICriticalTriggerEvaluator? _CriticalTriggerEvaluator;
        private readonly ProviderProgressTracker? _ProviderProgress;
        // Per-mission policy gates. An entry lives only while a policy application for that mission
        // holds or waits on it, so the map never grows with the number of missions ever handled.
        private readonly Dictionary<string, MissionLockEntry> _MissionLocks = new Dictionary<string, MissionLockEntry>(StringComparer.Ordinal);
        private readonly object _MissionLocksSync = new object();
        private readonly SemaphoreSlim _SweepLock = new SemaphoreSlim(1, 1);
        private int _LandingDrainNoDockCount = 0;
        private int _LandingDrainDiffFailedCount = 0;
        private readonly CheckRunService? _CheckRuns;
        private readonly Func<Mission, string, string?, CancellationToken, Task<JudgeFollowUp>> _CaptureJudgeFollowUp;
        private readonly DispatchHold? _DispatchHold;
        private readonly TerminalMarkerTracker? _TerminalMarkers;
        private readonly CaptainStallEvaluator _StallEvaluator;

        /// <summary>
        /// The D1 <c>failure_cause</c> typed-decision adapter, when wired. Null keeps recovery on its
        /// deterministic classifier alone. Set by the server after construction so the many existing
        /// construction sites and tests are unchanged. The adapter can only hold a rescue the rule
        /// would have dispatched; every rule hard-block still wins.
        /// </summary>
        public TypedFailureCauseAdapter? FailureCauseAdapter { get; set; }

        // Missions whose withheld nudge already produced an event, so a finished captain yields one
        // event rather than one per sweep tick; every withheld nudge is still counted and logged.
        // An entry is dropped once no Working captain holds the mission.
        private readonly ConcurrentDictionary<string, byte> _NudgeSuppressedMissions =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // Rescues refused by an engaged dispatch hold, keyed by failed mission id. Each entry
        // records which hold engagement refused it, so the refusal is written to the incident once
        // per engagement, and the first sweep after the hold clears re-evaluates every entry even
        // when the failure has aged out of the sweep's lookback window.
        private readonly ConcurrentDictionary<string, HoldDeferredRescue> _HoldDeferredRescues =
            new ConcurrentDictionary<string, HoldDeferredRescue>(StringComparer.Ordinal);

        /// <summary>
        /// Instantiate.
        /// </summary>
        public AutonomousRecoveryOrchestrator(
            DatabaseDriver database,
            IAdmiralService admiral,
            IncidentService incidents,
            RunbookService runbooks,
            ArmadaSettings settings,
            LoggingModule logging)
            : this(database, admiral, incidents, runbooks, settings, logging, null, null, null, null, null, null, null)
        {
        }

        /// <summary>
        /// Instantiate with optional landing-drain dependencies.
        /// </summary>
        public AutonomousRecoveryOrchestrator(
            DatabaseDriver database,
            IAdmiralService admiral,
            IncidentService incidents,
            RunbookService runbooks,
            ArmadaSettings settings,
            LoggingModule logging,
            IMergeQueueService? mergeQueue,
            IGitService? git,
            IAutoLandEvaluator? autoLandEvaluator,
            IConventionChecker? conventionChecker,
            ICriticalTriggerEvaluator? criticalTriggerEvaluator)
            : this(database, admiral, incidents, runbooks, settings, logging, mergeQueue, git, autoLandEvaluator, conventionChecker, criticalTriggerEvaluator, null, null)
        {
        }

        /// <summary>
        /// Instantiate with optional landing-drain dependencies and a provider-progress tracker.
        /// </summary>
        /// <remarks>
        /// The tracker is consulted by <see cref="NudgeStalledLiveCaptainsAsync"/> so a captain
        /// whose OS process is alive (heartbeat fresh) but whose provider-progress signal has
        /// gone silent is classified as a <see cref="ProviderStallKind.ProviderSilentStall"/>
        /// rather than the existing heartbeat-stall path. The Mail nudge payload carries the
        /// classified kind so operator / runbook readers can distinguish provider silence from
        /// a captain-wide stall. When the tracker is null the nudge path falls back to the
        /// historical heartbeat-only behavior.
        /// </remarks>
        public AutonomousRecoveryOrchestrator(
            DatabaseDriver database,
            IAdmiralService admiral,
            IncidentService incidents,
            RunbookService runbooks,
            ArmadaSettings settings,
            LoggingModule logging,
            IMergeQueueService? mergeQueue,
            IGitService? git,
            IAutoLandEvaluator? autoLandEvaluator,
            IConventionChecker? conventionChecker,
            ICriticalTriggerEvaluator? criticalTriggerEvaluator,
            ProviderProgressTracker? providerProgress,
            CheckRunService? checkRuns = null,
            Func<Mission, string, string?, CancellationToken, Task<JudgeFollowUp>>? captureJudgeFollowUp = null,
            DispatchHold? dispatchHold = null,
            TerminalMarkerTracker? terminalMarkers = null)
        {
            _DispatchHold = dispatchHold;
            _TerminalMarkers = terminalMarkers;
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
            _Runbooks = runbooks ?? throw new ArgumentNullException(nameof(runbooks));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _MergeQueue = mergeQueue;
            _Git = git;
            _AutoLandEvaluator = autoLandEvaluator;
            _ConventionChecker = conventionChecker;
            _CriticalTriggerEvaluator = criticalTriggerEvaluator;
            _ProviderProgress = providerProgress;
            _StallEvaluator = new CaptainStallEvaluator(_Database, _Git, _Logging);
            _CheckRuns = checkRuns;
            _CaptureJudgeFollowUp = captureJudgeFollowUp
                ?? ((mission, verdict, recommendation, token) =>
                    new JudgeFollowUpService(_Database, _Logging)
                        .CaptureAsync(mission, verdict, recommendation, token));
        }

        /// <summary>
        /// Handle a mission outcome emitted by MissionService.
        /// </summary>
        public async Task HandleMissionOutcomeAsync(Mission mission, bool willInvokeLandingHandler, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (!_Settings.AutonomousRecovery.Enabled) return;
            if (!IsRecoverableTerminalStatus(mission.Status)) return;

            await ApplyFailurePolicyAsync(mission.TenantId, mission.Id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Fire-and-forget heartbeat maintenance. This method never blocks the caller.
        /// </summary>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            if (!_Settings.AutonomousRecovery.Enabled) return;

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
        /// Run one bounded recovery maintenance pass.
        /// </summary>
        public async Task SweepAsync(CancellationToken token = default)
        {
            if (!_Settings.AutonomousRecovery.Enabled) return;
            if (!await _SweepLock.WaitAsync(0, token).ConfigureAwait(false)) return;

            try
            {
                await NudgeStalledLiveCaptainsAsync(token).ConfigureAwait(false);
                await ProcessRecentFailedMissionsAsync(token).ConfigureAwait(false);
                await ProcessLandingDrainOpenVoyagesAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _SweepLock.Release();
            }
        }

        private async Task ProcessRecentFailedMissionsAsync(CancellationToken token)
        {
            await ReevaluateHoldDeferredRescuesAsync(token).ConfigureAwait(false);

            DateTime cutoff = DateTime.UtcNow.AddHours(-_Settings.AutonomousRecovery.FailedMissionLookbackHours);

            // Enumerate lightweight summaries (id/status/tenant/last-update only) rather than
            // fully-hydrated Mission rows. This sweep runs every health-check tick (~5s); the
            // database can hold thousands of terminal Failed missions whose description /
            // agent_output / diff_snapshot columns are each tens-of-KB-to-MB strings. Loading
            // the full rows allocated hundreds of MB on the LOH every tick (observed via
            // dotMemory as 0.34-1.89 GB/s "Fast LOH growth" bursts). The candidate only needs
            // id + tenant to re-read the single mission it actually acts on, so summaries are
            // sufficient and ~450x smaller. ApplyFailurePolicyAsync re-reads the full mission
            // fresh before doing anything with it.
            List<MissionSummary> candidates = new List<MissionSummary>();
            candidates.AddRange(await EnumerateAllSummariesByStatusAsync(MissionStatusEnum.Failed, token).ConfigureAwait(false));
            candidates.AddRange(await EnumerateAllSummariesByStatusAsync(MissionStatusEnum.LandingFailed, token).ConfigureAwait(false));

            // Terminal parent voyages (Cancelled / Complete) never need autonomous recovery; selecting
            // them every ~5s tick re-ran the cancelled-voyage suppression path and spammed events. Filter
            // them out before consuming a selection slot. The per-sweep cache keyed by voyage id keeps the
            // filter bounded -- each distinct voyage is read at most once per sweep, never re-hydrating
            // full mission rows.
            Dictionary<string, bool> closedVoyageCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            int processed = 0;
            int reconciledSkipped = 0;

            foreach (MissionSummary candidate in candidates
                .Where(item => item.LastUpdateUtc >= cutoff)
                .OrderBy(item => item.LastUpdateUtc))
            {
                if (processed >= 10) break;
                token.ThrowIfCancellationRequested();

                // A mission the terminal-voyage reconciler closed is a record of an ended voyage, not a
                // failure of its own; it never enters recovery. Checked on the summary so the sweep reads
                // no full row for it, and again in ApplyFailurePolicyAsync for every other entry point.
                if (TerminalVoyageMissionRule.IsReconciledOutcome(candidate))
                {
                    reconciledSkipped++;
                    continue;
                }

                if (await IsRecoveryClosedVoyageAsync(candidate.VoyageId, closedVoyageCache, token).ConfigureAwait(false))
                    continue;

                // Exclude auto-rescue missions: they can never be rescued again (Classify returns
                // Blocked for any auto-rescue). Processing them only re-opens a High incident on
                // every sweep tick, generating repeated operator toil with no recovery value.
                // ParentMissionId is set exclusively by rescue dispatch and is present on the
                // lightweight summary, avoiding a full description load at this stage.
                if (!String.IsNullOrWhiteSpace(candidate.ParentMissionId))
                    continue;

                // Voyage-less failures older than the configured window are unlikely to need
                // autonomous recovery and sweeping them indefinitely generates stale incidents.
                // Age is measured by CompletedUtc when available, LastUpdateUtc otherwise.
                if (String.IsNullOrWhiteSpace(candidate.VoyageId)
                    && _Settings.AutonomousRecovery.RecoverySweepMaxFailedMissionAgeHours > 0)
                {
                    DateTime ageCutoff = DateTime.UtcNow.AddHours(-_Settings.AutonomousRecovery.RecoverySweepMaxFailedMissionAgeHours);
                    DateTime missionTimestamp = candidate.CompletedUtc ?? candidate.LastUpdateUtc;
                    if (missionTimestamp < ageCutoff)
                        continue;
                }

                if (await ApplyFailurePolicyAsync(candidate.TenantId, candidate.Id, token).ConfigureAwait(false))
                    processed++;
            }

            if (reconciledSkipped > 0)
            {
                _Logging.Debug(_Header + "failed-mission sweep skipped " + reconciledSkipped
                    + " mission(s) closed by terminal-voyage reconciliation");
            }
        }

        /// <summary>
        /// Number of rescues currently deferred by an engaged dispatch hold and waiting for it to clear.
        /// </summary>
        public int HoldDeferredRescueCount => _HoldDeferredRescues.Count;

        /// <summary>
        /// Number of per-mission policy gates currently held or awaited.
        /// </summary>
        internal int MissionLockCount
        {
            get { lock (_MissionLocksSync) { return _MissionLocks.Count; } }
        }

        /// <summary>
        /// Number of missions whose withheld stall nudge has already been recorded.
        /// </summary>
        internal int NudgeSuppressedMissionCount => _NudgeSuppressedMissions.Count;

        /// <summary>
        /// Branches the last landing-drain sweep could not measure because no dock worktree held them.
        /// </summary>
        public int LastLandingDrainNoDockCount { get; private set; } = 0;

        /// <summary>
        /// Branches the last landing-drain sweep could not measure because git failed reading the diff.
        /// </summary>
        public int LastLandingDrainDiffFailedCount { get; private set; } = 0;

        private async Task ReevaluateHoldDeferredRescuesAsync(CancellationToken token)
        {
            if (_HoldDeferredRescues.IsEmpty) return;
            if (_DispatchHold != null && _DispatchHold.Snapshot() != null) return;

            foreach (KeyValuePair<string, HoldDeferredRescue> deferred in _HoldDeferredRescues.ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (!_HoldDeferredRescues.TryRemove(deferred.Key, out HoldDeferredRescue? _)) continue;

                _Logging.Info(_Header + "dispatch hold cleared; re-evaluating the rescue deferred for mission " + deferred.Key);
                await ApplyFailurePolicyAsync(deferred.Value.TenantId, deferred.Key, token).ConfigureAwait(false);
            }
        }

        private async Task<bool> DeferRescueForDispatchHoldAsync(
            AuthContext auth,
            Mission mission,
            RecoveryDecision decision,
            DispatchHoldSnapshot hold,
            CancellationToken token)
        {
            if (_HoldDeferredRescues.TryGetValue(mission.Id, out HoldDeferredRescue? recorded)
                && recorded.HoldSetByUtc == hold.SetByUtc)
            {
                // This engagement's refusal is already on the incident; the entry is re-evaluated
                // when the hold clears.
                return false;
            }

            Incident incident = await EnsureIncidentAsync(auth, mission, decision, token).ConfigureAwait(false);
            await LinkIncidentToOwningObjectivesAsync(auth, mission, incident, token).ConfigureAwait(false);

            string holder = String.IsNullOrWhiteSpace(hold.SetBy) ? "unknown" : hold.SetBy!;
            string holdDetail = "dispatch_hold engaged by " + holder + " at " + hold.SetByUtc.ToString("u") + ": " + hold.Reason;
            await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
            {
                RecoveryNotes = AppendNote(incident.RecoveryNotes,
                    "Autonomous rescue deferred: " + holdDetail + ". No rescue was dispatched and no recovery attempt was spent; "
                    + "the rescue is re-evaluated on the first recovery sweep after the hold clears.")
            }, token).ConfigureAwait(false);

            _HoldDeferredRescues[mission.Id] = new HoldDeferredRescue(mission.TenantId, hold.SetByUtc);
            _Logging.Info(_Header + "rescue for mission " + mission.Id + " deferred: " + holdDetail);
            await EmitEventAsync("autonomous_recovery.rescue_deferred_dispatch_hold",
                "Autonomous rescue for failed mission " + mission.Id + " deferred: " + holdDetail + ".",
                mission, incident.Id, token).ConfigureAwait(false);
            return false;
        }

        private sealed class HoldDeferredRescue
        {
            public HoldDeferredRescue(string? tenantId, DateTime holdSetByUtc)
            {
                TenantId = tenantId;
                HoldSetByUtc = holdSetByUtc;
            }

            public string? TenantId { get; }

            public DateTime HoldSetByUtc { get; }
        }

        // Recovery is closed for a voyage that ended Complete or Cancelled. A Failed voyage stays
        // eligible: a mission failure is what ends a voyage Failed, and that failure is exactly what
        // recovery exists to rescue. Later policy conditions still decide whether a rescue launches.
        private async Task<bool> IsRecoveryClosedVoyageAsync(string? voyageId, Dictionary<string, bool> cache, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(voyageId))
                return false;

            bool terminal;
            if (cache.TryGetValue(voyageId, out terminal))
                return terminal;

            Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            terminal = voyage != null
                && (voyage.Status == VoyageStatusEnum.Cancelled
                    || voyage.Status == VoyageStatusEnum.Complete);
            cache[voyageId] = terminal;
            return terminal;
        }

        private async Task ProcessLandingDrainOpenVoyagesAsync(CancellationToken token)
        {
            if (!_Settings.AutonomousRecovery.Enabled || !_Settings.AutonomousRecovery.LandingDrainEnabled) return;
            if (_MergeQueue == null || _AutoLandEvaluator == null || _ConventionChecker == null || _CriticalTriggerEvaluator == null)
                return;

            List<Voyage> candidates = new List<Voyage>();
            candidates.AddRange(await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Open, token).ConfigureAwait(false));
            candidates.AddRange(await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false));

            int processed = 0;
            int maxVoyages = _Settings.AutonomousRecovery.LandingDrainMaxVoyagesPerSweep;
            Interlocked.Exchange(ref _LandingDrainNoDockCount, 0);
            Interlocked.Exchange(ref _LandingDrainDiffFailedCount, 0);

            foreach (Voyage voyage in candidates.OrderBy(item => item.LastUpdateUtc))
            {
                if (processed >= maxVoyages) break;
                token.ThrowIfCancellationRequested();

                List<MissionSummary> summaries = await _Database.Missions
                    .EnumerateMissionSummariesByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
                if (summaries.Count == 0) continue;

                if (summaries.Any(item => IsMissionActivelyRunning(item)))
                    continue;

                processed++;

                // Isolate each voyage: a single failing voyage (e.g. a vessel with an
                // unreachable working directory or a transient DB error) must not abort
                // the entire drain pass and starve every voyage that sorts after it.
                // Cancellation still propagates so the sweep can be torn down cleanly.
                try
                {
                    await DrainIdleVoyageAsync(voyage, summaries, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "landing-drain failed for voyage " + voyage.Id + ": " + ex.Message);
                }
            }

            // A recently Failed voyage is not drained (nothing is enqueued or rescued for it), but the
            // completion rule still applies: it moves to Complete once its failed work has landed.
            DateTime nowUtc = DateTime.UtcNow;
            List<Voyage> failed = await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Failed, token).ConfigureAwait(false);
            foreach (Voyage voyage in failed.Where(item => VoyageCompletionRule.IsSweepCandidate(item, nowUtc)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await TryCompleteIdleVoyageAsync(voyage, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "landing-drain completion failed for voyage " + voyage.Id + ": " + ex.Message);
                }
            }

            LastLandingDrainNoDockCount = Interlocked.CompareExchange(ref _LandingDrainNoDockCount, 0, 0);
            LastLandingDrainDiffFailedCount = Interlocked.CompareExchange(ref _LandingDrainDiffFailedCount, 0, 0);
            if (processed > 0 || LastLandingDrainNoDockCount > 0 || LastLandingDrainDiffFailedCount > 0)
            {
                _Logging.Info(_Header + "landing-drain sweep complete: voyages drained " + processed
                    + ", branches unmeasured (no dock) " + LastLandingDrainNoDockCount
                    + ", branches unmeasured (diff failed) " + LastLandingDrainDiffFailedCount);
            }
        }

        private async Task DrainIdleVoyageAsync(Voyage voyage, List<MissionSummary> summaries, CancellationToken token)
        {
            await EnqueueJudgePassedWorkProducedAsync(voyage, summaries, token).ConfigureAwait(false);
            await RescueFailedLeafChainsAsync(summaries, token).ConfigureAwait(false);

            List<MissionSummary> refreshed = await _Database.Missions
                .EnumerateMissionSummariesByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            voyage = await TryCompleteIdleVoyageAsync(voyage, token).ConfigureAwait(false);
            await DetectStuckOpenVoyageAsync(voyage, refreshed, token).ConfigureAwait(false);
        }

        private async Task EnqueueJudgePassedWorkProducedAsync(Voyage voyage, List<MissionSummary> summaries, CancellationToken token)
        {
            foreach (MissionSummary summary in summaries
                .Where(item => item.Status == MissionStatusEnum.WorkProduced && !String.IsNullOrWhiteSpace(item.BranchName))
                .OrderBy(item => item.LastUpdateUtc))
            {
                if (IsReviewerPersona(summary.Persona)) continue;


                if (!await IsReviewerChainPassedAsync(summaries, summary.Id, token).ConfigureAwait(false)) continue;

                Mission? mission = await ReadMissionAsync(summary.TenantId, summary.Id, token).ConfigureAwait(false);
                if (mission == null || String.IsNullOrWhiteSpace(mission.VesselId)) continue;

                Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
                if (vessel == null) continue;

                // Isolate each candidate mission: a single failing enqueue (e.g. a transient
                // merge-queue / DB error) must not abort the rest of this voyage's drain or
                // skip the downstream completion / stuck-detection steps. Cancellation still
                // propagates so the sweep can be torn down cleanly.
                try
                {
                    SafetyNetDiffLoad diffLoad = await TryLoadSafetyNetDiffAsync(mission, vessel, token).ConfigureAwait(false);
                    string? diff = diffLoad.Diff;
                    SafetyNetEnqueueResult result = await _MergeQueue!.TrySafetyNetEnqueueAsync(
                        mission,
                        vessel,
                        diff,
                        _AutoLandEvaluator!,
                        _ConventionChecker!,
                        _CriticalTriggerEvaluator!,
                        token).ConfigureAwait(false);

                    if (result.Outcome == SafetyNetEnqueueOutcomeEnum.AlreadyEnqueued)
                    {
                        _Logging.Debug(_Header + "landing-drain skipped mission " + mission.Id + ": already enqueued");
                        continue;
                    }

                    if (result.Outcome == SafetyNetEnqueueOutcomeEnum.SkippedNoBranch)
                    {
                        _Logging.Debug(_Header + "landing-drain skipped mission " + mission.Id + ": no branch");
                        continue;
                    }

                    string eventType = result.Outcome == SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview
                        ? "landing_drain.flagged_for_review"
                        : "landing_drain.enqueued";
                    string message = "Landing-drain safety net enqueued mission " + mission.Id +
                        " on voyage " + voyage.Id + (result.Detail != null ? ": " + result.Detail : String.Empty);
                    await EmitLandingDrainEventAsync(eventType, message, mission, voyage.Id, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "landing-drain enqueue failed for mission " + mission.Id + ": " + ex.Message);
                }
            }
        }

        private async Task RescueFailedLeafChainsAsync(List<MissionSummary> summaries, CancellationToken token)
        {
            foreach (MissionSummary summary in summaries
                .Where(item => item.Status == MissionStatusEnum.Failed && IsReviewerPersona(item.Persona)))
            {
                bool hasActiveDependents = summaries.Any(item =>
                    String.Equals(item.DependsOnMissionId, summary.Id, StringComparison.Ordinal) &&
                    item.Status != MissionStatusEnum.Failed &&
                    item.Status != MissionStatusEnum.Cancelled &&
                    item.Status != MissionStatusEnum.Complete &&
                    item.Status != MissionStatusEnum.LandingFailed);
                if (hasActiveDependents) continue;

                Mission? failed = await ReadMissionAsync(summary.TenantId, summary.Id, token).ConfigureAwait(false);
                if (failed == null) continue;

                string reason = failed.FailureReason ?? String.Empty;
                if (!reason.Contains("NEEDS_REVISION", StringComparison.OrdinalIgnoreCase) &&
                    !reason.Contains("Judge verdict: FAIL", StringComparison.OrdinalIgnoreCase) &&
                    !reason.Contains("did not emit an explicit PASS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await ApplyFailurePolicyAsync(failed.TenantId, failed.Id, token).ConfigureAwait(false);
            }
        }

        // Returns the voyage as the completion rule left it, so the stuck-voyage check that follows
        // sees a voyage the rule just finished (or one another writer ended) as terminal.
        private async Task<Voyage> TryCompleteIdleVoyageAsync(Voyage voyage, CancellationToken token)
        {
            // The rule raises OnVoyageComplete itself, after the write and before the drain's own
            // incident and event bookkeeping below.
            VoyageCompletionResult result = await VoyageCompletionRule.ApplyAsync(_Database, voyage.Id, _Admiral.OnVoyageComplete, token).ConfigureAwait(false);
            if (result.HookException != null)
                _Logging.Warn(_Header + "OnVoyageComplete failed for voyage " + voyage.Id + ": " + result.HookException.Message);
            if (result.EventException != null)
                _Logging.Warn(_Header + "could not record voyage.completed for voyage " + voyage.Id + ": " + result.EventException.Message);
            if (!result.Written) return result.Voyage ?? voyage;
            voyage = result.Voyage!;

            if (voyage.Status == VoyageStatusEnum.Complete)
            {
                await CloseOpenStuckVoyageIncidentsAsync(voyage, token).ConfigureAwait(false);
            }

            await EmitLandingDrainEventAsync(
                "landing_drain.voyage_completed",
                "Landing-drain marked voyage " + voyage.Id + " " + voyage.Status,
                null,
                voyage.Id,
                token,
                voyage.TenantId,
                voyage.UserId).ConfigureAwait(false);

            return voyage;
        }

        private async Task DetectStuckOpenVoyageAsync(Voyage voyage, List<MissionSummary> summaries, CancellationToken token)
        {
            if (voyage.Status != VoyageStatusEnum.Open && voyage.Status != VoyageStatusEnum.InProgress) return;
            if (summaries.Any(item => IsMissionActivelyRunning(item))) return;

            // Do not open a stuck-voyage incident while any mission still has a forward path.
            // WorkProduced missions are awaiting the next stage, Pending missions are queued for
            // assignment, and Assigned missions with a live captain process are actively running.
            if (summaries.Any(item => item.Status == MissionStatusEnum.WorkProduced)) return;
            if (summaries.Any(item => IsMissionWaitingForAssignment(item))) return;
            if (summaries.Any(item => item.Status == MissionStatusEnum.Assigned && IsMissionProcessAlive(item.ProcessId))) return;

            // Generic quiet-voyage path. Anchor elapsed time on a churn-stable timestamp (the most
            // recent mission start/completion) rather than the dependent/voyage LastUpdateUtc, which
            // WaitingForDependency retry churn bumps every health cycle and would otherwise reset the
            // quiet clock indefinitely.
            DateTime lastProgress = ComputeChurnStableProgressUtc(voyage, summaries);

            double quietMinutes = (DateTime.UtcNow - lastProgress).TotalMinutes;
            if (quietMinutes < _Settings.AutonomousRecovery.StuckOpenVoyageMinutes) return;

            if (await HasOpenStuckVoyageIncidentAsync(voyage, token).ConfigureAwait(false)) return;

            await OpenStuckVoyageIncidentAsync(
                voyage,
                "Voyage " + voyage.Id + " has been " + voyage.Status + " with no live missions and no progress for " +
                    quietMinutes.ToString("F1") + " minutes.",
                token).ConfigureAwait(false);
        }

        // Compute a churn-stable quiet-clock anchor: the most recent real forward-progress timestamp
        // (mission start/completion), which WaitingForDependency assignment retries never bump --
        // unlike LastUpdateUtc, which churns every health cycle. Falls back to voyage creation when
        // no mission has started so an always-stalled voyage can still age past the threshold.
        private static DateTime ComputeChurnStableProgressUtc(Voyage voyage, List<MissionSummary> summaries)
        {
            DateTime anchor = DateTime.MinValue;
            foreach (MissionSummary summary in summaries)
            {
                if (summary.CompletedUtc.HasValue && summary.CompletedUtc.Value > anchor)
                    anchor = summary.CompletedUtc.Value;
                if (summary.StartedUtc.HasValue && summary.StartedUtc.Value > anchor)
                    anchor = summary.StartedUtc.Value;
            }

            if (anchor == DateTime.MinValue) anchor = voyage.CreatedUtc;
            return anchor;
        }

        // Open a High-severity stuck-open-voyage incident and emit the landing-drain event. The
        // summary always contains "no live missions" so HasOpenStuckVoyageIncidentAsync de-dups
        // across both the structural and generic detection paths.
        private async Task OpenStuckVoyageIncidentAsync(Voyage voyage, string summary, CancellationToken token)
        {
            AuthContext auth = BuildVoyageAuth(voyage);
            Incident incident = await _Incidents.CreateAsync(auth, new IncidentUpsertRequest
            {
                Title = "Stuck open voyage: " + Truncate(voyage.Title, 96),
                Summary = summary,
                Status = IncidentStatusEnum.Open,
                Severity = IncidentSeverityEnum.High,
                VoyageId = voyage.Id,
                Impact = "Produced work may not be landing and downstream stages may be stalled.",
                RecoveryNotes = "Inspect judge-passed WorkProduced missions, pending handoffs, and merge queue entries.",
                DetectedUtc = DateTime.UtcNow
            }, token).ConfigureAwait(false);

            await EmitLandingDrainEventAsync(
                "landing_drain.stuck_open_voyage",
                "Landing-drain opened incident " + incident.Id + " for stuck voyage " + voyage.Id,
                null,
                voyage.Id,
                token,
                voyage.TenantId,
                voyage.UserId,
                incident.Id).ConfigureAwait(false);
        }

        private async Task<bool> HasOpenStuckVoyageIncidentAsync(Voyage voyage, CancellationToken token)
        {
            AuthContext auth = BuildVoyageAuth(voyage);
            List<Incident> active = await _Incidents.EnumerateActiveAsync(auth, new IncidentQuery
            {
                VoyageId = voyage.Id
            }, token).ConfigureAwait(false);

            return active.Any(item =>
                (item.Summary ?? String.Empty).Contains("no live missions", StringComparison.OrdinalIgnoreCase));
        }

        // Close any open stuck-open-voyage incidents when the voyage later reaches Complete.
        // This auto-mitigates false-positive incidents that opened before the health fixes landed.
        private async Task CloseOpenStuckVoyageIncidentsAsync(Voyage voyage, CancellationToken token)
        {
            AuthContext auth = BuildVoyageAuth(voyage);
            List<Incident> active = await _Incidents.EnumerateActiveAsync(auth, new IncidentQuery
            {
                VoyageId = voyage.Id
            }, token).ConfigureAwait(false);

            foreach (Incident incident in active.Where(item =>
                (item.Summary ?? String.Empty).Contains("no live missions", StringComparison.OrdinalIgnoreCase)))
            {
                await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Closed,
                    ClosedUtc = DateTime.UtcNow
                }, token).ConfigureAwait(false);

                await EmitLandingDrainEventAsync(
                    "landing_drain.stuck_voyage_incident_closed",
                    "Landing-drain closed stuck-voyage incident " + incident.Id + " because voyage " + voyage.Id + " completed.",
                    null,
                    voyage.Id,
                    token,
                    voyage.TenantId,
                    voyage.UserId,
                    incident.Id).ConfigureAwait(false);
            }
        }

        private async Task<bool> IsReviewerChainPassedAsync(List<MissionSummary> summaries, string rootMissionId, CancellationToken token)
        {
            // An empty queue (no dependents) returns true -- "no reviewers yet" is treated as
            // passed under the upfront-stage model where all reviewer missions are materialized
            // before any Worker reaches WorkProduced. A lazily-materialized pipeline that creates
            // reviewer stages only after the Worker finishes MUST set a sentinel dependency before
            // the Worker runs, or this method will incorrectly allow the Worker to auto-land before
            // any review occurs.
            List<MissionSummary> queue = summaries
                .Where(item => String.Equals(item.DependsOnMissionId, rootMissionId, StringComparison.Ordinal))
                .ToList();

            while (queue.Count > 0)
            {
                MissionSummary current = queue[0];
                queue.RemoveAt(0);

                if (current.Status == MissionStatusEnum.Pending)
                    return false;

                if (current.Status == MissionStatusEnum.Failed ||
                    current.Status == MissionStatusEnum.Cancelled ||
                    current.Status == MissionStatusEnum.LandingFailed)
                {
                    return false;
                }

                if (IsReviewerPersona(current.Persona))
                {
                    if (current.Status != MissionStatusEnum.Complete && current.Status != MissionStatusEnum.WorkProduced)
                        return false;

                    Mission? reviewer = await ReadMissionAsync(current.TenantId, current.Id, token).ConfigureAwait(false);
                    if (reviewer == null || !IsJudgePassMission(reviewer))
                        return false;

                    // A Judge PASS held for operator review has not passed yet: only an operator
                    // clear lets the drain land the work it reviewed.
                    if (reviewer.HeldForOperatorReview)
                    {
                        _Logging.Info(_Header + "landing-drain skipped mission " + rootMissionId + ": reviewer " + reviewer.Id
                            + " is held for operator review (" + (reviewer.HeldForOperatorReviewReason ?? "no reason recorded") + ")");
                        return false;
                    }
                }

                queue.AddRange(summaries.Where(item => String.Equals(item.DependsOnMissionId, current.Id, StringComparison.Ordinal)));
            }

            return true;
        }

        private async Task<SafetyNetDiffLoad> TryLoadSafetyNetDiffAsync(Mission mission, Vessel vessel, CancellationToken token)
        {
            if (_Git == null)
                return new SafetyNetDiffLoad { Outcome = SafetyNetDiffOutcomeEnum.GitUnavailable, Detail = "no git service configured" };

            // Only the captain's dock worktree is checked out on the mission branch. The vessel
            // WorkingDirectory/LocalPath is the default-branch checkout and always diffs empty, so it is
            // never used as a stand-in: an unmeasured branch is reported as such and flagged for review.
            string? repoPath = null;
            if (!String.IsNullOrWhiteSpace(mission.DockId))
            {
                Dock? dock = await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                if (dock != null && !String.IsNullOrWhiteSpace(dock.WorktreePath))
                    repoPath = dock.WorktreePath;
            }

            if (String.IsNullOrWhiteSpace(repoPath))
            {
                Interlocked.Increment(ref _LandingDrainNoDockCount);
                _Logging.Warn(_Header + "landing-drain cannot measure mission " + mission.Id
                    + ": no dock worktree holds branch " + (mission.BranchName ?? "(none)") + "; the branch is flagged for review unmeasured");
                return new SafetyNetDiffLoad { Outcome = SafetyNetDiffOutcomeEnum.NoDock, Detail = "no dock worktree for mission " + mission.Id };
            }

            try
            {
                string diff = await _Git.DiffAsync(repoPath, vessel.DefaultBranch ?? "main", token).ConfigureAwait(false);
                return new SafetyNetDiffLoad { Outcome = SafetyNetDiffOutcomeEnum.Loaded, Diff = diff };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _LandingDrainDiffFailedCount);
                _Logging.Warn(_Header + "landing-drain diff failed for mission " + mission.Id + " in " + repoPath + ": " + ex.Message);
                return new SafetyNetDiffLoad { Outcome = SafetyNetDiffOutcomeEnum.DiffFailed, Detail = ex.Message };
            }
        }

        private async Task EmitLandingDrainEventAsync(
            string eventType,
            string message,
            Mission? mission,
            string voyageId,
            CancellationToken token,
            string? tenantId = null,
            string? userId = null,
            string? incidentId = null)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                TenantId = tenantId ?? mission?.TenantId,
                UserId = userId ?? mission?.UserId,
                EntityType = incidentId != null ? "incident" : (mission != null ? "mission" : "voyage"),
                EntityId = incidentId ?? mission?.Id ?? voyageId,
                MissionId = mission?.Id,
                VesselId = mission?.VesselId,
                VoyageId = voyageId
            };

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        // A mission is waiting for assignment when it is Pending and its assignment pipeline state
        // is an expected wait state. These missions have a forward path and must not cause a
        // stuck-voyage incident.
        private static bool IsMissionWaitingForAssignment(MissionSummary summary)
        {
            if (summary.Status != MissionStatusEnum.Pending) return false;

            return summary.AssignmentState == MissionAssignmentStateEnum.Pending ||
                summary.AssignmentState == MissionAssignmentStateEnum.WaitingForDependency ||
                summary.AssignmentState == MissionAssignmentStateEnum.WaitingForVesselMutex ||
                summary.AssignmentState == MissionAssignmentStateEnum.WaitingForIdleCaptain ||
                summary.AssignmentState == MissionAssignmentStateEnum.WaitingForResourcePressure ||
                summary.AssignmentState == MissionAssignmentStateEnum.Provisioning;
        }

        // True when the mission's tracked OS process is still running. A null or dead process id
        // means the captain is no longer live.
        private static bool IsMissionProcessAlive(int? processId)
        {
            if (!processId.HasValue) return false;
            return ProcessSupervisor.IsTrackedProcessAlive(processId.Value);
        }

        // True when a mission is actively executing: InProgress/Testing/Review,
        // or Assigned with a live captain process. Assigned with a dead process is NOT actively
        // running and may be a stuck-voyage candidate.
        private static bool IsMissionActivelyRunning(MissionSummary summary)
        {
            return summary.Status == MissionStatusEnum.InProgress ||
                summary.Status == MissionStatusEnum.Testing ||
                summary.Status == MissionStatusEnum.Review ||
                (summary.Status == MissionStatusEnum.Assigned && IsMissionProcessAlive(summary.ProcessId));
        }

        private static bool IsJudgePassMission(Mission mission)
        {
            if (!String.Equals(mission.Persona, "Judge", StringComparison.OrdinalIgnoreCase)) return false;
            if (mission.Status == MissionStatusEnum.Failed) return false;

            string output = mission.AgentOutput ?? String.Empty;
            if (String.IsNullOrWhiteSpace(output)) return false;

            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                // Honor both the canonical standalone line and the progress-signal form the
                // runtime echoes for an in-flight verdict, so a salvaged verdict still counts
                // as a PASS when the safety-net sweep evaluates the Judge reviewer.
                if (_JudgePassLinePattern.IsMatch(line))
                    return true;
            }

            return false;
        }

        private static AuthContext BuildVoyageAuth(Voyage voyage)
        {
            return AuthContext.Authenticated(
                voyage.TenantId ?? Constants.DefaultTenantId,
                voyage.UserId ?? Constants.DefaultUserId,
                false,
                true,
                "AutonomousRecovery",
                principalDisplay: "Armada Autonomous Recovery");
        }

        private async Task<List<MissionSummary>> EnumerateAllSummariesByStatusAsync(MissionStatusEnum? status, CancellationToken token)
        {
            List<MissionSummary> all = new List<MissionSummary>();
            int pageNumber = 1;
            const int pageSize = 1000;
            while (true)
            {
                EnumerationResult<MissionSummary> page = await _Database.Missions.EnumerateMissionSummariesAsync(
                    new EnumerationQuery
                    {
                        Status = status?.ToString(),
                        PageNumber = pageNumber,
                        PageSize = pageSize,
                        Order = EnumerationOrderEnum.CreatedDescending
                    }, token).ConfigureAwait(false);

                all.AddRange(page.Objects);
                if (page.Objects.Count < pageSize) break;
                pageNumber++;
            }

            return all;
        }

        // Lightweight rescue lookup for the hot read-only path (IsAlreadyHandledAsync runs for
        // every recovery candidate every ~5s). ParentMissionId is set exclusively by rescue
        // dispatch (see DispatchRescueMissionAsync), so a ParentMissionId match uniquely
        // identifies auto-rescue missions of this failure. Uses summaries (no description /
        // agent_output / diff_snapshot hydration) to avoid loading every vessel mission's heavy
        // text columns on each tick.
        private async Task<List<MissionSummary>> EnumerateRescueMissionSummariesAsync(Mission failedMission, CancellationToken token)
        {
            List<MissionSummary> vesselMissions = !String.IsNullOrWhiteSpace(failedMission.VesselId)
                ? await _Database.Missions.EnumerateMissionSummariesByVesselAsync(failedMission.VesselId, token).ConfigureAwait(false)
                : await EnumerateAllSummariesByStatusAsync(null, token).ConfigureAwait(false);

            return vesselMissions
                .Where(item => String.Equals(item.ParentMissionId, failedMission.Id, StringComparison.Ordinal))
                .ToList();
        }

        private async Task<bool> ApplyFailurePolicyAsync(string? tenantId, string missionId, CancellationToken token)
        {
            MissionLockEntry missionLock = EnterMissionLock(missionId);
            try
            {
                await missionLock.Gate.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                LeaveMissionLock(missionId, missionLock);
                throw;
            }

            try
            {
                Mission? latest = await ReadMissionAsync(tenantId, missionId, token).ConfigureAwait(false);
                if (latest == null || !IsRecoverableTerminalStatus(latest.Status))
                    return false;

                // Checked before any write: an incident, a deferral, or a recovery timestamp would each
                // treat the reconciled record as a fresh failure and keep it inside the sweep's lookback.
                if (TerminalVoyageMissionRule.IsReconciledOutcome(latest))
                {
                    _HoldDeferredRescues.TryRemove(latest.Id, out HoldDeferredRescue? _);
                    _Logging.Debug(_Header + "recovery skipped mission " + latest.Id
                        + ": closed by terminal-voyage reconciliation, not a failure of its own");
                    return false;
                }

                if (await SuppressCancelledVoyageRecoveryAsync(latest, token).ConfigureAwait(false))
                    return true;

                if (await IsAlreadyHandledAsync(latest, token).ConfigureAwait(false))
                    return false;

                // Deterministic repeated-identical-test-failure comparison, read before classifying so
                // the decision can name a rescue that repeated its parent's exact test failure. The
                // comparison only reports a repeat for a rescue whose complete, non-overflowed test set
                // matches its parent's; every other case leaves the classification untouched.
                RepeatedFailureCheck repeated = await ReadRepeatedIdenticalTestFailureAsync(latest, token).ConfigureAwait(false);
                RecoveryDecision decision = Classify(latest, repeated.IsRepeated);
                decision = await RefineFailureCauseAsync(latest, decision, repeated, token).ConfigureAwait(false);
                AuthContext auth = BuildAuth(latest);

                // A block-policy rescue that produced no commits (rescue_produced_no_commits) was
                // deliberately failed by the no-op detection guard in MissionLandingHandler. No
                // further recovery is possible and no incident is meaningful for operator review:
                // the work already landed via another path. Close any pre-existing open incident
                // and skip the normal incident/runbook path so the sweep never re-opens one.
                if (!decision.DispatchRescue
                    && IsAutoRescueMission(latest)
                    && String.Equals(latest.FailureReason, "rescue_produced_no_commits", StringComparison.OrdinalIgnoreCase))
                {
                    await CloseActiveMissionIncidentsAsync(auth, latest,
                        "Autonomous recovery suppressed: rescue produced no commits; no further recovery possible.",
                        token).ConfigureAwait(false);
                    await MarkPolicyBlockedAsync(latest, token).ConfigureAwait(false);
                    await EmitEventAsync("autonomous_recovery.blocked",
                        "Autonomous recovery blocked for no-op rescue mission " + latest.Id + ": rescue_produced_no_commits",
                        latest, null, token).ConfigureAwait(false);
                    return true;
                }

                // A rescue is a dispatch, so it obeys the fleet-wide dispatch hold through the same
                // admission rule every other dispatch path calls. It is checked before any incident,
                // runbook, or voyage work so a held tick writes nothing but the named deferral.
                if (decision.DispatchRescue && !latest.IsReadOnlyMode && _DispatchHold != null)
                {
                    try
                    {
                        _DispatchHold.ThrowIfActive();
                    }
                    catch (DispatchHoldActiveException held)
                    {
                        return await DeferRescueForDispatchHoldAsync(auth, latest, decision, held.Hold, token).ConfigureAwait(false);
                    }
                }

                Incident incident = await EnsureIncidentAsync(auth, latest, decision, token).ConfigureAwait(false);
                await LinkIncidentToOwningObjectivesAsync(auth, latest, incident, token).ConfigureAwait(false);

                // When the rescue repeated its parent's exact test failure, name the repeated tests on
                // the incident so an operator sees the failure did not change, rather than the generic
                // auto-rescue block. Only complete, identical sets reach here.
                if (repeated.IsRepeated)
                {
                    incident = await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
                    {
                        RecoveryNotes = AppendNote(incident.RecoveryNotes,
                            "Repeated identical test failure: the rescue failed its definition-of-done gate on the same "
                            + repeated.RepeatedTests.Count + " test(s) as its parent, so the failure did not change. Repeated tests: "
                            + FormatRepeatedTests(repeated.RepeatedTests) + ". No further rescue was dispatched.")
                    }, token).ConfigureAwait(false);
                }

                RunbookExecution? execution = await ExecuteRecoveryRunbookAsync(auth, latest, incident, decision, token).ConfigureAwait(false);

                if (latest.IsReadOnlyMode)
                {
                    await EnsureReadOnlyJudgeFollowUpAsync(latest, token).ConfigureAwait(false);
                    await MarkPolicyBlockedAsync(latest, token).ConfigureAwait(false);
                    await EmitEventAsync("autonomous_recovery.read_only_preserved",
                        "Autonomous recovery preserved read-only mode " + latest.Mode + " and audit-only scope for mission " + latest.Id + "; no rescue was dispatched.",
                        latest, incident.Id, token).ConfigureAwait(false);
                    return true;
                }

                if (!decision.DispatchRescue)
                {
                    await MarkPolicyBlockedAsync(latest, token).ConfigureAwait(false);
                    await EmitEventAsync("autonomous_recovery.blocked",
                        "Autonomous recovery opened incident " + incident.Id + " but did not dispatch a rescue for mission " + latest.Id + ": " + decision.Reason,
                        latest, incident.Id, token).ConfigureAwait(false);
                    return true;
                }

                string? rescueStartFromRef = await ResolveRescueStartFromRefAsync(latest, token).ConfigureAwait(false);
                if (IsReviewerPersona(latest.Persona) && String.IsNullOrWhiteSpace(rescueStartFromRef))
                {
                    const string missingTipReason = "start_from_ref_missing: Armada could not find a durable reviewed commit for the reviewer rescue.";
                    await MarkPolicyBlockedAsync(latest, token).ConfigureAwait(false);
                    await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
                    {
                        RecoveryNotes = AppendNote(incident.RecoveryNotes,
                            "Autonomous rescue blocked: " + missingTipReason)
                    }, token).ConfigureAwait(false);
                    await EmitEventAsync("autonomous_recovery.blocked",
                        "Autonomous recovery opened incident " + incident.Id + " but did not dispatch a rescue for mission " + latest.Id + ": " + missingTipReason,
                        latest, incident.Id, token).ConfigureAwait(false);
                    return true;
                }

                Mission rescue;
                try
                {
                    rescue = await DispatchRescueMissionAsync(latest, incident, rescueStartFromRef, token).ConfigureAwait(false);
                }
                catch (DispatchHoldActiveException held)
                {
                    // The hold was engaged between the admission check above and the dispatch.
                    return await DeferRescueForDispatchHoldAsync(auth, latest, decision, held.Hold, token).ConfigureAwait(false);
                }
                await ApplyClaudeThinkingDisableAsync(latest, rescue, token).ConfigureAwait(false);
                latest.RecoveryAttempts++;
                latest.LastRecoveryActionUtc = DateTime.UtcNow;
                latest.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(latest, token).ConfigureAwait(false);

                await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
                {
                    RecoveryNotes = AppendNote(incident.RecoveryNotes,
                        "Autonomous rescue mission dispatched: " + rescue.Id +
                        (execution != null ? " via runbook execution " + execution.Id + "." : "."))
                }, token).ConfigureAwait(false);

                await EmitEventAsync("autonomous_recovery.rescue_dispatched",
                    "Autonomous rescue mission " + rescue.Id + " dispatched for failed mission " + latest.Id,
                    latest, incident.Id, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to apply recovery policy for mission " + missionId + ": " + ex.Message);
                return false;
            }
            finally
            {
                missionLock.Gate.Release();
                LeaveMissionLock(missionId, missionLock);
            }
        }

        private MissionLockEntry EnterMissionLock(string missionId)
        {
            lock (_MissionLocksSync)
            {
                if (!_MissionLocks.TryGetValue(missionId, out MissionLockEntry? entry))
                {
                    entry = new MissionLockEntry();
                    _MissionLocks[missionId] = entry;
                }
                entry.Users++;
                return entry;
            }
        }

        private void LeaveMissionLock(string missionId, MissionLockEntry entry)
        {
            lock (_MissionLocksSync)
            {
                entry.Users--;
                if (entry.Users == 0)
                {
                    _MissionLocks.Remove(missionId);
                    entry.Gate.Dispose();
                }
            }
        }

        private sealed class MissionLockEntry
        {
            public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

            public int Users { get; set; }
        }

        private async Task<Mission?> ReadMissionAsync(string? tenantId, string missionId, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(tenantId))
            {
                Mission? tenantScoped = await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false);
                if (tenantScoped != null) return tenantScoped;
            }

            return await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
        }

        private RecoveryDecision Classify(Mission mission, bool repeatedIdenticalTestFailure = false)
        {
            string reason = mission.FailureReason ?? String.Empty;

            // A stage that ended with [ARMADA:RESULT] BLOCKED is waiting on an owner answer. A rescue would
            // re-run the same stage without it and ask the same question, so no mode or budget turns it
            // into a rescue.
            if (CaptainBlockedResult.IsBlockedFailure(reason))
                return RecoveryDecision.Blocked(BlockedQuestionRecoveryReason);
            if (!_Settings.AutonomousRecovery.DispatchRescueMissions)
                return RecoveryDecision.Blocked("autonomous rescue dispatch is disabled");
            if (String.IsNullOrWhiteSpace(mission.VesselId))
                return RecoveryDecision.Blocked("mission has no vessel");
            if (mission.RecoveryAttempts >= _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts)
                return RecoveryDecision.Blocked("mission recovery budget is exhausted");
            if (mission.Status == MissionStatusEnum.LandingFailed)
                return RecoveryDecision.Blocked("landing failures remain owned by landing and merge recovery workflows");
            if (mission.IsReadOnlyMode)
                return RecoveryDecision.Blocked("read-only mode " + mission.Mode + "; autonomous recovery preserves audit-only scope");
            // The D21 revision_kind decision marks a NEEDS_REVISION whose every item is comment, doc, or
            // boundary wording: a full rescue chain would spend a whole voyage to change wording that an
            // operator lands directly. The MissionService seam already opened an incident tagged for
            // operator landing; hold the rescue here.
            if (reason.Contains(MissionService.RevisionCommentOnlyRescueBlockMarker, StringComparison.Ordinal))
                return RecoveryDecision.Blocked("revision_comment_only: every NEEDS_REVISION item is comment, documentation, or boundary wording; this is an operator landing, not a rescue");
            if (IsAutoRescueMission(mission))
            {
                // The deterministic repeated-failure guard: a rescue that failed its gate on the very
                // same complete set of tests as its parent made no progress, so naming that in the
                // decision is clearer than the generic auto-rescue block. The hard-blocks above still
                // win because they return first; a rescue with a different, empty, overflowed or
                // unknown set keeps the generic block. This is the deterministic fallback the typed
                // foreign-test decision sits on top of.
                if (repeatedIdenticalTestFailure)
                    return RecoveryDecision.Blocked(RepeatedIdenticalTestFailureReason
                        + ": the rescue failed its gate on the same complete set of tests as its parent, so the failure did not change");
                return RecoveryDecision.Blocked("failed mission is already an autonomous rescue");
            }
            if (IsPolicyRefusalFailure(reason))
                return RecoveryDecision.Blocked("captain refusal already had its one continuation or has no approved alternate runtime; a rescue would repeat the blocked path: " + reason);
            // Infra and Timeout name the host, not the work: a rescue re-runs the same gate commands on
            // the same host and fails the same way. Compile and TestFail keep the rescue, and a reason
            // with no recorded class keeps the marker rules below.
            if (DefinitionOfDoneFailureClassifier.TryReadRecordedClass(reason, out DefinitionOfDoneFailureClassEnum gateClass)
                && (gateClass == DefinitionOfDoneFailureClassEnum.Infra || gateClass == DefinitionOfDoneFailureClassEnum.Timeout))
                return RecoveryDecision.Blocked("definition-of-done gate failure class " + gateClass
                    + " is a host fault that a rescue on the same host would repeat: " + reason);
            string markerText = FailureMarkerText(reason);
            if (IsEnvironmentalFailure(markerText))
                return RecoveryDecision.Blocked("environmental or provisioning fault, which no captain can repair: " + reason);
            if (HasSeriousFailureReason(markerText))
                return RecoveryDecision.Blocked("failure requires human review: " + reason);

            return RecoveryDecision.Rescue("recoverable mission failure");
        }

        /// <summary>
        /// The part of a failure reason the environmental and human-review marker rules read. A
        /// definition-of-done Compile or TestFail reason carries the command's own output after its
        /// first line: test names, assertion text and HTTP status lines that the work under test
        /// printed. That text describes the defect a rescue is meant to fix, not the platform, so a
        /// test named for authorization or quota, or an assertion expecting 403 Forbidden, must not
        /// read as an authorization or quota fault. Only the reason's first line is read for those
        /// classes; every other reason is read whole.
        /// </summary>
        /// <param name="reason">Recorded mission failure reason.</param>
        /// <returns>The text the marker rules read.</returns>
        public static string FailureMarkerText(string? reason)
        {
            if (String.IsNullOrEmpty(reason)) return String.Empty;
            if (!DefinitionOfDoneFailureClassifier.TryReadRecordedClass(reason, out DefinitionOfDoneFailureClassEnum gateClass))
                return reason;
            if (gateClass != DefinitionOfDoneFailureClassEnum.Compile && gateClass != DefinitionOfDoneFailureClassEnum.TestFail)
                return reason;

            string trimmed = reason.TrimStart();
            int newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return newline < 0 ? trimmed : trimmed.Substring(0, newline);
        }

        /// <summary>
        /// Refine the deterministic recovery decision with the D1 <c>failure_cause</c> adapter. Only a
        /// decision the rule would RESCUE is offered to the model: every rule hard-block returns first
        /// and is never reconsidered, so the model can only hold a rescue, never manufacture one. When
        /// the adapter is not wired, or the model does not gate, the decision is returned unchanged.
        /// The model reads the joined Checks and any parent failing tests so identical failure text
        /// with different causes is not misjudged. Never throws into the sweep.
        /// </summary>
        private async Task<RecoveryDecision> RefineFailureCauseAsync(Mission mission, RecoveryDecision decision, RepeatedFailureCheck repeated, CancellationToken token)
        {
            if (FailureCauseAdapter == null) return decision;
            if (!decision.DispatchRescue) return decision;

            try
            {
                FailureCauseDecisionInput input = await BuildFailureCauseInputAsync(mission, repeated, token).ConfigureAwait(false);
                TypedRecoveryVerdict refined = await FailureCauseAdapter
                    .DecideAsync(input, new TypedRecoveryVerdict(decision.DispatchRescue, decision.Reason), token)
                    .ConfigureAwait(false);

                // The adapter never converts a block into a rescue; still, only ever narrow a rescue to
                // a block here so a wiring or model error can never make recovery less conservative.
                if (decision.DispatchRescue && !refined.DispatchRescue)
                    return RecoveryDecision.Blocked(refined.Reason);

                return decision;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failure_cause refinement failed for mission " + mission.Id + ", rule stands: " + ex.Message);
                return decision;
            }
        }

        /// <summary>
        /// Build the D1 state: the failure reason, the captain output tail, the parsed
        /// definition-of-done class, persona, mode, recovery attempts, the voyage Checks joined as
        /// compact facts, and the parent's failing test names when this is a rescue.
        /// </summary>
        private async Task<FailureCauseDecisionInput> BuildFailureCauseInputAsync(Mission mission, RepeatedFailureCheck repeated, CancellationToken token)
        {
            string reason = mission.FailureReason ?? String.Empty;
            string? dodClass = DefinitionOfDoneFailureClassifier.TryReadRecordedClass(reason, out DefinitionOfDoneFailureClassEnum gateClass)
                ? gateClass.ToString()
                : null;

            List<FailureCauseCheckFact> checks = await ReadFailureCauseCheckFactsAsync(mission, token).ConfigureAwait(false);

            List<string> parentFailingTests = new List<string>();
            if (!String.IsNullOrWhiteSpace(mission.ParentMissionId))
            {
                if (repeated.IsRepeated)
                {
                    parentFailingTests.AddRange(repeated.RepeatedTests);
                }
                else
                {
                    StoredFailedTestSet? parentSet = await ReadLatestFailedTestSetAsync(mission.ParentMissionId!, token).ConfigureAwait(false);
                    if (parentSet != null) parentFailingTests.AddRange(parentSet.Value.Names);
                }
            }

            return new FailureCauseDecisionInput
            {
                Mission = mission,
                FailureReason = reason,
                AgentOutputTail = LastLines(mission.AgentOutput, 40),
                DodClass = dodClass,
                Persona = mission.Persona,
                MissionMode = mission.Mode.ToString(),
                RecoveryAttempts = mission.RecoveryAttempts,
                Checks = checks,
                ParentFailingTests = parentFailingTests
            };
        }

        /// <summary>
        /// Read the voyage Checks as compact facts for the D1 state. A read failure yields an empty
        /// list, so the model still answers from the reason and output; it never blocks recovery.
        /// </summary>
        private async Task<List<FailureCauseCheckFact>> ReadFailureCauseCheckFactsAsync(Mission mission, CancellationToken token)
        {
            List<FailureCauseCheckFact> facts = new List<FailureCauseCheckFact>();
            if (String.IsNullOrWhiteSpace(mission.VoyageId)) return facts;

            try
            {
                EnumerationResult<CheckRun> page = await _Database.CheckRuns
                    .EnumerateAsync(new CheckRunQuery { TenantId = mission.TenantId, VoyageId = mission.VoyageId, PageNumber = 1, PageSize = 50 }, token)
                    .ConfigureAwait(false);

                foreach (CheckRun run in page.Objects)
                {
                    facts.Add(new FailureCauseCheckFact
                    {
                        Label = String.IsNullOrWhiteSpace(run.Label) ? run.Type.ToString() : run.Label!,
                        Type = run.Type.ToString(),
                        Status = run.Status.ToString(),
                        CommitMatchesJudge = !String.IsNullOrWhiteSpace(run.CommitHash)
                            && !String.IsNullOrWhiteSpace(mission.CommitHash)
                            && String.Equals(run.CommitHash, mission.CommitHash, StringComparison.Ordinal),
                        ExitCode = run.ExitCode,
                        // Carry the evidence the rule read: a head and a tail for a failing check (an
                        // infra fault often surfaces at the start), a short tail for a passing one.
                        Diagnostics = TypedFailureCauseAdapter.OutputEvidence(
                            run.Output,
                            run.Status.ToString().Contains("Fail", StringComparison.OrdinalIgnoreCase)
                                || run.Status.ToString().Contains("Error", StringComparison.OrdinalIgnoreCase))
                    });
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failure_cause check join failed for mission " + mission.Id + ": " + ex.Message);
            }

            return facts;
        }

        private static string LastLines(string? text, int count)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int first = Math.Max(0, lines.Length - count);
            return String.Join("\n", lines[first..]);
        }

        /// <summary>
        /// Read whether a failed rescue repeated its parent's exact test failure. A repeat requires
        /// the mission to be a rescue (a parent to compare against), both the rescue's and the
        /// parent's latest definition-of-done evaluations to be complete, non-overflowed, non-empty
        /// test-failure sets, and the two sets to be identical. Any other case (not a rescue, no
        /// stored set, a non-test failure, an overflowed set, an empty set, or a differing set)
        /// returns not-repeated with no matched tests, so today's behaviour is unchanged.
        /// </summary>
        private async Task<RepeatedFailureCheck> ReadRepeatedIdenticalTestFailureAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.ParentMissionId))
                return RepeatedFailureCheck.None;

            StoredFailedTestSet? rescueSet = await ReadLatestFailedTestSetAsync(mission.Id, token).ConfigureAwait(false);
            if (rescueSet == null) return RepeatedFailureCheck.None;

            StoredFailedTestSet? parentSet = await ReadLatestFailedTestSetAsync(mission.ParentMissionId!, token).ConfigureAwait(false);
            if (parentSet == null) return RepeatedFailureCheck.None;

            if (!AreComparableIdenticalTestSets(parentSet.Value, rescueSet.Value))
                return RepeatedFailureCheck.None;

            return new RepeatedFailureCheck(true, rescueSet.Value.Names);
        }

        /// <summary>
        /// Whether two failing-test sets are both complete (not overflowed), non-empty and identical
        /// as unordered, de-duplicated sets. Pure and deterministic.
        /// </summary>
        internal static bool AreComparableIdenticalTestSets(StoredFailedTestSet parent, StoredFailedTestSet rescue)
        {
            if (parent.Overflow || rescue.Overflow) return false;
            if (parent.Names.Count == 0 || rescue.Names.Count == 0) return false;
            if (parent.Names.Count != rescue.Names.Count) return false;

            // A parallel test runner prints the failing tests in completion order, which changes from
            // run to run, so the same failures can arrive in a different order. Compare as sets.
            return new HashSet<string>(parent.Names, StringComparer.Ordinal).SetEquals(rescue.Names);
        }

        /// <summary>
        /// Read the latest definition-of-done evaluation for a mission and return its failing-test
        /// set when that evaluation was a test failure, or null when there is no evaluation, the
        /// latest evaluation was not a test failure, or the payload cannot be read.
        /// </summary>
        private async Task<StoredFailedTestSet?> ReadLatestFailedTestSetAsync(string missionId, CancellationToken token)
        {
            EnumerationResult<ArmadaEvent> page;
            try
            {
                page = await _Database.Events.EnumerateAsync(new EnumerationQuery
                {
                    MissionId = missionId,
                    EventType = DefinitionOfDoneEvaluationRecord.EventType,
                    Order = EnumerationOrderEnum.CreatedDescending,
                    PageNumber = 1,
                    PageSize = 1
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read definition-of-done evaluations for mission " + missionId + ": " + ex.Message);
                return null;
            }

            ArmadaEvent? latest = page.Objects.FirstOrDefault();
            if (latest == null || String.IsNullOrWhiteSpace(latest.Payload)) return null;
            if (latest.Payload!.Length > DefinitionOfDoneEvaluationRecord.MaxStoredPayloadLength) return null;

            DefinitionOfDoneEvaluationRecord? record;
            try
            {
                record = System.Text.Json.JsonSerializer.Deserialize<DefinitionOfDoneEvaluationRecord>(latest.Payload);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }

            if (record == null) return null;
            if (record.Outcome != DefinitionOfDoneEvaluationOutcomeEnum.Failed) return null;
            if (record.FailureClass != DefinitionOfDoneFailureClassEnum.TestFail) return null;

            List<string> names = record.FailedTestNames != null
                ? new List<string>(record.FailedTestNames)
                : new List<string>();
            return new StoredFailedTestSet(names, record.FailedTestNamesOverflow);
        }

        private async Task<bool> SuppressCancelledVoyageRecoveryAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VoyageId))
                return false;

            Voyage? voyage = await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
            if (voyage?.Status != VoyageStatusEnum.Cancelled)
                return false;

            // The first suppression pass marks recovery-handled fields, cancels active rescues, and
            // closes incidents. On subsequent passes the mission is still suppressed (rescue dispatch
            // stays blocked) but the cancellation / incident-closing / logging work must not repeat,
            // otherwise the sweep re-runs it on every tick. Skip the work once the mission is marked.
            if (IsRecoveryHandled(mission))
                return true;

            string note = "Autonomous recovery suppressed because parent voyage " + voyage.Id + " is Cancelled.";
            AuthContext auth = BuildAuth(mission);
            List<Mission> cancelledRescues = await CancelActiveRescueMissionsAsync(mission, note, token).ConfigureAwait(false);
            await CloseActiveMissionIncidentsAsync(auth, mission, AppendRescueCancellationNote(note, cancelledRescues), token).ConfigureAwait(false);
            await MarkPolicyBlockedAsync(mission, token).ConfigureAwait(false);

            _Logging.Debug(_Header + note +
                (cancelledRescues.Count > 0 ? " Cancelled rescue mission(s): " + String.Join(", ", cancelledRescues.Select(item => item.Id)) + "." : String.Empty));
            return true;
        }

        private bool IsRecoveryHandled(Mission mission)
        {
            return mission.LastRecoveryActionUtc.HasValue
                && mission.RecoveryAttempts >= _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts;
        }

        private async Task<List<Mission>> CancelActiveRescueMissionsAsync(Mission failedMission, string reason, CancellationToken token)
        {
            List<Mission> rescues = await EnumerateRescueMissionsAsync(failedMission, token).ConfigureAwait(false);
            List<Mission> cancelled = new List<Mission>();

            foreach (Mission rescue in rescues.Where(item => IsCancellableRescueStatus(item.Status)))
            {
                if (!String.IsNullOrWhiteSpace(rescue.CaptainId))
                {
                    try
                    {
                        await _Admiral.RecallCaptainAsync(rescue.CaptainId, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "could not recall captain " + rescue.CaptainId + " while cancelling rescue " + rescue.Id + ": " + ex.Message);
                    }
                }

                rescue.Status = MissionStatusEnum.Cancelled;
                rescue.FailureReason = reason;
                rescue.ProcessId = null;
                rescue.CompletedUtc = DateTime.UtcNow;
                rescue.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(rescue, token).ConfigureAwait(false);
                cancelled.Add(rescue);
            }

            return cancelled;
        }

        private async Task CloseActiveMissionIncidentsAsync(AuthContext auth, Mission mission, string note, CancellationToken token)
        {
            List<Incident> active = await _Incidents.EnumerateActiveAsync(auth, new IncidentQuery
            {
                MissionId = mission.Id
            }, token).ConfigureAwait(false);

            foreach (Incident incident in active)
            {
                Incident updated = await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Closed,
                    RecoveryNotes = AppendNote(incident.RecoveryNotes, note),
                    ClosedUtc = DateTime.UtcNow
                }, token).ConfigureAwait(false);

                await EmitEventAsync("autonomous_recovery.incident_closed_cancelled_voyage",
                    "Autonomous recovery closed incident " + updated.Id + " because parent voyage " + mission.VoyageId + " is Cancelled.",
                    mission, updated.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<Incident> EnsureIncidentAsync(AuthContext auth, Mission mission, RecoveryDecision decision, CancellationToken token)
        {
            List<Incident> activeIncidents = await _Incidents.EnumerateActiveAsync(auth, new IncidentQuery
            {
                MissionId = mission.Id
            }, token).ConfigureAwait(false);

            Incident? active = activeIncidents.FirstOrDefault();
            string recoveryNote = decision.DispatchRescue
                ? "Autonomous policy classified this as recoverable and will dispatch one rescue mission."
                : "Autonomous policy stopped before rescue dispatch: " + decision.Reason + "." + BuildModeScopeNote(mission);

            if (active != null)
            {
                return await _Incidents.UpdateAsync(auth, active.Id, new IncidentUpsertRequest
                {
                    Summary = BuildIncidentSummary(mission, decision),
                    Severity = decision.DispatchRescue ? IncidentSeverityEnum.Medium : IncidentSeverityEnum.High,
                    RecoveryNotes = AppendNote(active.RecoveryNotes, recoveryNote)
                }, token).ConfigureAwait(false);
            }

            Incident created = await _Incidents.CreateAsync(auth, new IncidentUpsertRequest
            {
                Title = "Mission failed: " + Truncate(mission.Title, 96),
                Summary = BuildIncidentSummary(mission, decision),
                Status = IncidentStatusEnum.Open,
                Severity = decision.DispatchRescue ? IncidentSeverityEnum.Medium : IncidentSeverityEnum.High,
                VesselId = mission.VesselId,
                MissionId = mission.Id,
                VoyageId = mission.VoyageId,
                Impact = "Mission did not reach a successful landing.",
                RootCause = mission.FailureReason,
                RecoveryNotes = recoveryNote,
                DetectedUtc = mission.CompletedUtc ?? mission.LastUpdateUtc
            }, token).ConfigureAwait(false);

            await EmitEventAsync("autonomous_recovery.incident_opened",
                "Autonomous recovery opened incident " + created.Id + " for mission " + mission.Id,
                mission, created.Id, token).ConfigureAwait(false);
            return created;
        }

        private async Task EnsureReadOnlyJudgeFollowUpAsync(Mission mission, CancellationToken token)
        {
            if (!mission.IsReadOnlyMode
                || !String.Equals(
                    PersonaCatalog.NormalizeName(mission.Persona),
                    PersonaCatalog.Judge,
                    StringComparison.Ordinal))
                return;

            // A blocked Judge gave no verdict; its question is on the owner's incident, not a review follow-up.
            if (CaptainBlockedResult.IsBlockedFailure(mission.FailureReason))
                return;

            try
            {
                List<JudgeFollowUp> pending = await _Database.JudgeFollowUps
                    .EnumeratePendingAsync(mission.VesselId, token).ConfigureAwait(false);
                if (pending.Any(item => String.Equals(item.JudgeMissionId, mission.Id, StringComparison.Ordinal)))
                    return;

                string? recommendation = String.IsNullOrWhiteSpace(mission.ReviewComment)
                    ? null
                    : mission.ReviewComment.Trim();
                string verdict = (mission.FailureReason ?? String.Empty)
                    .Contains("NEEDS_REVISION", StringComparison.OrdinalIgnoreCase)
                    ? "NEEDS_REVISION"
                    : "FAIL";
                JudgeFollowUp followUp = await _CaptureJudgeFollowUp(mission, verdict, recommendation, token)
                    .ConfigureAwait(false);
                _Logging.Info(_Header + "persisted read-only Judge follow-up " + followUp.Id +
                    " for mission " + mission.Id + "; implementation remains outside the audit-only scope.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not persist read-only Judge follow-up for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private async Task<RunbookExecution?> ExecuteRecoveryRunbookAsync(
            AuthContext auth,
            Mission mission,
            Incident incident,
            RecoveryDecision decision,
            CancellationToken token)
        {
            try
            {
                Runbook runbook = await EnsureRecoveryRunbookAsync(auth, token).ConfigureAwait(false);
                RunbookExecution execution = await _Runbooks.StartExecutionAsync(auth, runbook.Id, new RunbookExecutionStartRequest
                {
                    Title = "Autonomous recovery for " + mission.Id,
                    IncidentId = incident.Id,
                    ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["missionId"] = mission.Id,
                        ["incidentId"] = incident.Id,
                        ["vesselId"] = mission.VesselId ?? String.Empty,
                        ["failureReason"] = mission.FailureReason ?? String.Empty,
                        ["missionMode"] = mission.Mode.ToString(),
                        ["decision"] = decision.DispatchRescue ? "dispatch_rescue" : "block"
                    },
                    Notes = "Decision: " + (decision.DispatchRescue ? "dispatch rescue" : "block") + ". Reason: " + decision.Reason + "." + BuildModeScopeNote(mission)
                }, token).ConfigureAwait(false);

                await _Runbooks.UpdateExecutionAsync(auth, execution.Id, new RunbookExecutionUpdateRequest
                {
                    Status = RunbookExecutionStatusEnum.Completed,
                    CompletedStepIds = runbook.Steps.Select(step => step.Id).ToList(),
                    Notes = "Autonomous recovery policy completed. Decision: " +
                        (decision.DispatchRescue ? "dispatch rescue." : "block. " + decision.Reason + ".") +
                        BuildModeScopeNote(mission)
                }, token).ConfigureAwait(false);

                return execution;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "runbook execution failed for mission " + mission.Id + ": " + ex.Message);
                return null;
            }
        }

        private async Task<Runbook> EnsureRecoveryRunbookAsync(AuthContext auth, CancellationToken token)
        {
            EnumerationResult<Runbook> existing = await _Runbooks.EnumerateAsync(auth, new RunbookQuery
            {
                Search = _RecoveryRunbookFileName,
                PageNumber = 1,
                PageSize = 100
            }, token).ConfigureAwait(false);

            Runbook? runbook = existing.Objects.FirstOrDefault(item =>
                String.Equals(item.FileName, _RecoveryRunbookFileName, StringComparison.OrdinalIgnoreCase));
            if (runbook != null)
            {
                RunbookParameter? existingMode = runbook.Parameters.FirstOrDefault(item =>
                    String.Equals(item.Name, "missionMode", StringComparison.OrdinalIgnoreCase));
                if (existingMode?.Required == true)
                    return runbook;

                List<RunbookParameter> parameters = runbook.Parameters
                    .Where(item => !String.Equals(item.Name, "missionMode", StringComparison.OrdinalIgnoreCase))
                    .Select(item => new RunbookParameter
                    {
                        Name = item.Name,
                        Label = item.Label,
                        Description = item.Description,
                        DefaultValue = item.DefaultValue,
                        Required = item.Required
                    })
                    .ToList();
                parameters.Add(new RunbookParameter
                {
                    Name = "missionMode",
                    Label = existingMode?.Label ?? "Mission Mode",
                    Description = existingMode?.Description,
                    DefaultValue = existingMode?.DefaultValue,
                    Required = true
                });

                return await _Runbooks.UpdateAsync(auth, runbook.Id, new RunbookUpsertRequest
                {
                    FileName = runbook.FileName,
                    Title = runbook.Title,
                    Description = runbook.Description,
                    WorkflowProfileId = runbook.WorkflowProfileId,
                    EnvironmentId = runbook.EnvironmentId,
                    EnvironmentName = runbook.EnvironmentName,
                    DefaultCheckType = runbook.DefaultCheckType,
                    Parameters = parameters,
                    Steps = runbook.Steps,
                    OverviewMarkdown = runbook.OverviewMarkdown,
                    Active = runbook.Active
                }, token).ConfigureAwait(false);
            }

            return await _Runbooks.CreateAsync(auth, new RunbookUpsertRequest
            {
                FileName = _RecoveryRunbookFileName,
                Title = "Autonomous Mission Recovery",
                Description = "Classify failed missions, open incidents, and dispatch bounded rescue work when safe.",
                Active = true,
                Parameters = new List<RunbookParameter>
                {
                    new RunbookParameter { Name = "missionId", Label = "Mission ID", Required = true },
                    new RunbookParameter { Name = "incidentId", Label = "Incident ID", Required = true },
                    new RunbookParameter { Name = "vesselId", Label = "Vessel ID", Required = false },
                    new RunbookParameter { Name = "failureReason", Label = "Failure Reason", Required = false },
                    new RunbookParameter { Name = "missionMode", Label = "Mission Mode", Required = true },
                    new RunbookParameter { Name = "decision", Label = "Decision", Required = true }
                },
                Steps = new List<RunbookStep>
                {
                    new RunbookStep { Title = "Classify failure", Instructions = "Decide whether the failure is recoverable or requires human review." },
                    new RunbookStep { Title = "Open incident", Instructions = "Create or update the incident tied to the failed mission." },
                    new RunbookStep { Title = "Apply policy", Instructions = "Dispatch one rescue mission when safe; otherwise leave the incident open for human review." }
                },
                OverviewMarkdown = "System runbook used by Armada's autonomous recovery orchestrator. It records the policy decision for failed missions and links the result to the incident."
            }, token).ConfigureAwait(false);
        }

        private async Task<Mission> DispatchRescueMissionAsync(
            Mission failedMission,
            Incident incident,
            string? startFromRef,
            CancellationToken token)
        {
            int attemptNumber = failedMission.RecoveryAttempts + 1;
            string rescuePersona = ResolveRescuePersona(failedMission.Persona);
            Objective? owningObjective = await ResolveOwningObjectiveAsync(failedMission, token).ConfigureAwait(false);
            List<SelectedPlaybook> rescuePlaybooks = await ResolveRescuePlaybooksAsync(
                failedMission,
                owningObjective,
                token).ConfigureAwait(false);
            Pipeline? recoveryPipeline = await ResolveRecoveryPipelineAsync(
                failedMission,
                owningObjective,
                token).ConfigureAwait(false);
            PipelineStage? recoveryWorkerStage = recoveryPipeline?.Stages
                .OrderBy(item => item.Order)
                .FirstOrDefault(item => String.Equals(item.PersonaName, rescuePersona, StringComparison.OrdinalIgnoreCase));
            Mission rescue = new Mission
            {
                TenantId = failedMission.TenantId,
                UserId = failedMission.UserId,
                VesselId = failedMission.VesselId,
                ParentMissionId = failedMission.Id,
                Persona = rescuePersona,
                // The rescue persona can differ from the failed mission persona. A reviewer tier
                // is a constraint for that reviewer, not for the Worker that revises its findings.
                // Use the recovery pipeline's Worker tier when set; otherwise let the Worker's
                // configured minimum choose an eligible tier.
                PreferredModel = PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    recoveryWorkerStage?.PreferredModel,
                    null,
                    _Settings.ModelTier.MinimumTierForPersona(rescuePersona)),
                StageOrder = recoveryWorkerStage?.Order,
                Priority = Math.Max(0, failedMission.Priority - 10),
                Title = "Rescue " + attemptNumber + ": " + Truncate(failedMission.Title, 100),
                Description = BuildRescueDescription(failedMission, incident, attemptNumber),
                StartFromRef = startFromRef,
                Mode = failedMission.Mode,
                RequiresReview = recoveryWorkerStage?.RequiresReview ?? false,
                ReviewDenyAction = recoveryWorkerStage?.ReviewDenyAction ?? ReviewDenyActionEnum.RetryStage,
                SelectedPlaybooks = ClonePlaybookSelections(rescuePlaybooks),
                // Carry the recovery budget forward onto the rescue itself. A rescue stage that
                // fails again is picked up by the sweep with RecoveryAttempts already at the
                // attempt count, so Classify blocks further rescues once the budget is spent and
                // the bounded revise->retest->rejudge loop terminates instead of recursing.
                RecoveryAttempts = attemptNumber
            };

            // A Worker revision must be re-verified before it lands rather than landing with no
            // review. That holds for a reviewer rejection (a Judge NEEDS_REVISION recovered by a
            // Worker) AND for a Worker that failed its gate inside a voyage: the pipeline cancelled
            // that voyage's TestEngineer and Judge when the Worker failed, so a standalone rescue
            // would be the only stage left, pass its own gate, and land through LocalMerge with no
            // reviewer ever reading the final code. Chain a re-Judge (and re-TestEngineer where the
            // vessel pipeline defines one) onto the revision in both cases. A standalone mission
            // with no voyage never had review stages and keeps its standalone rescue.
            bool chainReReview = String.Equals(rescuePersona, "Worker", StringComparison.Ordinal)
                && (IsReviewerPersona(failedMission.Persona) || !String.IsNullOrEmpty(failedMission.VoyageId));

            if (!chainReReview)
                return await _Admiral.DispatchMissionAsync(rescue, token).ConfigureAwait(false);

            return await DispatchRescueReviewLoopAsync(
                failedMission,
                rescue,
                attemptNumber,
                recoveryPipeline,
                rescuePlaybooks,
                token).ConfigureAwait(false);
        }

        private async Task<Objective?> ResolveOwningObjectiveAsync(Mission failedMission, CancellationToken token)
        {
            List<Objective> objectives = await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
            List<Objective> matches = objectives
                .Where(item => item != null
                    && ((item.MissionIds?.Contains(failedMission.Id, StringComparer.Ordinal) ?? false)
                        || (!String.IsNullOrWhiteSpace(failedMission.VoyageId)
                            && (item.VoyageIds?.Contains(failedMission.VoyageId, StringComparer.Ordinal) ?? false))))
                .OrderByDescending(item => !String.IsNullOrWhiteSpace(item.SuggestedPipelineId))
                .ThenByDescending(item => item.MissionIds?.Contains(failedMission.Id, StringComparer.Ordinal) ?? false)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            return matches.FirstOrDefault();
        }

        private async Task<Pipeline?> ResolveRecoveryPipelineAsync(
            Mission failedMission,
            Objective? owningObjective,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(failedMission.VesselId)) return null;

            Vessel? vessel = await _Database.Vessels.ReadAsync(failedMission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) return null;

            return await _Admiral.ResolvePipelineAsync(
                owningObjective?.SuggestedPipelineId,
                vessel,
                token).ConfigureAwait(false);
        }

        private async Task<List<SelectedPlaybook>> ResolveRescuePlaybooksAsync(
            Mission failedMission,
            Objective? owningObjective,
            CancellationToken token)
        {
            if (failedMission.PlaybookSnapshots != null && failedMission.PlaybookSnapshots.Count > 0)
            {
                return failedMission.PlaybookSnapshots.Select(snapshot => new SelectedPlaybook
                {
                    PlaybookId = snapshot.PlaybookId ?? snapshot.FileName,
                    DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent,
                    InlineFullContent = snapshot.Content
                }).ToList();
            }

            if (!String.IsNullOrWhiteSpace(failedMission.VoyageId))
            {
                List<SelectedPlaybook> voyageSelections = await _Database.Playbooks
                    .GetVoyageSelectionsAsync(failedMission.VoyageId, token).ConfigureAwait(false);
                if (voyageSelections.Count > 0)
                    return ClonePlaybookSelections(voyageSelections);
            }

            return ClonePlaybookSelections(owningObjective?.SuggestedPlaybooks);
        }

        private async Task<string?> ResolveRescueStartFromRefAsync(Mission failedMission, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(failedMission.CommitHash)) return failedMission.CommitHash.Trim();
            if (!String.IsNullOrWhiteSpace(failedMission.StartFromRef)) return failedMission.StartFromRef.Trim();
            if (String.IsNullOrWhiteSpace(failedMission.DependsOnMissionId)) return null;

            Mission? reviewed = await ReadMissionAsync(
                failedMission.TenantId,
                failedMission.DependsOnMissionId,
                token).ConfigureAwait(false);
            if (reviewed == null || !String.Equals(reviewed.VesselId, failedMission.VesselId, StringComparison.Ordinal))
                return null;
            return String.IsNullOrWhiteSpace(reviewed?.CommitHash) ? null : reviewed.CommitHash.Trim();
        }

        /// <summary>
        /// Link a recovery incident to each objective that already owns the failed mission or
        /// its voyage. Incident links are annotations and do not change objective dispatch lineage.
        /// </summary>
        internal async Task<int> LinkIncidentToOwningObjectivesAsync(
            AuthContext auth,
            Mission failedMission,
            Incident incident,
            CancellationToken token)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (failedMission == null) throw new ArgumentNullException(nameof(failedMission));
            if (incident == null) throw new ArgumentNullException(nameof(incident));
            if (String.IsNullOrWhiteSpace(failedMission.Id) && String.IsNullOrWhiteSpace(failedMission.VoyageId)) return 0;

            ObjectiveService objectiveService = new ObjectiveService(_Database, _Logging);
            HashSet<string> objectiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task AddMatchesAsync(ObjectiveQuery query)
            {
                while (true)
                {
                    EnumerationResult<Objective> page = await objectiveService
                        .EnumerateAsync(auth, query, token)
                        .ConfigureAwait(false);
                    foreach (Objective objective in page.Objects)
                        objectiveIds.Add(objective.Id);

                    if (page.PageNumber >= page.TotalPages || page.Objects.Count == 0) return;
                    query.PageNumber++;
                }
            }

            if (!String.IsNullOrWhiteSpace(failedMission.Id))
            {
                await AddMatchesAsync(new ObjectiveQuery
                {
                    MissionId = failedMission.Id,
                    PageNumber = 1,
                    PageSize = 200
                }).ConfigureAwait(false);
            }

            if (!String.IsNullOrWhiteSpace(failedMission.VoyageId))
            {
                await AddMatchesAsync(new ObjectiveQuery
                {
                    VoyageId = failedMission.VoyageId,
                    PageNumber = 1,
                    PageSize = 200
                }).ConfigureAwait(false);
            }

            foreach (string objectiveId in objectiveIds)
            {
                await objectiveService.LinkIncidentAsync(auth, objectiveId, incident.Id, token).ConfigureAwait(false);
                _Logging.Info(_Header + "incident " + incident.Id + " linked to objective " + objectiveId
                    + " for failed mission " + failedMission.Id);
            }

            return objectiveIds.Count;
        }

        /// <summary>
        /// Link a rescue voyage to every objective that owns the voyage it rescues.
        /// </summary>
        /// <remarks>
        /// A rescue continues the objective's own work, so the objective must own it. The
        /// objective scheduler counts running voyages through <see cref="Objective.VoyageIds"/>,
        /// for the fleet ceiling and per vessel alike, so an unlinked rescue is invisible to both:
        /// the fleet ran one voyage more than its stated ceiling, on a vessel the scheduler believed
        /// idle, exactly while that vessel was already failing. Linking also stops the scheduler
        /// re-dispatching the objective beside its own rescue. The link is best-effort: a rescue
        /// must never be refused because a record could not be updated.
        /// </remarks>
        /// <param name="failedMission">The mission being rescued; its voyage is the parent.</param>
        /// <param name="rescueVoyage">The voyage the rescue runs in.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of objectives the rescue voyage was linked to.</returns>
        internal async Task<int> LinkRescueVoyageToObjectivesAsync(Mission failedMission, Voyage rescueVoyage, CancellationToken token)
        {
            if (failedMission == null || rescueVoyage == null) return 0;
            if (String.IsNullOrWhiteSpace(failedMission.VoyageId)) return 0;

            int linked = 0;
            try
            {
                List<Objective> objectives = await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
                ObjectiveService objectiveService = new ObjectiveService(_Database, _Logging);

                foreach (Objective objective in objectives)
                {
                    if (objective == null || objective.VoyageIds == null) continue;
                    if (!objective.VoyageIds.Contains(failedMission.VoyageId, StringComparer.Ordinal)) continue;
                    if (objective.VoyageIds.Contains(rescueVoyage.Id, StringComparer.Ordinal)) continue;

                    AuthContext auth = AuthContext.Authenticated(
                        objective.TenantId ?? Constants.DefaultTenantId,
                        objective.UserId ?? Constants.DefaultUserId,
                        false,
                        true,
                        "AutonomousRecoveryOrchestrator",
                        principalDisplay: "Armada Autonomous Recovery");

                    await objectiveService.LinkVoyageAsync(auth, objective.Id, rescueVoyage.Id, token, isRescueLink: true).ConfigureAwait(false);
                    linked++;
                    _Logging.Info(_Header + "rescue voyage " + rescueVoyage.Id + " linked to objective " + objective.Id
                        + " (rescues voyage " + failedMission.VoyageId + ")");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not link rescue voyage " + rescueVoyage.Id + " to the objectives of voyage "
                    + failedMission.VoyageId + ": " + ex.Message);
            }

            return linked;
        }

        // Dispatch the Worker revision rescue as the root of a dedicated rescue voyage, then rebuild
        // every downstream stage from the owning objective's selected pipeline. The standard handoff
        // (MissionService) stamps each downstream stage with the revision branch and assigns it
        // once the prior stage produces work, so the re-Judge reviews exactly the branch the
        // revision lands on. Branch choice: the revision runs on a fresh captain branch (the
        // existing root-mission behavior; revising in place on the original branch would require
        // checkout-existing-branch support in dock provisioning, which is out of scope here), and
        // the re-Judge inherits that branch through handoff -- so the re-judge always targets the
        // branch the revision actually lands on.
        //
        // Creation-order race: the Worker root is dispatched via DispatchMissionQueuedAsync (which
        // persists the row and DEFERS assignment to the background dispatch queue) instead of
        // DispatchMissionAsync (which assigns synchronously). This guarantees the TestEngineer and
        // Judge dependent rows are created (Status=Pending) BEFORE the Worker can be assigned, run,
        // and transition to WorkProduced. TryHandoffToNextStageAsync only stamps the upstream
        // branch onto dependents that already exist when the Worker completes; if the Worker
        // finished before the dependents were created, the branch was never propagated and the
        // dependents parked at WaitingForDependency forever. Deferring the Worker assignment closes
        // that window. (MissionService.TryAssignAsync also self-heals a missed handoff lazily, so
        // this ordering plus that lazy path are belt-and-suspenders.)
        private async Task<Mission> DispatchRescueReviewLoopAsync(
            Mission failedMission,
            Mission workerRescue,
            int attemptNumber,
            Pipeline? recoveryPipeline,
            List<SelectedPlaybook> rescuePlaybooks,
            CancellationToken token)
        {
            // The rescue voyage is written before the admiral admits the Worker, so the hold is
            // checked first; otherwise a held dispatch leaves a created-then-cancelled voyage.
            _DispatchHold?.ThrowIfActive();
            Voyage rescueVoyage = await _Database.Voyages.CreateAsync(new Voyage(
                "Rescue " + attemptNumber + ": " + Truncate(failedMission.Title, 80),
                "Autonomous revise/retest/rejudge loop for failed mission " + failedMission.Id + ".")
            {
                TenantId = failedMission.TenantId,
                UserId = failedMission.UserId,
                Status = VoyageStatusEnum.InProgress
            }, token).ConfigureAwait(false);

            workerRescue.VoyageId = rescueVoyage.Id;
            try
            {
                rescueVoyage.SelectedPlaybooks = ClonePlaybookSelections(rescuePlaybooks);
                if (rescueVoyage.SelectedPlaybooks.Count > 0)
                {
                    await _Database.Playbooks.SetVoyageSelectionsAsync(
                        rescueVoyage.Id,
                        rescueVoyage.SelectedPlaybooks,
                        token).ConfigureAwait(false);
                }

                Mission dispatchedWorker = await _Admiral.DispatchMissionQueuedAsync(workerRescue, token).ConfigureAwait(false);

                await LinkRescueVoyageToObjectivesAsync(failedMission, rescueVoyage, token).ConfigureAwait(false);

                string upstreamMissionId = dispatchedWorker.Id;
                int missionCount = 1;
                List<PipelineStage> downstreamStages = await ResolveRecoveryStagesAsync(
                    failedMission,
                    recoveryPipeline,
                    token).ConfigureAwait(false);
                foreach (IGrouping<int, PipelineStage> stageGroup in downstreamStages.GroupBy(item => item.Order).OrderBy(item => item.Key))
                {
                    string groupDependencyId = upstreamMissionId;
                    string? lastMissionInGroup = null;
                    foreach (PipelineStage stage in stageGroup)
                    {
                        Mission chainedStage = await _Database.Missions.CreateAsync(
                            BuildChainedRescueStage(
                                failedMission,
                                rescueVoyage.Id,
                                groupDependencyId,
                                stage,
                                attemptNumber,
                                rescuePlaybooks),
                            token).ConfigureAwait(false);
                        await PersistRescuePlaybookSnapshotsAsync(chainedStage, token).ConfigureAwait(false);
                        lastMissionInGroup = chainedStage.Id;
                        missionCount++;
                    }

                    if (!String.IsNullOrWhiteSpace(lastMissionInGroup))
                        upstreamMissionId = lastMissionInGroup;
                }

                // A rescue-Judge PASS is subject to the real-signal gate: it needs independent green
                // Build/UnitTest Checks, and a rescue voyage has none unless something creates them.
                await ArmRescueChecksAsync(failedMission, rescueVoyage, token).ConfigureAwait(false);

                // A rescue voyage is dispatched like any other, so it announces itself the same way.
                await VoyageDispatchedEvent.EmitAsync(_Database, _Logging, rescueVoyage, failedMission.VesselId, missionCount).ConfigureAwait(false);

                return dispatchedWorker;
            }
            catch (FleetCapacityAdmissionException capacity)
            {
                await VoyageCancellation.CancelVoyageAsync(
                    _Database,
                    rescueVoyage,
                    "Rescue deferred: " + capacity.Code + ".",
                    CancellationToken.None,
                    _Admiral.RecallCaptainAsync).ConfigureAwait(false);
                await EmitEventAsync(
                    "autonomous_recovery.capacity_deferred",
                    capacity.Message,
                    failedMission,
                    null,
                    token).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await VoyageCancellation.CancelVoyageAsync(
                    _Database,
                    rescueVoyage,
                    "Rescue cancelled: initial mission graph creation failed.",
                    CancellationToken.None,
                    _Admiral.RecallCaptainAsync).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Arm Build and UnitTest checks for a rescue voyage so the rescue Judge's real-signal gate
        /// finds independent Checks that measured the revision. The records are created Pending
        /// through the same seam dispatch uses; the check executor stamps each with the rescue's
        /// branch and commit once a stage has committed and runs it then. Running them here
        /// instead would measure the vessel's default branch before the rescue Worker has started.
        /// Failures are swallowed: the rescue must proceed even if a record cannot be created (the
        /// gate then reports the specific rejection reason).
        /// </summary>
        /// <param name="failedMission">The failed mission being rescued; supplies vessel context.</param>
        /// <param name="rescueVoyage">The rescue voyage the checks are linked to.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of Check records armed.</returns>
        internal async Task<int> ArmRescueChecksAsync(Mission failedMission, Voyage rescueVoyage, CancellationToken token)
        {
            if (failedMission == null || rescueVoyage == null) return 0;
            if (String.IsNullOrWhiteSpace(failedMission.VesselId)) return 0;

            try
            {
                Vessel? vessel = await _Database.Vessels.ReadAsync(failedMission.VesselId, token).ConfigureAwait(false);
                if (vessel == null) return 0;

                VoyageCheckArmingService arming = new VoyageCheckArmingService(_Database, _Settings, _Logging);
                return await arming.ArmAsync(rescueVoyage, vessel, "autonomous_rescue", token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not arm rescue checks for voyage " + rescueVoyage.Id + ": " + ex.Message);
                return 0;
            }
        }

        // Build a downstream objective-pipeline stage for the rescue loop. The
        // stage carries the auto-rescue marker (so it is recognised as rescue work) and the
        // recovery budget, but no ParentMissionId -- it is a pipeline dependent of the Worker
        // rescue (via DependsOnMissionId), not a direct rescue of the original failure, so it does
        // not inflate the original failure's rescue accounting. The persona preamble and prior-
        // stage diff are injected by the standard MissionService handoff when the upstream stage
        // completes.
        internal Mission BuildChainedRescueStage(
            Mission failedMission,
            string voyageId,
            string dependsOnMissionId,
            PipelineStage stage,
            int attemptNumber,
            List<SelectedPlaybook> rescuePlaybooks)
        {
            return new Mission
            {
                TenantId = failedMission.TenantId,
                UserId = failedMission.UserId,
                VesselId = failedMission.VesselId,
                VoyageId = voyageId,
                DependsOnMissionId = dependsOnMissionId,
                StageOrder = stage.Order,
                Persona = stage.PersonaName,
                // A downstream stage uses its own configured pipeline tier and persona minimum.
                // The failed mission's tier belongs to its persona and must not constrain this one.
                PreferredModel = PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel,
                    null,
                    _Settings.ModelTier.MinimumTierForPersona(stage.PersonaName)),
                Priority = Math.Max(0, failedMission.Priority - 10),
                Status = MissionStatusEnum.Pending,
                RecoveryAttempts = attemptNumber,
                Mode = failedMission.Mode,
                RequiresReview = stage.RequiresReview,
                ReviewDenyAction = stage.ReviewDenyAction,
                SelectedPlaybooks = ClonePlaybookSelections(rescuePlaybooks),
                Title = "Rescue " + attemptNumber + " " + stage.PersonaName + ": " + Truncate(failedMission.Title, 90),
                Description = _RescueMarker + Environment.NewLine +
                    "Re-verification stage for the autonomous revision rescue of failed mission " + failedMission.Id + "." + Environment.NewLine +
                    "Confirm the revised branch resolves the original reviewer feedback before it lands."
            };
        }

        private async Task<List<PipelineStage>> ResolveRecoveryStagesAsync(
            Mission failedMission,
            Pipeline? recoveryPipeline,
            CancellationToken token)
        {
            if (recoveryPipeline != null)
            {
                PipelineStage? workerStage = recoveryPipeline.Stages
                    .OrderBy(item => item.Order)
                    .FirstOrDefault(item => String.Equals(item.PersonaName, "Worker", StringComparison.OrdinalIgnoreCase));
                if (workerStage != null)
                {
                    List<PipelineStage> selectedStages = recoveryPipeline.Stages
                        .Where(item => item.Order > workerStage.Order)
                        .OrderBy(item => item.Order)
                        .ThenBy(item => item.Id, StringComparer.Ordinal)
                        .ToList();
                    if (selectedStages.Count > 0)
                        return selectedStages;
                }
            }

            List<PipelineStage> fallback = new List<PipelineStage>();
            int order = 2;
            if (await VesselPipelineHasTestEngineerAsync(failedMission, token).ConfigureAwait(false))
                fallback.Add(new PipelineStage(order++, "TestEngineer"));
            fallback.Add(new PipelineStage(order, "Judge"));
            return fallback;
        }

        private async Task PersistRescuePlaybookSnapshotsAsync(Mission mission, CancellationToken token)
        {
            if (mission.SelectedPlaybooks == null
                || mission.SelectedPlaybooks.Count == 0
                || String.IsNullOrWhiteSpace(mission.TenantId)) return;

            PlaybookService playbooks = new PlaybookService(_Database, _Logging);
            List<MissionPlaybookSnapshot> snapshots = await playbooks.CreateSnapshotsAsync(
                mission.TenantId,
                mission.SelectedPlaybooks,
                token).ConfigureAwait(false);
            await _Database.Playbooks.SetMissionSnapshotsAsync(mission.Id, snapshots, token).ConfigureAwait(false);
        }

        private static List<SelectedPlaybook> ClonePlaybookSelections(List<SelectedPlaybook>? selections)
        {
            if (selections == null || selections.Count == 0) return new List<SelectedPlaybook>();

            return selections.Select(item => new SelectedPlaybook
            {
                PlaybookId = item.PlaybookId,
                DeliveryMode = item.DeliveryMode,
                InlineFullContent = item.InlineFullContent
            }).ToList();
        }

        private async Task<bool> VesselPipelineHasTestEngineerAsync(Mission failedMission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(failedMission.VesselId)) return false;

            Vessel? vessel = await _Database.Vessels.ReadAsync(failedMission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) return false;

            Pipeline? pipeline = await _Admiral.ResolvePipelineAsync(null, vessel, token).ConfigureAwait(false);
            if (pipeline == null) return false;

            return pipeline.Stages.Any(stage =>
                PersonaCatalog.Matches(stage.PersonaName, PersonaCatalog.TestEngineer));
        }

        private async Task<bool> IsAlreadyHandledAsync(Mission failedMission, CancellationToken token)
        {
            int maxAttempts = _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts;
            if (failedMission.LastRecoveryActionUtc.HasValue && failedMission.RecoveryAttempts >= maxAttempts)
                return true;

            List<MissionSummary> rescues = await EnumerateRescueMissionSummariesAsync(failedMission, token).ConfigureAwait(false);
            if (rescues.Any(rescue => !IsRetryableRescueTerminalFailure(rescue.Status)))
                return true;

            return false;
        }

        // Mutating voyage-cancel path. This runs inside SuppressCancelledVoyageRecoveryAsync,
        // which fires on every ~5s sweep tick for every cancelled-voyage failed candidate -- it is
        // NOT a cold path. Identify rescue missions via lightweight summaries (no description /
        // agent_output / diff_snapshot hydration) and then hydrate only the handful that actually
        // match. Enumerating every fully-hydrated vessel/global mission here would allocate every
        // mission's heavy text columns on each tick.
        private async Task<List<Mission>> EnumerateRescueMissionsAsync(Mission failedMission, CancellationToken token)
        {
            List<MissionSummary> rescueSummaries = await EnumerateRescueMissionSummariesAsync(failedMission, token).ConfigureAwait(false);

            List<Mission> rescues = new List<Mission>();
            foreach (MissionSummary summary in rescueSummaries)
            {
                Mission? rescue = await ReadMissionAsync(summary.TenantId, summary.Id, token).ConfigureAwait(false);
                if (rescue != null && IsAutoRescueMission(rescue))
                    rescues.Add(rescue);
            }

            return rescues;
        }

        /// <summary>
        /// Enumerate auto-rescue missions on a vessel that may have landed as a false-positive
        /// no-op: missions marked <see cref="MissionStatusEnum.Complete"/> whose recorded commit
        /// hash equals the supplied target-branch tip, meaning the rescue captain produced no
        /// commits yet reconciled to Complete on an identity push.
        ///
        /// The caller supplies the current target-branch tip (for example the resolved HEAD of
        /// the vessel default branch). The heuristic is best-effort: Armada keeps no historical
        /// snapshot of the target-branch HEAD at each mission's landing time, so a rescue whose
        /// commit hash happens to equal the current tip is flagged for operator review rather than
        /// confirmed as a false-positive. Rescues whose commit hash differs from the supplied tip
        /// (they advanced the branch with a real commit) are excluded.
        /// </summary>
        /// <param name="vesselId">Vessel to scan for suspect rescue missions.</param>
        /// <param name="targetBranchHead">Current target-branch tip commit hash to compare against.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of suspect Complete auto-rescue missions for operator review.</returns>
        public async Task<List<Mission>> FindSuspectNoOpRescueMissionsAsync(
            string vesselId,
            string targetBranchHead,
            CancellationToken token = default)
        {
            List<Mission> suspects = new List<Mission>();
            if (String.IsNullOrWhiteSpace(vesselId) || String.IsNullOrWhiteSpace(targetBranchHead))
                return suspects;

            string normalizedHead = targetBranchHead.Trim();
            List<MissionSummary> summaries = await _Database.Missions
                .EnumerateMissionSummariesByVesselAsync(vesselId, token).ConfigureAwait(false);

            foreach (MissionSummary summary in summaries)
            {
                if (summary.Status != MissionStatusEnum.Complete) continue;
                if (String.IsNullOrWhiteSpace(summary.CommitHash)) continue;
                if (!String.Equals(summary.CommitHash.Trim(), normalizedHead, StringComparison.OrdinalIgnoreCase)) continue;

                Mission? mission = await ReadMissionAsync(summary.TenantId, summary.Id, token).ConfigureAwait(false);
                if (mission == null) continue;
                if (!IsAutoRescueMission(mission)) continue;

                suspects.Add(mission);
            }

            return suspects;
        }

        private async Task MarkPolicyBlockedAsync(Mission mission, CancellationToken token)
        {
            mission.RecoveryAttempts = Math.Max(
                mission.RecoveryAttempts + 1,
                _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts);
            mission.LastRecoveryActionUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
        }

        private async Task NudgeStalledLiveCaptainsAsync(CancellationToken token)
        {
            if (!_Settings.AutonomousRecovery.SendStallMailNudges)
            {
                _NudgeSuppressedMissions.Clear();
                return;
            }

            double thresholdMinutes = Math.Max(1.0, _Settings.StallThresholdMinutes * _Settings.AutonomousRecovery.StallMailNudgeThresholdRatio);
            List<Captain> working = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);

            // A suppression record is kept only while a Working captain still holds the mission.
            HashSet<string> heldMissionIds = working
                .Where(item => !String.IsNullOrWhiteSpace(item.CurrentMissionId))
                .Select(item => item.CurrentMissionId!)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string suppressedMissionId in _NudgeSuppressedMissions.Keys)
            {
                if (!heldMissionIds.Contains(suppressedMissionId))
                    _NudgeSuppressedMissions.TryRemove(suppressedMissionId, out byte _);
            }

            foreach (Captain captain in working)
            {
                if (!captain.LastHeartbeatUtc.HasValue || String.IsNullOrWhiteSpace(captain.CurrentMissionId))
                    continue;

                DateTime nowUtc = DateTime.UtcNow;

                // Output first: a captain whose heartbeat (or provider progress, when the runtime
                // reports it) is inside the window needs no stall decision at all.
                DateTime? lastProviderProgressUtc = null;
                _ProviderProgress?.TryGet(captain.Id, out lastProviderProgressUtc);
                ProviderStallKind stallKind = CaptainStallEvaluator.ClassifyOutput(captain, lastProviderProgressUtc, thresholdMinutes, nowUtc);
                if (stallKind == ProviderStallKind.None) continue;

                Mission? mission = await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                if (mission == null || !IsLiveMissionStatus(mission.Status))
                    continue;

                // Startup grace: never nudge a mission that began within the stall threshold.
                // The per-captain provider-progress tracker can carry stale progress from a prior
                // mission, which classifies a freshly launched captain as a silent stall within
                // seconds (probe run 2026-08-10: healthy captain nudged 12s after launch).
                if (ProviderStallClassifier.IsWithinStartupGrace(mission.StartedUtc, nowUtc, thresholdMinutes))
                    continue;

                // A captain whose output already carries its terminal marker is finished, not stalled.
                // A nudge asks it to continue the mission, and a finished reviewer answers by reviewing
                // again; the lifecycle handler completes the stage after its grace period instead.
                if (_TerminalMarkers != null
                    && _TerminalMarkers.TryGet(mission.Id, out TerminalMarkerRecord? marker)
                    && marker != null)
                {
                    long suppressedTotal = _TerminalMarkers.RecordSuppressedNudge();
                    string markerText = "[" + marker.MarkerType + "] " + marker.Value;
                    _Logging.Info(_Header + "stall nudge withheld for captain " + captain.Id + " on mission " + mission.Id
                        + ": its output already carries terminal marker " + markerText
                        + " (withheld nudges this process: " + suppressedTotal + ")");
                    if (_NudgeSuppressedMissions.TryAdd(mission.Id, 0))
                    {
                        await EmitEventAsync("autonomous_recovery.mail_nudge_suppressed",
                            "Autonomous Mail nudge withheld for captain " + captain.Id + " on mission " + mission.Id
                            + " (stall kind: " + stallKind + "): output already carries terminal marker " + markerText
                            + " seen at " + marker.FirstSeenUtc.ToString("u") + ".",
                            mission, null, token).ConfigureAwait(false);
                    }
                    continue;
                }

                if (await HasRecentAutoNudgeAsync(captain, token).ConfigureAwait(false))
                    continue;

                // Quiet output is not a stall on its own: the shared evaluator also reads the dock
                // worktree and the branch tip, and records the decision with its evidence.
                CaptainStallDecision stall = await _StallEvaluator.EvaluateAsync(
                    captain, mission, thresholdMinutes, nowUtc, lastProviderProgressUtc, token).ConfigureAwait(false);
                await _StallEvaluator.RecordAsync(stall, "autonomous_recovery_nudge", captain, mission, token).ConfigureAwait(false);
                if (!stall.IsStalled) continue;

                string reason = StallReasonFor(stallKind, captain, lastProviderProgressUtc, nowUtc);
                Signal signal = new Signal(SignalTypeEnum.Mail,
                    _NudgeMarker + " " + reason +
                    " on mission " + mission.Id + ". Please report status, continue the mission, or fail with a specific blocker.");
                signal.TenantId = captain.TenantId ?? mission.TenantId;
                signal.UserId = captain.UserId ?? mission.UserId;
                signal.ToCaptainId = captain.Id;
                await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

                await EmitEventAsync("autonomous_recovery.mail_nudge_sent",
                    "Autonomous Mail nudge sent to captain " + captain.Id + " for mission " + mission.Id +
                    " (stall kind: " + stallKind + "). Evidence: " + stall.DescribeEvidence(),
                    mission, null, token).ConfigureAwait(false);
            }
        }

        // Build the human-readable stall reason. The kind tag in parentheses is what runbooks
        // and the operator grep on; the prose is what the captain sees in the Mail payload.
        // <paramref name="lastProviderProgressUtc"/> is the actual provider-progress timestamp
        // from the tracker (when wired) so the ProviderSilentStall prose reports the true silent
        // duration rather than the still-fresh heartbeat time.
        private static string StallReasonFor(
            ProviderStallKind kind,
            Captain captain,
            DateTime? lastProviderProgressUtc,
            DateTime nowUtc)
        {
            switch (kind)
            {
                case ProviderStallKind.ProviderSilentStall:
                    return "Armada has not seen provider progress for " +
                        (lastProviderProgressUtc.HasValue
                            ? (nowUtc - lastProviderProgressUtc.Value).TotalMinutes.ToString("F1")
                            : "an unknown number of") +
                        " minutes on captain " + captain.Id +
                        " (provider_silent_stall: process is emitting output but the underlying provider has gone silent)";
                case ProviderStallKind.HeartbeatAndProviderStall:
                    return "Armada has not seen captain output or provider progress for " +
                        (nowUtc - captain.LastHeartbeatUtc!.Value).TotalMinutes.ToString("F1") +
                        " minutes on captain " + captain.Id +
                        " (provider_and_heartbeat_stall: process is alive but both heartbeat and provider progress are stale)";
                default:
                    return "Armada has not seen progress for " +
                        (nowUtc - captain.LastHeartbeatUtc!.Value).TotalMinutes.ToString("F1") +
                        " minutes on mission (heartbeat_stall)";
            }
        }

        private async Task<bool> HasRecentAutoNudgeAsync(Captain captain, CancellationToken token)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-_Settings.AutonomousRecovery.StallMailNudgeCooldownMinutes);
            EnumerationResult<Signal> page = !String.IsNullOrWhiteSpace(captain.TenantId)
                ? await _Database.Signals.EnumerateAsync(captain.TenantId, new EnumerationQuery
                {
                    PageNumber = 1,
                    PageSize = 100,
                    SignalType = SignalTypeEnum.Mail.ToString(),
                    ToCaptainId = captain.Id
                }, token).ConfigureAwait(false)
                : await _Database.Signals.EnumerateAsync(new EnumerationQuery
                {
                    PageNumber = 1,
                    PageSize = 100,
                    SignalType = SignalTypeEnum.Mail.ToString(),
                    ToCaptainId = captain.Id
                }, token).ConfigureAwait(false);

            return page.Objects.Any(item =>
                item.CreatedUtc >= cutoff
                && String.Equals(item.ToCaptainId, captain.Id, StringComparison.Ordinal)
                && item.Type == SignalTypeEnum.Mail
                && (item.Payload ?? String.Empty).Contains(_NudgeMarker, StringComparison.Ordinal));
        }

        private async Task EmitEventAsync(string eventType, string message, Mission mission, string? incidentId, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                EntityType = incidentId != null ? "incident" : "mission",
                EntityId = incidentId ?? mission.Id,
                MissionId = mission.Id,
                VesselId = mission.VesselId,
                VoyageId = mission.VoyageId
            };
            EventOwnerScope.ApplyFromMission(evt, mission);

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        private static AuthContext BuildAuth(Mission mission)
        {
            return AuthContext.Authenticated(
                mission.TenantId ?? Constants.DefaultTenantId,
                mission.UserId ?? Constants.DefaultUserId,
                false,
                true,
                "AutonomousRecovery",
                principalDisplay: "Armada Autonomous Recovery");
        }

        private static string BuildIncidentSummary(Mission mission, RecoveryDecision decision)
        {
            return "Mission " + mission.Id + " is " + mission.Status + ". " +
                "Reason: " + (String.IsNullOrWhiteSpace(mission.FailureReason) ? "not recorded" : mission.FailureReason) + ". " +
                "Policy: " + (decision.DispatchRescue ? "dispatch rescue" : "block") + " (" + decision.Reason + ").";
        }

        private static string BuildModeScopeNote(Mission mission)
        {
            if (mission.IsReadOnlyMode)
                return " Preserved mode: " + mission.Mode + ". Scope: audit-only; no production edits or implementation rescue.";

            return " Mode: " + mission.Mode + ". Scope: implementation recovery.";
        }

        // Hard size cap on the prior mission's description embedded in the rescue brief.
        // Azure OpenAI's content_filter rejects prompts that carry many repeated warning lines
        // (BL0005, CS0618, etc.) packed into one block, which is exactly the shape a
        // failed DoD-gate build log has. A bounded brief keeps the prompt under the filter
        // threshold while still giving the rescue captain the scope block and the
        // reviewer feedback that are actually useful.
        internal const int _MaxRescueDescriptionChars = 6000;
        internal const int _MaxRescueDiagnosticsChars = 1500;
        internal const int _MaxRescueReviewerFeedbackChars = 2000;

        internal static string BuildRescueDescription(Mission failedMission, Incident incident, int attemptNumber)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(_RescueMarker);
            sb.AppendLine("Autonomous rescue attempt " + attemptNumber + " for failed mission " + failedMission.Id + ".");
            sb.AppendLine();
            sb.AppendLine("Incident: " + incident.Id);
            sb.AppendLine("Original title: " + failedMission.Title);
            sb.AppendLine("Failure status: " + failedMission.Status);
            sb.AppendLine("Failure reason: " + (failedMission.FailureReason == null
                ? "not recorded"
                : TruncateForBrief(failedMission.FailureReason, _MaxRescueDiagnosticsChars)));
            if (!String.IsNullOrWhiteSpace(failedMission.BranchName))
                sb.AppendLine("Original branch: " + failedMission.BranchName);
            if (!String.IsNullOrWhiteSpace(failedMission.ReviewComment))
            {
                sb.AppendLine();
                sb.AppendLine("Reviewer feedback to address:");
                sb.AppendLine(TruncateReviewerFeedbackForBrief(failedMission.ReviewComment.Trim(), _MaxRescueReviewerFeedbackChars));
            }
            sb.AppendLine();
            sb.AppendLine("Objective:");
            sb.AppendLine("Recover the original mission without repeating the failure. Inspect the original failure, make the smallest corrective change, run the vessel's workflow profile checks when available, and leave explicit evidence in Armada records.");
            sb.AppendLine();
            sb.AppendLine("Original mission description:");
            string originalDescription = failedMission.Description ?? "(no description recorded)";

            // Shrink the older prior-stage blocks before the size cap is applied. The cap keeps the HEAD
            // of the description, and handoff blocks sit at the end, so an oversized description loses
            // every one of them. Reducing the older blocks to references first means the newest one --
            // the context the rescued mission was actually working from -- can still fit.
            originalDescription = MissionService.CompactOlderHandoffBlocks(originalDescription, keepNewestFull: true);

            string sanitized = SanitizeOriginalDescriptionForRescue(originalDescription);
            sb.AppendLine(sanitized);
            return sb.ToString();
        }

        // Reduce a prior mission's description to a bounded, low-filter-risk digest.
        // The original rescue brief embedded the full description verbatim, which on
        // DoD-gate failures is the entire build output (thousands of repeated
        // warning lines). Azure OpenAI's content policy rejects that shape. This
        // method keeps the parts a rescue captain actually needs (the architect scope
        // block, the first line of actionable diagnostics, the failure reason digest)
        // and discards the rest.
        internal static string SanitizeOriginalDescriptionForRescue(string originalDescription)
        {
            if (String.IsNullOrEmpty(originalDescription))
                return "(no description recorded)";

            // Hard cap first. If the description is already small enough, return as-is.
            if (originalDescription.Length <= _MaxRescueDescriptionChars)
                return originalDescription;

            // Split the description into a scope block (the architect handoff / mission
            // tasks) and a diagnostics block (ACTIONABLE DIAGNOSTICS / OUTPUT TAIL).
            // The scope block is the part that carries recovery value; the diagnostics
            // block is the part that triggers Azure's content filter.
            int diagnosticsStart = IndexOfAny(originalDescription,
                "--- ACTIONABLE DIAGNOSTICS ---",
                "ACTIONABLE DIAGNOSTICS",
                "--- OUTPUT TAIL ---",
                "OUTPUT TAIL");
            string scope;
            string diagnostics;
            if (diagnosticsStart > 0)
            {
                scope = originalDescription.Substring(0, diagnosticsStart).TrimEnd();
                diagnostics = originalDescription.Substring(diagnosticsStart).TrimStart();
            }
            else
            {
                scope = originalDescription;
                diagnostics = String.Empty;
            }

            // Truncate the scope to the head of the description. Architect briefs and
            // mission tasks live at the top; long failure logs and outputs follow.
            string scopeTruncated = TruncateForBrief(scope, _MaxRescueDescriptionChars - 200);

            if (diagnostics.Length == 0)
                return scopeTruncated;

            // Keep only the first slice of the diagnostics so a rescue captain can see
            // the leading error code and the first actionable line. Past the first 1.5KB
            // the value is noise and the filter risk is real.
            string diagnosticsTruncated = TruncateForBrief(diagnostics, _MaxRescueDiagnosticsChars);
            return scopeTruncated
                + Environment.NewLine
                + "--- (ACTIONABLE DIAGNOSTICS truncated; full failure log in admiral log) ---"
                + Environment.NewLine
                + diagnosticsTruncated;
        }

        // The section headers that carry a Judge report's actionable content. A Judge
        // writes its findings first and its instructions last, so a head-first cut of
        // an over-cap report keeps the diagnosis and drops the deliverable.
        private static readonly string[] _ReviewerFeedbackTailHeaders =
        {
            "## Suggested Follow-ups",
            "## Verdict"
        };

        // Truncate reviewer feedback for the rescue brief. A Judge report has a fixed
        // shape (Completeness, Correctness, Tests, Failure Modes, Suggested Follow-ups,
        // Verdict, then the [ARMADA:VERDICT] line) and the part a rescue captain must
        // act on is at the END. When the report exceeds the cap, the tail sections are
        // kept whole first, the remaining budget is filled from the head, and the marker
        // sits where the omitted middle was and names the sections it dropped. Text
        // without those headers (a gate log, a free-form review) keeps the head-first
        // cut, because its signal is wherever it starts.
        internal static string TruncateReviewerFeedbackForBrief(string source, int maxChars)
        {
            if (String.IsNullOrEmpty(source) || source.Length <= maxChars)
                return source ?? String.Empty;

            // A Judge transcript often opens with tool narration ("Let me read the
            // brief...") before its first section header. That preamble carries no
            // finding, and under a head-first budget it is exactly what survives while
            // the findings fall off the end. Drop it before any budget is applied.
            int firstSection = IndexOfFirstSectionHeader(source);
            if (firstSection > 0)
            {
                source = "--- (" + firstSection + " chars of reviewer narration before the first section omitted; remainder in admiral log) ---"
                    + Environment.NewLine
                    + source.Substring(firstSection);
                if (source.Length <= maxChars)
                    return source;
            }

            int tailStart = IndexOfAny(source, _ReviewerFeedbackTailHeaders);
            if (tailStart <= 0)
                return TruncateForBrief(source, maxChars);

            string tail = source.Substring(tailStart).TrimEnd();
            string head = source.Substring(0, tailStart).TrimEnd();

            // Every section header the cut will hide: those in the head past the budget.
            // Computed before the head is cut so the marker can name them.
            int headBudget = maxChars - tail.Length - _OmittedSectionsMarkerReserve;
            if (headBudget < _MinimumHeadChars)
            {
                // The tail alone is at or over the cap. Keep the END of the tail: the
                // Verdict and the [ARMADA:VERDICT] line are the last things written.
                int keep = Math.Max(0, maxChars - _OmittedSectionsMarkerReserve);
                string kept = tail.Length > keep ? tail.Substring(tail.Length - keep) : tail;
                int newline = kept.IndexOf(Environment.NewLine, StringComparison.Ordinal);
                if (newline > 0 && newline < kept.Length - 1)
                    kept = kept.Substring(newline + Environment.NewLine.Length);
                return "--- (reviewer feedback truncated: " + (source.Length - kept.Length) + " of " + source.Length
                    + " chars omitted before this point, including "
                    + DescribeSectionHeaders(source.Substring(0, source.Length - kept.Length))
                    + "; remainder in admiral log) ---"
                    + Environment.NewLine
                    + kept;
            }

            string headKept;
            string omitted;
            if (head.Length <= headBudget)
            {
                headKept = head;
                omitted = String.Empty;
            }
            else
            {
                int cut = head.LastIndexOf(Environment.NewLine, headBudget, StringComparison.Ordinal);
                if (cut <= 0)
                    cut = headBudget;
                headKept = head.Substring(0, cut).TrimEnd();
                omitted = head.Substring(cut);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(headKept);
            if (omitted.Length > 0)
            {
                sb.Append(Environment.NewLine);
                sb.Append("--- (reviewer feedback truncated: ")
                  .Append(omitted.Length).Append(" of ").Append(source.Length)
                  .Append(" chars omitted here, including ")
                  .Append(DescribeSectionHeaders(omitted))
                  .Append("; the sections below are kept whole; remainder in admiral log) ---");
            }
            sb.Append(Environment.NewLine);
            sb.Append(Environment.NewLine);
            sb.Append(tail);
            return sb.ToString();
        }

        // The offset of the first "## " section header that starts a line, or -1 when
        // the text has none (a gate log, a free-form review).
        private static int IndexOfFirstSectionHeader(string source)
        {
            if (source.StartsWith("## ", StringComparison.Ordinal)) return 0;
            int index = source.IndexOf(Environment.NewLine + "## ", StringComparison.Ordinal);
            if (index < 0) index = source.IndexOf("\n## ", StringComparison.Ordinal);
            if (index < 0) return -1;
            return index + (source.Substring(index).StartsWith(Environment.NewLine, StringComparison.Ordinal)
                ? Environment.NewLine.Length : 1);
        }

        // Room reserved for the omitted-sections marker inside the reviewer-feedback cap,
        // and the smallest head worth keeping before the tail-only fallback applies.
        private const int _OmittedSectionsMarkerReserve = 220;
        private const int _MinimumHeadChars = 200;

        // Name the "## " section headers inside an omitted block, in order, so the rescue
        // captain knows which parts of the report it is not seeing. "no section headers"
        // when the omitted text carried none.
        internal static string DescribeSectionHeaders(string omitted)
        {
            List<string> names = new List<string>();
            if (!String.IsNullOrEmpty(omitted))
            {
                string[] lines = omitted.Split('\n');
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.StartsWith("## ", StringComparison.Ordinal))
                    {
                        string name = line.Substring(3).Trim();
                        if (name.Length > 0 && !names.Contains(name))
                            names.Add(name);
                    }
                }
            }

            if (names.Count == 0)
                return "no section headers";
            return "the sections " + String.Join(", ", names);
        }

        // Truncate a block to a target size on a line boundary and append a marker.
        // The marker tells the rescue captain where the truncation happened so they
        // do not treat the truncated tail as complete.
        internal static string TruncateForBrief(string source, int maxChars)
        {
            if (String.IsNullOrEmpty(source) || source.Length <= maxChars)
                return source ?? String.Empty;

            int cut = source.LastIndexOf(Environment.NewLine, maxChars, StringComparison.Ordinal);
            if (cut <= 0)
                cut = maxChars;
            return source.Substring(0, cut)
                + Environment.NewLine
                + "--- (truncated to " + cut + " of " + source.Length + " chars; remainder in admiral log) ---";
        }

        private static int IndexOfAny(string source, params string[] needles)
        {
            int best = -1;
            foreach (string needle in needles)
            {
                int idx = source.IndexOf(needle, StringComparison.Ordinal);
                if (idx >= 0 && (best < 0 || idx < best))
                    best = idx;
            }
            return best;
        }

        private static readonly string[] _ReviewerPersonas =
        {
            "judge",
            "testengineer",
            "usabilityengineer",
            "diagnosticprotocolreviewer",
            "tenantsecurityreviewer",
            "portingreferenceanalyst"
        };

        private static string ResolveRescuePersona(string? failedPersona)
        {
            if (IsReviewerPersona(failedPersona)) return "Worker";
            return String.IsNullOrWhiteSpace(failedPersona) ? "Worker" : failedPersona.Trim();
        }

        /// <summary>
        /// Whether a failed mission's persona is a reviewer/judge persona. A reviewer rejection
        /// (for example a Judge NEEDS_REVISION) is recovered by a Worker revision, and that
        /// revision must be re-reviewed before it lands rather than landing un-judged.
        /// </summary>
        private static bool IsReviewerPersona(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return false;

            string normalized = persona.Trim().ToLowerInvariant().Replace(" ", "");
            if (_ReviewerPersonas.Any(item => String.Equals(item, normalized, StringComparison.Ordinal)))
                return true;

            return normalized.EndsWith("reviewer", StringComparison.Ordinal)
                || normalized.EndsWith("analyst", StringComparison.Ordinal);
        }

        private static string AppendNote(string? existing, string note)
        {
            if (String.IsNullOrWhiteSpace(existing))
                return note;
            if (existing.Contains(note, StringComparison.OrdinalIgnoreCase))
                return existing;
            return existing.TrimEnd() + Environment.NewLine + Environment.NewLine + note;
        }

        // Render the repeated tests for an incident note, bounded so a large set does not write an
        // unwieldy note. The set is already complete and identical between rescue and parent.
        private static string FormatRepeatedTests(IReadOnlyList<string> tests)
        {
            const int maxListed = 20;
            if (tests.Count <= maxListed)
                return String.Join(", ", tests);
            return String.Join(", ", tests.Take(maxListed)) + ", and " + (tests.Count - maxListed) + " more";
        }

        private static bool IsRecoverableTerminalStatus(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Failed || status == MissionStatusEnum.LandingFailed;
        }

        private static bool IsRetryableRescueTerminalFailure(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.LandingFailed;
        }

        private static bool IsCancellableRescueStatus(MissionStatusEnum status)
        {
            return status != MissionStatusEnum.Complete
                && status != MissionStatusEnum.Failed
                && status != MissionStatusEnum.LandingFailed
                && status != MissionStatusEnum.Cancelled;
        }

        private static bool IsLiveMissionStatus(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Assigned
                || status == MissionStatusEnum.InProgress
                || status == MissionStatusEnum.Testing
                || status == MissionStatusEnum.Review;
        }

        private static bool IsAutoRescueMission(Mission mission)
        {
            return RescueMissionMarker.IsAutoRescue(mission);
        }

        /// <summary>
        /// Determine whether a failure reason matches the Anthropic ClaudeCode thinking-block
        /// replay error. The signature is an HTTP 400 response stating that a "thinking" or
        /// "redacted_thinking" block in the latest assistant message "cannot be modified".
        /// Returns false for null/empty input and for unrelated failures (including ordinary
        /// process failures and 400 responses without the thinking-block signature).
        /// </summary>
        /// <param name="failureReason">Recorded mission failure reason.</param>
        public static bool IsClaudeThinkingBlockFailure(string? failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) return false;

            string normalized = failureReason.ToLowerInvariant();
            if (!normalized.Contains("400", StringComparison.Ordinal)) return false;
            if (!normalized.Contains("cannot be modified", StringComparison.Ordinal)) return false;

            return normalized.Contains("thinking", StringComparison.Ordinal)
                || normalized.Contains("redacted_thinking", StringComparison.Ordinal);
        }

        // When a ClaudeCode captain fails on the Anthropic thinking-block replay error, the retry
        // must run without extended thinking. We cannot carry a per-mission runtime override without
        // a new persisted Mission column (out of scope), so instead we set the disable-extended-thinking
        // flag on the captain that picked up the rescue. Captain.RuntimeOptionsJson is already a
        // persisted column, so no schema migration is required.
        private async Task ApplyClaudeThinkingDisableAsync(Mission failedMission, Mission rescue, CancellationToken token)
        {
            if (!IsClaudeThinkingBlockFailure(failedMission.FailureReason)) return;
            if (!await WasRunByClaudeCodeCaptainAsync(failedMission, token).ConfigureAwait(false)) return;
            if (String.IsNullOrWhiteSpace(rescue.CaptainId)) return;

            Captain? rescueCaptain = await _Database.Captains.ReadAsync(rescue.CaptainId, token).ConfigureAwait(false);
            if (rescueCaptain == null || rescueCaptain.Runtime != AgentRuntimeEnum.ClaudeCode) return;

            rescueCaptain.RuntimeOptionsJson = CaptainRuntimeOptions.WithDisableExtendedThinking(rescueCaptain, true);
            await _Database.Captains.UpdateAsync(rescueCaptain, token).ConfigureAwait(false);

            await EmitEventAsync("autonomous_recovery.claude_thinking_disabled",
                "Autonomous recovery disabled extended thinking for ClaudeCode rescue captain " + rescueCaptain.Id +
                " on rescue mission " + rescue.Id + " of failed mission " + failedMission.Id,
                rescue, null, token).ConfigureAwait(false);
        }

        private async Task<bool> WasRunByClaudeCodeCaptainAsync(Mission failedMission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(failedMission.CaptainId)) return false;
            Captain? captain = await _Database.Captains.ReadAsync(failedMission.CaptainId, token).ConfigureAwait(false);
            return captain != null && captain.Runtime == AgentRuntimeEnum.ClaudeCode;
        }

        /// <summary>
        /// Determine whether a mission failed because its captain refusal was stopped: its one continuation on an
        /// alternate runtime was already spent, or no approved alternate runtime exists. A rescue would repeat the
        /// blocked path, so such a failure is routed to the operator instead.
        /// </summary>
        /// <param name="reason">Recorded mission failure reason.</param>
        /// <returns>True when the failure opens with the stopped-refusal prefix.</returns>
        public static bool IsPolicyRefusalFailure(string? reason)
        {
            return !String.IsNullOrWhiteSpace(reason)
                && reason.TrimStart().StartsWith(PolicyRefusalContinuationService.StoppedReasonPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Determine whether a failure was caused by the ENVIRONMENT the mission ran in rather than
        /// by the work the stage produced.
        /// </summary>
        /// <remarks>
        /// A rescue re-runs the same brief in the same environment with a different captain. When
        /// the cause is a missing dependency commit, an unprovisioned sibling, an absent environment
        /// variable or an unusable working directory, nothing the replacement captain can do will
        /// change the outcome -- so the rescue fails identically and spends the recovery budget that
        /// a genuine defect would have needed. stage_base_missing already tells the operator it is
        /// "a provisioning fault, not a defect in the stage's own work"; this is that sentence
        /// driving the policy instead of only describing it.
        /// </remarks>
        /// <param name="reason">Recorded mission failure reason.</param>
        /// <returns>True when the failure is environmental and must be routed to the operator.</returns>
        public static bool IsEnvironmentalFailure(string? reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) return false;

            string normalized = reason.ToLowerInvariant();
            string[] environmentalMarkers =
            {
                "stage_base_missing",
                "start_from_ref_missing",
                "start_from_ref_base_missing",
                "start_from_ref_unverified",
                "provisioning fault",
                "ineffective_rescue",
                "working_directory_sync_failed",
                "usable working directory",
                "environment variable",
                "no workflow profile",
                "not a git repository",
                "could not provision"
            };

            return environmentalMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        }

        private static bool HasSeriousFailureReason(string reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) return false;

            string normalized = reason.ToLowerInvariant();
            string[] seriousMarkers =
            {
                "protected path",
                "review denied",
                "approval",
                "unauthorized",
                "forbidden",
                "invalid api key",
                "authentication failed",
                "not logged in",
                "login required",
                "quota",
                "rate limit",
                "recovery exhausted",
                "blocked by failed dependency",
                "vessel deleted",
                // A Judge PASS rejected by the real-signal gate (no green independent Checks, a
                // failed Check, or Checks that never resolved) is an operator-attachment problem,
                // not a substantive rejection of the work. A rescue Judge on the same branch
                // re-reviews already-verified green work and cannot attach Checks itself, so it
                // loops. The operator attaches Checks or the Judge documents the exclusion marker.
                "judge pass rejected"
            };

            return seriousMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        }

        private static string Truncate(string? value, int max)
        {
            string normalized = String.IsNullOrWhiteSpace(value) ? "mission" : value.Trim();
            if (normalized.Length <= max) return normalized;
            return normalized.Substring(0, Math.Max(1, max - 3)).TrimEnd() + "...";
        }

        private static string AppendRescueCancellationNote(string note, List<Mission> cancelledRescues)
        {
            if (cancelledRescues.Count == 0)
                return note;

            return note + " Cancelled active autonomous rescue mission(s): " +
                String.Join(", ", cancelledRescues.Select(item => item.Id)) + ".";
        }

        private readonly struct RecoveryDecision
        {
            public bool DispatchRescue { get; }
            public string Reason { get; }

            private RecoveryDecision(bool dispatchRescue, string reason)
            {
                DispatchRescue = dispatchRescue;
                Reason = reason;
            }

            public static RecoveryDecision Rescue(string reason) => new RecoveryDecision(true, reason);
            public static RecoveryDecision Blocked(string reason) => new RecoveryDecision(false, reason);
        }

        /// <summary>
        /// Result of the repeated-identical-test-failure comparison: whether the rescue repeated its
        /// parent's exact test failure, and the matched tests when it did.
        /// </summary>
        private readonly struct RepeatedFailureCheck
        {
            public static readonly RepeatedFailureCheck None = new RepeatedFailureCheck(false, new List<string>());

            public RepeatedFailureCheck(bool isRepeated, IReadOnlyList<string> repeatedTests)
            {
                IsRepeated = isRepeated;
                RepeatedTests = repeatedTests;
            }

            public bool IsRepeated { get; }

            public IReadOnlyList<string> RepeatedTests { get; }
        }

        /// <summary>
        /// A stored failing-test set read back from a definition-of-done evaluation record.
        /// </summary>
        internal readonly struct StoredFailedTestSet
        {
            public StoredFailedTestSet(IReadOnlyList<string> names, bool overflow)
            {
                Names = names ?? new List<string>();
                Overflow = overflow;
            }

            public IReadOnlyList<string> Names { get; }

            public bool Overflow { get; }
        }
    }
}
