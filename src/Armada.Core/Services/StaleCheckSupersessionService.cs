namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Json;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Replaces a green Check that measured older work with a fresh Pending record for the work a
    /// live voyage is now reviewing.
    /// </summary>
    /// <remarks>
    /// A voyage-armed Check runs once the voyage's work is final (<see cref="SelectFinalWorkUnderReview"/>),
    /// is stamped with that commit when it executes, and is never re-stamped. A stage can still add
    /// a commit afterwards, for example a fix stage added to the voyage, and then the record vouches
    /// for older work. A Failed record for an older commit is treated the same way as a stale green:
    /// the later commit may be the fix, so the red is superseded and a fresh record decides at the
    /// tip. The Judge gate holds a PASS when it sees such a record
    /// (<see cref="CheckRunGateRules.IsStale"/>); this service is what lets the hold end, by giving
    /// the executor a record it can run against the current tip.
    /// <para>
    /// The stale record is Canceled, not deleted or rewritten: it stays as the honest history of
    /// what it measured, its summary names the record that supersedes it, and a Canceled record
    /// is ignored by every gate. That is the same rule an operator follows by hand when a flake is
    /// retried to green.
    /// </para>
    /// <para>
    /// A voyage that splits into several lanes (<see cref="VoyageLanes"/>) is decided lane by lane.
    /// Each record is compared with the work of the one lane it measures, a lane whose work is final
    /// gets its own records stamped at its own commit, and a record that measures several lanes at
    /// once (armed at dispatch before the split, or run at a planner commit above it) is retired once
    /// every lane it spans has a verdict of its own.
    /// </para>
    /// </remarks>
    public class StaleCheckSupersessionService
    {
        #region Private-Members

        private readonly string _Header = "[StaleCheckSupersession] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule? _Logging;
        private readonly JsonSerializerOptions _JsonOptions = JsonDefaults.Web;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _VoyageLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module, optional.</param>
        public StaleCheckSupersessionService(DatabaseDriver database, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The mission whose commit a voyage-armed Check should measure: an eligible dependency
        /// descendant before its ancestors, then the most recently updated unrelated stage.
        /// Null while no stage has produced measurable work.
        /// </summary>
        /// <remarks>
        /// This is the one definition of "the work under review" for voyage-armed Checks. The
        /// executor stamps from it and this service compares against it, so the two cannot disagree
        /// about which commit a record ought to carry.
        /// </remarks>
        /// <param name="missions">The voyage's missions, in any order.</param>
        /// <returns>The mission carrying the work under review, or null.</returns>
        public static Mission? SelectWorkUnderReview(IEnumerable<Mission>? missions)
        {
            if (missions == null) return null;
            List<Mission> list = missions.Where(mission => mission != null).ToList();
            List<Mission> candidates = list
                .Where(mission => !String.IsNullOrWhiteSpace(mission.BranchName))
                .Where(mission => !String.IsNullOrWhiteSpace(mission.CommitHash))
                .Where(mission => mission.Status == MissionStatusEnum.WorkProduced
                    || mission.Status == MissionStatusEnum.PullRequestOpen
                    || mission.Status == MissionStatusEnum.Testing
                    || mission.Status == MissionStatusEnum.Review
                    || mission.Status == MissionStatusEnum.Complete)
                .ToList();

            // Completing a landed chain updates predecessors last. Those timestamps record
            // bookkeeping, not a new branch tip. Follow dependencies through every stage,
            // including stages without a commit, before applying the legacy time fallback.
            HashSet<string> ancestors = VoyageLanes.StrictAncestorsOf(list, candidates);

            return candidates.Where(mission => !ancestors.Contains(mission.Id))
                .OrderByDescending(mission => mission.LastUpdateUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Whether a stage can still add a commit on top of the work under review.
        /// </summary>
        /// <remarks>
        /// A stage that has not finished (Pending, Assigned or InProgress) may still commit, unless
        /// it is a review or record-keeping stage whose result never lands in the branch: the Judge,
        /// a persona whose normal completion is an empty diff
        /// (<see cref="PersonaCatalog.IsNoOpCompletionExempt"/>), or a mission running in a read-only
        /// mode. Testing and Review count as unfinished while the mission has no completion time: a
        /// running captain reports them as progress and keeps working, while the completion path
        /// that moves finished work to Review stamps the completion time. Every other status means
        /// the stage is done or ended, so its commit, if any, is final.
        /// </remarks>
        /// <param name="mission">A mission of the voyage.</param>
        /// <returns>True when the mission may still commit.</returns>
        public static bool MayStillCommit(Mission? mission)
        {
            if (mission == null) return false;
            bool running = mission.Status == MissionStatusEnum.Pending
                || mission.Status == MissionStatusEnum.Assigned
                || mission.Status == MissionStatusEnum.InProgress
                || ((mission.Status == MissionStatusEnum.Testing || mission.Status == MissionStatusEnum.Review)
                    && mission.CompletedUtc == null);
            if (!running) return false;
            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge)) return false;
            if (PersonaCatalog.IsNoOpCompletionExempt(mission.Persona)) return false;
            if (mission.IsReadOnlyMode) return false;
            return true;
        }

        /// <summary>
        /// The first stage of the voyage that can still commit before the work under review is judged,
        /// or null when there is none.
        /// </summary>
        /// <remarks>
        /// A stage counts when it may still commit (<see cref="MayStillCommit"/>) and can start before
        /// the Judge that reviews the current work ends. A stage whose dependency chain reaches a Judge
        /// that still holds its dependents back (one that is held for operator review, or whose status
        /// does not release a dependent) cannot be assigned until that Judge ends, so it cannot commit
        /// before the verdict. Counting it would hold back the Checks that verdict waits on, and neither
        /// would ever move. A stage beside the Judge, or after a Judge that has released its dependents,
        /// still counts.
        /// </remarks>
        /// <param name="missions">The voyage's missions, in any order.</param>
        /// <returns>The stage that can still commit, or null.</returns>
        public static Mission? FindStageThatMayStillCommit(IEnumerable<Mission>? missions)
        {
            if (missions == null) return null;
            List<Mission> list = missions.Where(mission => mission != null).ToList();
            Dictionary<string, Mission> byId = list
                .Where(mission => !String.IsNullOrWhiteSpace(mission.Id))
                .GroupBy(mission => mission.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return list.FirstOrDefault(mission => MayStillCommit(mission) && !WaitsOnUnreleasedJudge(mission, byId));
        }

        /// <summary>
        /// Whether a voyage-armed Check would measure final work if it ran now: some stage has
        /// committed work (<see cref="SelectWorkUnderReview"/>) and no stage can still commit on top
        /// of it before the work is judged (<see cref="FindStageThatMayStillCommit"/>).
        /// </summary>
        /// <remarks>
        /// Only the run at the last commit decides the Judge gate; a run while a later stage can still
        /// commit is superseded as soon as that stage commits, so it spends the host command slot for
        /// no verdict. The Judge itself never counts as a committing stage, and neither does a stage
        /// that cannot start until the Judge ends, so the Check becomes runnable no later than when the
        /// Judge and the stages behind it are all that is left, and the Judge's hold on a missing green
        /// still ends.
        /// </remarks>
        /// <param name="missions">The voyage's missions, in any order.</param>
        /// <returns>The mission carrying final work, or null while there is none.</returns>
        public static Mission? SelectFinalWorkUnderReview(IEnumerable<Mission>? missions)
        {
            if (missions == null) return null;
            List<Mission> list = missions.Where(mission => mission != null).ToList();
            if (FindStageThatMayStillCommit(list) != null) return null;
            return SelectWorkUnderReview(list);
        }

        /// <summary>
        /// The mission whose commit a particular voyage-armed record should measure.
        /// </summary>
        /// <remarks>
        /// On a voyage that has not split this is <see cref="SelectWorkUnderReview"/>. On a voyage
        /// that has split it is the work of the one lane the record measures
        /// (<see cref="VoyageLanes.SingleLaneOfRecord"/>), and null for a record that measures
        /// several lanes or a lane that has committed nothing of its own yet.
        /// </remarks>
        /// <param name="run">The record.</param>
        /// <param name="missions">The voyage's missions, in any order.</param>
        /// <returns>The mission carrying the work the record measures, or null.</returns>
        public static Mission? SelectWorkUnderReviewFor(CheckRun? run, IEnumerable<Mission>? missions)
        {
            List<Mission> list = missions?.Where(mission => mission != null).ToList() ?? new List<Mission>();
            IReadOnlyList<VoyageLane> lanes = VoyageLanes.Resolve(list);
            if (!VoyageLanes.IsFanOut(lanes)) return SelectWorkUnderReview(list);
            VoyageLane? lane = VoyageLanes.SingleLaneOfRecord(lanes, run);
            return lane == null ? null : SelectLaneWork(lane);
        }

        /// <summary>
        /// The final work a particular voyage-armed record would measure if it ran now, or null while
        /// the work it measures can still change.
        /// </summary>
        /// <remarks>
        /// On a voyage that has not split this is <see cref="SelectFinalWorkUnderReview"/>. On a voyage
        /// that has split only the stages of the record's own lane can hold it back, so a lane whose
        /// stages are done runs its Checks while another lane is still working.
        /// </remarks>
        /// <param name="run">The record.</param>
        /// <param name="missions">The voyage's missions, in any order.</param>
        /// <returns>The mission carrying final work, or null.</returns>
        public static Mission? SelectFinalWorkUnderReviewFor(CheckRun? run, IEnumerable<Mission>? missions)
        {
            List<Mission> list = missions?.Where(mission => mission != null).ToList() ?? new List<Mission>();
            IReadOnlyList<VoyageLane> lanes = VoyageLanes.Resolve(list);
            if (!VoyageLanes.IsFanOut(lanes)) return SelectFinalWorkUnderReview(list);
            VoyageLane? lane = VoyageLanes.SingleLaneOfRecord(lanes, run);
            return lane == null ? null : SelectFinalLaneWork(lane);
        }

        /// <summary>
        /// Why a particular voyage-armed record has no final work to measure yet, for the record's view.
        /// </summary>
        /// <remarks>
        /// Uses the same selection as <see cref="SelectFinalWorkUnderReviewFor"/>: it names the stage that
        /// can still commit before the work is judged, or says that no stage has committed the work the
        /// record measures.
        /// </remarks>
        /// <param name="run">The record.</param>
        /// <param name="missions">The voyage's missions, in any order.</param>
        /// <returns>The reason.</returns>
        public static string DescribeWorkWaitFor(CheckRun? run, IEnumerable<Mission>? missions)
        {
            List<Mission> list = missions?.Where(mission => mission != null).ToList() ?? new List<Mission>();
            IReadOnlyList<VoyageLane> lanes = VoyageLanes.Resolve(list);
            IEnumerable<Mission> scope = list;
            if (VoyageLanes.IsFanOut(lanes))
            {
                VoyageLane? lane = VoyageLanes.SingleLaneOfRecord(lanes, run);
                if (lane == null)
                    return "waiting for the voyage's work: the voyage has split and this check measures no single lane's own work";
                scope = lane.Missions;
            }

            Mission? blocker = FindStageThatMayStillCommit(scope);
            if (blocker != null)
            {
                return "waiting for mission " + blocker.Id + " (" + (blocker.Persona ?? "stage") + ", " + blocker.Status
                    + "), which can still commit on top of the work this check measures; the check starts once no such stage remains";
            }

            return "waiting for a stage of the voyage to commit the work this check measures";
        }

        /// <summary>
        /// Decide whether a fresh record is needed once a stale green has been ruled out. None is
        /// needed when a sibling of the same type already covers the work: one still queued or
        /// running (it is stamped at execution and will measure the current work), or one that
        /// Passed at the current commit.
        /// </summary>
        /// <param name="stale">The record being superseded.</param>
        /// <param name="siblings">Every other record attached to the same voyage.</param>
        /// <param name="workCommit">The commit under review.</param>
        /// <returns>True when a replacement record must be created.</returns>
        public static bool NeedsReplacement(CheckRun stale, IEnumerable<CheckRun>? siblings, string? workCommit)
        {
            if (stale == null) throw new ArgumentNullException(nameof(stale));
            if (siblings == null) return true;

            foreach (CheckRun sibling in siblings)
            {
                if (sibling == null || String.Equals(sibling.Id, stale.Id, StringComparison.Ordinal)) continue;
                if (sibling.Type != stale.Type) continue;
                if (sibling.Status == CheckRunStatusEnum.Pending || sibling.Status == CheckRunStatusEnum.Running) return false;
                if (sibling.Status == CheckRunStatusEnum.Passed && CheckRunGateRules.SameCommit(sibling.CommitHash, workCommit)) return false;
            }

            return true;
        }

        /// <summary>
        /// Sweep every live voyage for green Checks that measured older work and supersede each one.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of records superseded.</returns>
        public async Task<int> SupersedeAsync(CancellationToken token = default)
        {
            int superseded = 0;

            // A dispatched voyage runs as Open; only a rescue voyage is created InProgress. Both are
            // live, and the objective scheduler counts both as active. Reading one of the two would
            // silently skip every ordinary voyage while appearing to work on rescues.
            List<Voyage> live = new List<Voyage>();
            live.AddRange(await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Open, token).ConfigureAwait(false));
            live.AddRange(await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false));

            foreach (Voyage voyage in live)
            {
                if (voyage == null) continue;
                superseded += await SupersedeForVoyageAsync(voyage, token).ConfigureAwait(false);
            }

            return superseded;
        }

        /// <summary>
        /// True when a voyage is still running, so its Checks are subject to supersession.
        /// </summary>
        /// <param name="status">The voyage status.</param>
        /// <returns>True for Open and InProgress.</returns>
        public static bool IsLiveVoyageStatus(VoyageStatusEnum status)
        {
            return status == VoyageStatusEnum.Open || status == VoyageStatusEnum.InProgress;
        }

        /// <summary>
        /// Supersede the stale greens attached to one voyage.
        /// </summary>
        /// <param name="voyage">A voyage that is still live (Open or InProgress).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of records superseded.</returns>
        public async Task<int> SupersedeForVoyageAsync(Voyage voyage, CancellationToken token = default)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            if (!IsLiveVoyageStatus(voyage.Status)) return 0;
            Voyage? stored = await _Database.Voyages.ReadAsync(voyage.Id, token).ConfigureAwait(false);
            if (stored == null || !IsLiveVoyageStatus(stored.Status)) return 0;
            voyage = stored;

            List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            IReadOnlyList<VoyageLane> lanes = VoyageLanes.Resolve(missions);
            if (VoyageLanes.IsFanOut(lanes)) return await SupersedeForLanesAsync(voyage, lanes, token).ConfigureAwait(false);

            Mission? work = SelectWorkUnderReview(missions);
            if (work == null) return 0;

            EnumerationResult<CheckRun> attached = await _Database.CheckRuns
                .EnumerateAsync(new CheckRunQuery { TenantId = voyage.TenantId, VoyageId = voyage.Id, PageNumber = 1, PageSize = 200 }, token)
                .ConfigureAwait(false);
            List<CheckRun> records = attached.Objects.Where(run => run != null).ToList();

            int superseded = 0;
            foreach (CheckRun stale in records.Where(run => CheckRunGateRules.IsStale(run, work.CommitHash)).ToList())
            {
                CheckRunStatusEnum priorStatus = stale.Status;
                CheckRun? replacement = null;
                if (NeedsReplacement(stale, records, work.CommitHash))
                {
                    replacement = new CheckRun
                    {
                        TenantId = stale.TenantId,
                        UserId = stale.UserId,
                        VesselId = stale.VesselId,
                        VoyageId = stale.VoyageId,
                        WorkflowProfileId = stale.WorkflowProfileId,
                        Type = stale.Type,
                        Source = stale.Source,
                        Status = CheckRunStatusEnum.Pending,
                        Label = stale.Type.ToString() + " (re-armed: work moved to " + Abbreviate(work.CommitHash) + ")"
                    };
                    replacement = await _Database.CheckRuns.CreateAsync(replacement, token).ConfigureAwait(false);
                    records.Add(replacement);
                }

                stale.Status = CheckRunStatusEnum.Canceled;
                stale.Summary = "Superseded"
                    + (replacement == null ? String.Empty : " by " + replacement.Id)
                    + ": this record " + (priorStatus == CheckRunStatusEnum.Failed ? "failed" : "passed") + " at " + Abbreviate(stale.CommitHash)
                    + " but the work under review moved to " + Abbreviate(work.CommitHash)
                    + " (mission " + work.Id + "). A verdict for older work does not decide newer work.";
                stale.LastUpdateUtc = DateTime.UtcNow;
                await _Database.CheckRuns.UpdateAsync(stale, token).ConfigureAwait(false);

                await WriteEventAsync(stale, replacement, work, priorStatus, token).ConfigureAwait(false);
                _Logging?.Info(_Header + "superseded check " + stale.Id + " (passed at " + Abbreviate(stale.CommitHash)
                    + ") on voyage " + voyage.Id + "; work under review is " + Abbreviate(work.CommitHash)
                    + (replacement == null ? "; a sibling already covers it" : "; re-armed as " + replacement.Id));
                superseded++;
            }

            return superseded;
        }

        #endregion

        #region Private-Methods

        private static Mission? SelectLaneWork(VoyageLane lane)
        {
            // Work above the split is not a lane's own: a record stamped there would measure every
            // lane at once and decide none of them.
            Mission? work = SelectWorkUnderReview(lane.Missions);
            return work != null && lane.Owns(work.Id) ? work : null;
        }

        private static Mission? SelectFinalLaneWork(VoyageLane lane)
        {
            if (FindStageThatMayStillCommit(lane.Missions) != null) return null;
            return SelectLaneWork(lane);
        }

        private static bool WaitsOnUnreleasedJudge(Mission mission, Dictionary<string, Mission> byId)
        {
            // A dependency outside the list ends the walk: the stage then counts, which keeps the Check waiting.
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (!String.IsNullOrWhiteSpace(mission.Id)) seen.Add(mission.Id);
            string? parentId = mission.DependsOnMissionId;
            while (!String.IsNullOrWhiteSpace(parentId)
                && seen.Add(parentId)
                && byId.TryGetValue(parentId, out Mission? parent))
            {
                if (PersonaCatalog.Matches(parent.Persona, PersonaCatalog.Judge)
                    && (parent.HeldForOperatorReview || !MissionService.IsDependencySatisfyingStatus(parent.Status)))
                    return true;
                parentId = parent.DependsOnMissionId;
            }

            return false;
        }

        private static bool IsVoyageGateType(CheckRun run)
        {
            return String.IsNullOrWhiteSpace(run.MissionId)
                && String.IsNullOrWhiteSpace(run.DeploymentId)
                && run.Type != CheckRunTypeEnum.Deploy
                && run.Type != CheckRunTypeEnum.Rollback
                && run.Type != CheckRunTypeEnum.RollbackVerification;
        }

        private static bool LaneCovers(
            IReadOnlyList<VoyageLane> lanes, VoyageLane lane, IEnumerable<CheckRun> records, CheckRunTypeEnum type, string? workCommit, string? excludeId)
        {
            foreach (CheckRun run in records)
            {
                if (run == null || run.Type != type) continue;
                if (excludeId != null && String.Equals(run.Id, excludeId, StringComparison.Ordinal)) continue;
                if (!ReferenceEquals(VoyageLanes.SingleLaneOfRecord(lanes, run), lane)) continue;
                if (run.Status == CheckRunStatusEnum.Pending || run.Status == CheckRunStatusEnum.Running) return true;
                // A red at the lane's commit is a verdict too: arming another record would bury it.
                if ((run.Status == CheckRunStatusEnum.Passed || run.Status == CheckRunStatusEnum.Failed)
                    && CheckRunGateRules.SameCommit(run.CommitHash, workCommit)) return true;
            }

            return false;
        }

        private async Task<CheckRun> ArmLaneRecordAsync(Voyage voyage, CheckRun template, Mission work, string label, CancellationToken token)
        {
            CheckRun armed = new CheckRun
            {
                TenantId = template.TenantId ?? voyage.TenantId,
                UserId = template.UserId,
                VesselId = template.VesselId ?? work.VesselId,
                MissionId = work.Id,
                VoyageId = voyage.Id,
                WorkflowProfileId = template.WorkflowProfileId,
                Type = template.Type,
                Source = template.Source,
                Status = CheckRunStatusEnum.Pending,
                Label = label,
                BranchName = work.BranchName,
                CommitHash = work.CommitHash
            };
            return await _Database.CheckRuns.CreateAsync(armed, token).ConfigureAwait(false);
        }

        private async Task<int> SupersedeForLanesAsync(Voyage voyage, IReadOnlyList<VoyageLane> lanes, CancellationToken token)
        {
            // The Judge gate and the fleet sweep both call this; one at a time per voyage, so two
            // callers never arm the same lane twice.
            SemaphoreSlim voyageLock = _VoyageLocks.GetOrAdd(voyage.Id, _ => new SemaphoreSlim(1, 1));
            await voyageLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Voyage? stored = await _Database.Voyages.ReadAsync(voyage.Id, token).ConfigureAwait(false);
                if (stored == null || !IsLiveVoyageStatus(stored.Status)) return 0;
                voyage = stored;
                EnumerationResult<CheckRun> attached = await _Database.CheckRuns
                    .EnumerateAsync(new CheckRunQuery { TenantId = voyage.TenantId, VoyageId = voyage.Id, PageNumber = 1, PageSize = 200 }, token)
                    .ConfigureAwait(false);
                List<CheckRun> records = attached.Objects.Where(run => run != null).ToList();
                int changed = 0;

                // A record of one lane that measured older work of that lane.
                foreach (CheckRun stale in records.ToList())
                {
                    VoyageLane? lane = VoyageLanes.SingleLaneOfRecord(lanes, stale);
                    if (lane == null) continue;
                    Mission? work = SelectLaneWork(lane);
                    if (work == null || !CheckRunGateRules.IsStale(stale, work.CommitHash)) continue;

                    CheckRunStatusEnum priorStatus = stale.Status;
                    CheckRun? replacement = null;
                    if (SelectFinalLaneWork(lane) != null && !LaneCovers(lanes, lane, records, stale.Type, work.CommitHash, stale.Id))
                    {
                        replacement = await ArmLaneRecordAsync(voyage, stale, work,
                            stale.Type.ToString() + " (re-armed: lane work moved to " + Abbreviate(work.CommitHash) + ")", token).ConfigureAwait(false);
                        records.Add(replacement);
                    }

                    stale.Status = CheckRunStatusEnum.Canceled;
                    stale.Summary = "Superseded"
                        + (replacement == null ? String.Empty : " by " + replacement.Id)
                        + ": this record " + (priorStatus == CheckRunStatusEnum.Failed ? "failed" : "passed") + " at " + Abbreviate(stale.CommitHash)
                        + " but the work of its lane (ending in mission " + lane.LeafMissionId + ") moved to " + Abbreviate(work.CommitHash)
                        + " (mission " + work.Id + "). A verdict for older work does not decide newer work.";
                    stale.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.CheckRuns.UpdateAsync(stale, token).ConfigureAwait(false);
                    await WriteEventAsync(stale, replacement, work, priorStatus, token).ConfigureAwait(false);
                    _Logging?.Info(_Header + "superseded check " + stale.Id + " on voyage " + voyage.Id + " lane " + lane.LeafMissionId
                        + "; lane work is " + Abbreviate(work.CommitHash)
                        + (replacement == null ? String.Empty : "; re-armed as " + replacement.Id));
                    changed++;
                }

                // A lane whose work is final and that has no record of an armed type gets one stamped
                // at its own commit, so its Judge waits for a verdict instead of reading another lane's.
                List<CheckRun> templates = records
                    .Where(IsVoyageGateType)
                    .OrderBy(run => run.CreatedUtc)
                    .GroupBy(run => run.Type)
                    .Select(group => group.First())
                    .ToList();
                foreach (VoyageLane lane in lanes)
                {
                    Mission? work = SelectFinalLaneWork(lane);
                    if (work == null) continue;
                    foreach (CheckRun template in templates)
                    {
                        if (LaneCovers(lanes, lane, records, template.Type, work.CommitHash, null)) continue;
                        CheckRun armed = await ArmLaneRecordAsync(voyage, template, work,
                            template.Type.ToString() + " (armed for lane ending in " + lane.LeafMissionId + ")", token).ConfigureAwait(false);
                        records.Add(armed);
                        _Logging?.Info(_Header + "armed check " + armed.Id + " on voyage " + voyage.Id + " for lane " + lane.LeafMissionId
                            + " at " + Abbreviate(work.CommitHash));
                    }
                }

                // A record that measures several lanes decides none of them; once every lane it spans
                // has a verdict of its own it is retired.
                foreach (CheckRun shared in records.ToList())
                {
                    if (shared.Status == CheckRunStatusEnum.Canceled || shared.Status == CheckRunStatusEnum.Running) continue;
                    if (!IsVoyageGateType(shared)) continue;
                    IReadOnlyList<VoyageLane> spans = VoyageLanes.LanesOfRecord(lanes, shared);
                    if (spans.Count < 2) continue;
                    bool covered = true;
                    foreach (VoyageLane lane in spans)
                    {
                        Mission? work = SelectFinalLaneWork(lane);
                        if (work == null || !LaneCovers(lanes, lane, records, shared.Type, work.CommitHash, null))
                        {
                            covered = false;
                            break;
                        }
                    }
                    if (!covered) continue;

                    CheckRunStatusEnum priorStatus = shared.Status;
                    shared.Status = CheckRunStatusEnum.Canceled;
                    shared.Summary = "Superseded: this record measures " + spans.Count + " lanes of a split voyage"
                        + (String.IsNullOrWhiteSpace(shared.CommitHash) ? String.Empty : " (at " + Abbreviate(shared.CommitHash) + ")")
                        + ", and each lane has a record of its own. A record shared by several lanes decides none of them.";
                    shared.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.CheckRuns.UpdateAsync(shared, token).ConfigureAwait(false);
                    _Logging?.Info(_Header + "retired shared check " + shared.Id + " (" + priorStatus + ") on voyage " + voyage.Id);
                    changed++;
                }

                return changed;
            }
            finally
            {
                voyageLock.Release();
            }
        }

        private static string Abbreviate(string? commit)
        {
            if (String.IsNullOrWhiteSpace(commit)) return "(none)";
            string trimmed = commit.Trim();
            return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
        }

        private async Task WriteEventAsync(CheckRun stale, CheckRun? replacement, Mission work, CheckRunStatusEnum priorStatus, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(
                "check.superseded",
                "Check " + stale.Id + " superseded: " + (priorStatus == CheckRunStatusEnum.Failed ? "failed" : "passed") + " at " + Abbreviate(stale.CommitHash)
                    + ", work under review moved to " + Abbreviate(work.CommitHash))
            {
                TenantId = stale.TenantId,
                UserId = stale.UserId,
                EntityType = "check_run",
                EntityId = stale.Id,
                MissionId = work.Id,
                VesselId = stale.VesselId,
                VoyageId = stale.VoyageId,
                Payload = JsonSerializer.Serialize(new
                {
                    StaleCheckId = stale.Id,
                    ReplacementCheckId = replacement?.Id,
                    PriorStatus = priorStatus.ToString(),
                    stale.Type,
                    MeasuredCommit = stale.CommitHash,
                    WorkCommit = work.CommitHash,
                    WorkMissionId = work.Id
                }, _JsonOptions)
            };

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        #endregion
    }
}
