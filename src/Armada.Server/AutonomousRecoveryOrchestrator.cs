namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
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
    /// Armada-native policy service for failed mission incidents, runbook records, rescue dispatch,
    /// and bounded Mail nudges for live stalled work.
    /// </summary>
    public sealed class AutonomousRecoveryOrchestrator
    {
        private const string _Header = "[AutonomousRecoveryOrchestrator] ";
        private const string _RecoveryRunbookFileName = "system/mission-recovery.md";
        private const string _RescueMarker = "<!-- ARMADA:AUTO-RESCUE -->";
        private const string _NudgeMarker = "[ARMADA_AUTO_NUDGE]";

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
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _MissionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _SweepLock = new SemaphoreSlim(1, 1);

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
            : this(database, admiral, incidents, runbooks, settings, logging, null, null, null, null, null)
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
        {
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
            Dictionary<string, bool> terminalVoyageCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            int processed = 0;

            foreach (MissionSummary candidate in candidates
                .Where(item => item.LastUpdateUtc >= cutoff)
                .OrderBy(item => item.LastUpdateUtc))
            {
                if (processed >= 10) break;
                token.ThrowIfCancellationRequested();

                if (await IsTerminalVoyageAsync(candidate.VoyageId, terminalVoyageCache, token).ConfigureAwait(false))
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

                await ApplyFailurePolicyAsync(candidate.TenantId, candidate.Id, token).ConfigureAwait(false);
                processed++;
            }
        }

        private async Task<bool> IsTerminalVoyageAsync(string? voyageId, Dictionary<string, bool> cache, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(voyageId))
                return false;

            bool terminal;
            if (cache.TryGetValue(voyageId, out terminal))
                return terminal;

            Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            terminal = voyage != null
                && (voyage.Status == VoyageStatusEnum.Cancelled
                    || voyage.Status == VoyageStatusEnum.Complete
                    || voyage.Status == VoyageStatusEnum.Failed);
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

            foreach (Voyage voyage in candidates.OrderBy(item => item.LastUpdateUtc))
            {
                if (processed >= maxVoyages) break;
                token.ThrowIfCancellationRequested();

                List<MissionSummary> summaries = await _Database.Missions
                    .EnumerateMissionSummariesByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
                if (summaries.Count == 0) continue;

                if (summaries.Any(item => IsLandingDrainLiveMission(item.Status)))
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
        }

        private async Task DrainIdleVoyageAsync(Voyage voyage, List<MissionSummary> summaries, CancellationToken token)
        {
            await EnqueueJudgePassedWorkProducedAsync(voyage, summaries, token).ConfigureAwait(false);
            await RescueFailedLeafChainsAsync(summaries, token).ConfigureAwait(false);

            List<MissionSummary> refreshed = await _Database.Missions
                .EnumerateMissionSummariesByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            await TryCompleteIdleVoyageAsync(voyage, refreshed, token).ConfigureAwait(false);
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
                    string? diff = await TryLoadSafetyNetDiffAsync(mission, vessel, token).ConfigureAwait(false);
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

        private async Task TryCompleteIdleVoyageAsync(Voyage voyage, List<MissionSummary> summaries, CancellationToken token)
        {
            if (voyage.Status == VoyageStatusEnum.Complete ||
                voyage.Status == VoyageStatusEnum.Cancelled ||
                voyage.Status == VoyageStatusEnum.Failed)
            {
                return;
            }

            if (summaries.Any(item => IsLandingDrainLiveMission(item.Status)))
                return;

            bool allTerminal = summaries.All(item => IsVoyageDrainTerminalStatus(item.Status));
            if (!allTerminal) return;

            bool anyFailed = summaries.Any(item =>
                item.Status == MissionStatusEnum.Failed || item.Status == MissionStatusEnum.LandingFailed);

            voyage.Status = anyFailed ? VoyageStatusEnum.Failed : VoyageStatusEnum.Complete;
            voyage.CompletedUtc = DateTime.UtcNow;
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);

            await EmitLandingDrainEventAsync(
                "landing_drain.voyage_completed",
                "Landing-drain marked voyage " + voyage.Id + " " + voyage.Status,
                null,
                voyage.Id,
                token,
                voyage.TenantId,
                voyage.UserId).ConfigureAwait(false);

            if (_Admiral.OnVoyageComplete != null)
            {
                try
                {
                    await _Admiral.OnVoyageComplete.Invoke(voyage).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "OnVoyageComplete failed for voyage " + voyage.Id + ": " + ex.Message);
                }
            }
        }

        private async Task DetectStuckOpenVoyageAsync(Voyage voyage, List<MissionSummary> summaries, CancellationToken token)
        {
            if (voyage.Status != VoyageStatusEnum.Open && voyage.Status != VoyageStatusEnum.InProgress) return;
            if (summaries.Any(item => IsLandingDrainLiveMission(item.Status))) return;

            DateTime lastProgress = voyage.LastUpdateUtc;
            foreach (MissionSummary summary in summaries)
            {
                if (summary.LastUpdateUtc > lastProgress) lastProgress = summary.LastUpdateUtc;
            }

            double quietMinutes = (DateTime.UtcNow - lastProgress).TotalMinutes;
            if (quietMinutes < _Settings.AutonomousRecovery.StuckOpenVoyageMinutes) return;

            if (await HasOpenStuckVoyageIncidentAsync(voyage, token).ConfigureAwait(false)) return;

            AuthContext auth = BuildVoyageAuth(voyage);
            Incident incident = await _Incidents.CreateAsync(auth, new IncidentUpsertRequest
            {
                Title = "Stuck open voyage: " + Truncate(voyage.Title, 96),
                Summary = "Voyage " + voyage.Id + " has been " + voyage.Status + " with no live missions and no progress for " +
                    quietMinutes.ToString("F1") + " minutes.",
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
            EnumerationResult<Incident> page = await _Incidents.EnumerateAsync(auth, new IncidentQuery
            {
                VoyageId = voyage.Id,
                PageNumber = 1,
                PageSize = 25
            }, token).ConfigureAwait(false);

            return page.Objects.Any(item =>
                item.Status != IncidentStatusEnum.Closed &&
                item.Status != IncidentStatusEnum.RolledBack &&
                (item.Summary ?? String.Empty).Contains("no live missions", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> IsReviewerChainPassedAsync(List<MissionSummary> summaries, string rootMissionId, CancellationToken token)
        {
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
                }

                queue.AddRange(summaries.Where(item => String.Equals(item.DependsOnMissionId, current.Id, StringComparison.Ordinal)));
            }

            return true;
        }

        private async Task<string?> TryLoadSafetyNetDiffAsync(Mission mission, Vessel vessel, CancellationToken token)
        {
            if (_Git == null) return null;

            string? repoPath = vessel.WorkingDirectory ?? vessel.LocalPath;
            if (String.IsNullOrWhiteSpace(repoPath)) return null;

            try
            {
                return await _Git.DiffAsync(repoPath, vessel.DefaultBranch ?? "main", token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "landing-drain diff unavailable for mission " + mission.Id + ": " + ex.Message);
                return null;
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

        private static bool IsLandingDrainLiveMission(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.InProgress ||
                status == MissionStatusEnum.Assigned ||
                status == MissionStatusEnum.Testing ||
                status == MissionStatusEnum.Review ||
                status == MissionStatusEnum.WaitingForInput;
        }

        private static bool IsVoyageDrainTerminalStatus(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete ||
                status == MissionStatusEnum.Failed ||
                status == MissionStatusEnum.Cancelled ||
                status == MissionStatusEnum.LandingFailed ||
                status == MissionStatusEnum.PullRequestOpen;
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
                if (line.Contains("[ARMADA:VERDICT] PASS", StringComparison.OrdinalIgnoreCase))
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

        private async Task ApplyFailurePolicyAsync(string? tenantId, string missionId, CancellationToken token)
        {
            SemaphoreSlim missionLock = _MissionLocks.GetOrAdd(missionId, _ => new SemaphoreSlim(1, 1));
            await missionLock.WaitAsync(token).ConfigureAwait(false);

            try
            {
                Mission? latest = await ReadMissionAsync(tenantId, missionId, token).ConfigureAwait(false);
                if (latest == null || !IsRecoverableTerminalStatus(latest.Status))
                    return;

                if (await SuppressCancelledVoyageRecoveryAsync(latest, token).ConfigureAwait(false))
                    return;

                if (await IsAlreadyHandledAsync(latest, token).ConfigureAwait(false))
                    return;

                RecoveryDecision decision = Classify(latest);
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
                    return;
                }

                Incident incident = await EnsureIncidentAsync(auth, latest, decision, token).ConfigureAwait(false);
                RunbookExecution? execution = await ExecuteRecoveryRunbookAsync(auth, latest, incident, decision, token).ConfigureAwait(false);

                if (!decision.DispatchRescue)
                {
                    await MarkPolicyBlockedAsync(latest, token).ConfigureAwait(false);
                    await EmitEventAsync("autonomous_recovery.blocked",
                        "Autonomous recovery opened incident " + incident.Id + " but did not dispatch a rescue for mission " + latest.Id + ": " + decision.Reason,
                        latest, incident.Id, token).ConfigureAwait(false);
                    return;
                }

                Mission rescue = await DispatchRescueMissionAsync(latest, incident, token).ConfigureAwait(false);
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
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to apply recovery policy for mission " + missionId + ": " + ex.Message);
            }
            finally
            {
                missionLock.Release();
            }
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

        private RecoveryDecision Classify(Mission mission)
        {
            string reason = mission.FailureReason ?? String.Empty;

            if (!_Settings.AutonomousRecovery.DispatchRescueMissions)
                return RecoveryDecision.Blocked("autonomous rescue dispatch is disabled");
            if (String.IsNullOrWhiteSpace(mission.VesselId))
                return RecoveryDecision.Blocked("mission has no vessel");
            if (mission.RecoveryAttempts >= _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts)
                return RecoveryDecision.Blocked("mission recovery budget is exhausted");
            if (mission.Status == MissionStatusEnum.LandingFailed)
                return RecoveryDecision.Blocked("landing failures remain owned by landing and merge recovery workflows");
            if (IsAutoRescueMission(mission))
                return RecoveryDecision.Blocked("failed mission is already an autonomous rescue");
            if (HasSeriousFailureReason(reason))
                return RecoveryDecision.Blocked("failure requires human review: " + reason);

            return RecoveryDecision.Rescue("recoverable mission failure");
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
            EnumerationResult<Incident> existing = await _Incidents.EnumerateAsync(auth, new IncidentQuery
            {
                MissionId = mission.Id,
                PageNumber = 1,
                PageSize = 25
            }, token).ConfigureAwait(false);

            foreach (Incident incident in existing.Objects.Where(item =>
                item.Status != IncidentStatusEnum.Closed && item.Status != IncidentStatusEnum.RolledBack))
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
            EnumerationResult<Incident> existing = await _Incidents.EnumerateAsync(auth, new IncidentQuery
            {
                MissionId = mission.Id,
                PageNumber = 1,
                PageSize = 25
            }, token).ConfigureAwait(false);

            Incident? active = existing.Objects
                .FirstOrDefault(item => item.Status != IncidentStatusEnum.Closed && item.Status != IncidentStatusEnum.RolledBack);
            string recoveryNote = decision.DispatchRescue
                ? "Autonomous policy classified this as recoverable and will dispatch one rescue mission."
                : "Autonomous policy stopped before rescue dispatch: " + decision.Reason + ".";

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
                        ["decision"] = decision.DispatchRescue ? "dispatch_rescue" : "block"
                    },
                    Notes = "Decision: " + (decision.DispatchRescue ? "dispatch rescue" : "block") + ". Reason: " + decision.Reason
                }, token).ConfigureAwait(false);

                await _Runbooks.UpdateExecutionAsync(auth, execution.Id, new RunbookExecutionUpdateRequest
                {
                    Status = RunbookExecutionStatusEnum.Completed,
                    CompletedStepIds = runbook.Steps.Select(step => step.Id).ToList(),
                    Notes = "Autonomous recovery policy completed. Decision: " +
                        (decision.DispatchRescue ? "dispatch rescue." : "block. " + decision.Reason)
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
                return runbook;

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

        private async Task<Mission> DispatchRescueMissionAsync(Mission failedMission, Incident incident, CancellationToken token)
        {
            int attemptNumber = failedMission.RecoveryAttempts + 1;
            string rescuePersona = ResolveRescuePersona(failedMission.Persona);
            Mission rescue = new Mission
            {
                TenantId = failedMission.TenantId,
                UserId = failedMission.UserId,
                VesselId = failedMission.VesselId,
                ParentMissionId = failedMission.Id,
                Persona = rescuePersona,
                PreferredModel = failedMission.PreferredModel,
                Priority = Math.Max(0, failedMission.Priority - 10),
                Title = "Rescue " + attemptNumber + ": " + Truncate(failedMission.Title, 100),
                Description = BuildRescueDescription(failedMission, incident, attemptNumber),
                // Carry the recovery budget forward onto the rescue itself. A rescue stage that
                // fails again is picked up by the sweep with RecoveryAttempts already at the
                // attempt count, so Classify blocks further rescues once the budget is spent and
                // the bounded revise->retest->rejudge loop terminates instead of recursing.
                RecoveryAttempts = attemptNumber
            };

            // A reviewer rejection (for example a Judge NEEDS_REVISION) is recovered by a Worker
            // revision. That revision must be re-verified before it lands rather than landing with
            // no review. Chain a re-Judge (and re-TestEngineer where the vessel pipeline defines
            // one) onto the revision so the revised branch is re-reviewed first.
            bool chainReReview = IsReviewerPersona(failedMission.Persona)
                && String.Equals(rescuePersona, "Worker", StringComparison.Ordinal);

            if (!chainReReview)
                return await _Admiral.DispatchMissionAsync(rescue, token).ConfigureAwait(false);

            return await DispatchRescueReviewLoopAsync(failedMission, rescue, attemptNumber, token).ConfigureAwait(false);
        }

        // Dispatch the Worker revision rescue as the root of a dedicated rescue voyage, then chain
        // the verification stages onto it via DependsOnMissionId. The standard pipeline handoff
        // (MissionService) stamps each downstream stage with the revision branch and assigns it
        // once the prior stage produces work, so the re-Judge reviews exactly the branch the
        // revision lands on. Branch choice: the revision runs on a fresh captain branch (the
        // existing root-mission behavior; revising in place on the original branch would require
        // checkout-existing-branch support in dock provisioning, which is out of scope here), and
        // the re-Judge inherits that branch through handoff -- so the re-judge always targets the
        // branch the revision actually lands on.
        private async Task<Mission> DispatchRescueReviewLoopAsync(Mission failedMission, Mission workerRescue, int attemptNumber, CancellationToken token)
        {
            Voyage rescueVoyage = await _Database.Voyages.CreateAsync(new Voyage(
                "Rescue " + attemptNumber + ": " + Truncate(failedMission.Title, 80),
                "Autonomous revise/retest/rejudge loop for failed mission " + failedMission.Id + ".")
            {
                TenantId = failedMission.TenantId,
                UserId = failedMission.UserId,
                Status = VoyageStatusEnum.InProgress
            }, token).ConfigureAwait(false);

            workerRescue.VoyageId = rescueVoyage.Id;
            Mission dispatchedWorker = await _Admiral.DispatchMissionAsync(workerRescue, token).ConfigureAwait(false);

            string upstreamMissionId = dispatchedWorker.Id;

            if (await VesselPipelineHasTestEngineerAsync(failedMission, token).ConfigureAwait(false))
            {
                Mission testStage = await _Database.Missions.CreateAsync(
                    BuildChainedRescueStage(failedMission, rescueVoyage.Id, upstreamMissionId, "TestEngineer", attemptNumber),
                    token).ConfigureAwait(false);
                upstreamMissionId = testStage.Id;
            }

            await _Database.Missions.CreateAsync(
                BuildChainedRescueStage(failedMission, rescueVoyage.Id, upstreamMissionId, "Judge", attemptNumber),
                token).ConfigureAwait(false);

            return dispatchedWorker;
        }

        // Build a downstream verification stage (TestEngineer / Judge) for the rescue loop. The
        // stage carries the auto-rescue marker (so it is recognised as rescue work) and the
        // recovery budget, but no ParentMissionId -- it is a pipeline dependent of the Worker
        // rescue (via DependsOnMissionId), not a direct rescue of the original failure, so it does
        // not inflate the original failure's rescue accounting. The persona preamble and prior-
        // stage diff are injected by the standard MissionService handoff when the upstream stage
        // completes.
        private Mission BuildChainedRescueStage(Mission failedMission, string voyageId, string dependsOnMissionId, string persona, int attemptNumber)
        {
            return new Mission
            {
                TenantId = failedMission.TenantId,
                UserId = failedMission.UserId,
                VesselId = failedMission.VesselId,
                VoyageId = voyageId,
                DependsOnMissionId = dependsOnMissionId,
                Persona = persona,
                PreferredModel = failedMission.PreferredModel,
                Priority = Math.Max(0, failedMission.Priority - 10),
                Status = MissionStatusEnum.Pending,
                RecoveryAttempts = attemptNumber,
                Title = "Rescue " + attemptNumber + " " + persona + ": " + Truncate(failedMission.Title, 90),
                Description = _RescueMarker + Environment.NewLine +
                    "Re-verification stage for the autonomous revision rescue of failed mission " + failedMission.Id + "." + Environment.NewLine +
                    "Confirm the revised branch resolves the original reviewer feedback before it lands."
            };
        }

        private async Task<bool> VesselPipelineHasTestEngineerAsync(Mission failedMission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(failedMission.VesselId)) return false;

            Vessel? vessel = await _Database.Vessels.ReadAsync(failedMission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) return false;

            Pipeline? pipeline = await _Admiral.ResolvePipelineAsync(null, vessel, token).ConfigureAwait(false);
            if (pipeline == null) return false;

            return pipeline.Stages.Any(stage =>
                String.Equals(stage.PersonaName, "TestEngineer", StringComparison.OrdinalIgnoreCase));
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
        // match. Enumerating every fully-hydrated vessel/global mission here reintroduced the
        // MissionFromReader allocation leak (dotMemory: 2.37 GB of strings per sweep window).
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
            if (!_Settings.AutonomousRecovery.SendStallMailNudges) return;

            double thresholdMinutes = Math.Max(1.0, _Settings.StallThresholdMinutes * _Settings.AutonomousRecovery.StallMailNudgeThresholdRatio);
            List<Captain> working = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);

            foreach (Captain captain in working)
            {
                if (!captain.LastHeartbeatUtc.HasValue || String.IsNullOrWhiteSpace(captain.CurrentMissionId))
                    continue;

                TimeSpan quietFor = DateTime.UtcNow - captain.LastHeartbeatUtc.Value;
                if (quietFor.TotalMinutes < thresholdMinutes)
                    continue;

                Mission? mission = await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                if (mission == null || !IsLiveMissionStatus(mission.Status))
                    continue;

                if (await HasRecentAutoNudgeAsync(captain, token).ConfigureAwait(false))
                    continue;

                Signal signal = new Signal(SignalTypeEnum.Mail,
                    _NudgeMarker + " Armada has not seen progress for " + quietFor.TotalMinutes.ToString("F1") +
                    " minutes on mission " + mission.Id + ". Please report status, continue the mission, or fail with a specific blocker.");
                signal.TenantId = captain.TenantId ?? mission.TenantId;
                signal.UserId = captain.UserId ?? mission.UserId;
                signal.ToCaptainId = captain.Id;
                await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

                await EmitEventAsync("autonomous_recovery.mail_nudge_sent",
                    "Autonomous Mail nudge sent to captain " + captain.Id + " for mission " + mission.Id,
                    mission, null, token).ConfigureAwait(false);
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
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                EntityType = incidentId != null ? "incident" : "mission",
                EntityId = incidentId ?? mission.Id,
                MissionId = mission.Id,
                VesselId = mission.VesselId,
                VoyageId = mission.VoyageId
            };

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

        private static string BuildRescueDescription(Mission failedMission, Incident incident, int attemptNumber)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(_RescueMarker);
            sb.AppendLine("Autonomous rescue attempt " + attemptNumber + " for failed mission " + failedMission.Id + ".");
            sb.AppendLine();
            sb.AppendLine("Incident: " + incident.Id);
            sb.AppendLine("Original title: " + failedMission.Title);
            sb.AppendLine("Failure status: " + failedMission.Status);
            sb.AppendLine("Failure reason: " + (failedMission.FailureReason ?? "not recorded"));
            if (!String.IsNullOrWhiteSpace(failedMission.BranchName))
                sb.AppendLine("Original branch: " + failedMission.BranchName);
            if (!String.IsNullOrWhiteSpace(failedMission.ReviewComment))
            {
                sb.AppendLine();
                sb.AppendLine("Reviewer feedback to address:");
                sb.AppendLine(failedMission.ReviewComment.Trim());
            }
            sb.AppendLine();
            sb.AppendLine("Objective:");
            sb.AppendLine("Recover the original mission without repeating the failure. Inspect the original failure, make the smallest corrective change, run the vessel's workflow profile checks when available, and leave explicit evidence in Armada records.");
            sb.AppendLine();
            sb.AppendLine("Original mission description:");
            sb.AppendLine(failedMission.Description ?? "(no description recorded)");
            return sb.ToString();
        }

        private static readonly string[] _ReviewerPersonas =
        {
            "judge",
            "testengineer",
            "usabilityengineer",
            "diagnosticprotocolreviewer",
            "tenantsecurityreviewer",
            "migrationdatareviewer",
            "performancememoryreviewer",
            "portingreferenceanalyst",
            "frontendworkflowreviewer"
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
            return (mission.Description ?? String.Empty).Contains(_RescueMarker, StringComparison.Ordinal)
                || (mission.Title ?? String.Empty).StartsWith("Rescue:", StringComparison.OrdinalIgnoreCase);
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
                "vessel deleted"
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
    }
}
