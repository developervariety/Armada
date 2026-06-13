namespace Armada.Core.Services
{
    using System.Diagnostics;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;


    /// <summary>
    /// Merge queue that processes vessel+target-branch groups in parallel, while
    /// entries within each group are processed sequentially one at a time.  Each
    /// successful merge is landed immediately so the next entry in the same group
    /// merges against the up-to-date target branch, eliminating the cascade
    /// failures that occur with batch-style processing.
    /// </summary>
    public class MergeQueueService : IMergeQueueService
    {
        #region Private-Members

        private string _Header = "[MergeQueue] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;
        private IMergeFailureClassifier _Classifier;
        private Func<PullRequestPlatform, string, IPullRequestService>? _PullRequestServiceFactory;
        private ICodeIndexService? _CodeIndexService;
        private IMergeRecoveryHandler? _RecoveryHandler;

        private bool _Processing = false;
        private readonly object _ProcessLock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git service.</param>
        /// <param name="classifier">Pure classifier invoked at fail-time to populate
        /// the merge entry's failure-class fields before the Failed status is persisted.</param>
        /// <param name="pullRequestServiceFactory">Optional factory that returns the right
        /// <see cref="IPullRequestService"/> for a (platform, working-directory) pair. When
        /// non-null, entries flagged with <c>AuditCriticalTrigger</c> are routed to PR fallback
        /// instead of an automatic land. When null, the legacy land-everything path applies.</param>
        /// <param name="codeIndexService">Optional code-index service invoked after a merge entry
        /// lands successfully (fire-and-forget). When null, no index refresh runs.</param>
        public MergeQueueService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            IMergeFailureClassifier classifier,
            Func<PullRequestPlatform, string, IPullRequestService>? pullRequestServiceFactory = null,
            ICodeIndexService? codeIndexService = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
            _PullRequestServiceFactory = pullRequestServiceFactory;
            _CodeIndexService = codeIndexService;
        }

        /// <summary>
        /// Wire the recovery handler post-construction. The handler depends on
        /// IMergeQueueService (cyclic), so it cannot be supplied via the constructor;
        /// callers wire it once both services exist.
        /// </summary>
        /// <param name="handler">Recovery handler invoked on Failed-status transitions.</param>
        public void SetRecoveryHandler(IMergeRecoveryHandler handler)
        {
            _RecoveryHandler = handler;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            entry.Status = MergeStatusEnum.Queued;
            entry.CreatedUtc = DateTime.UtcNow;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.CreateAsync(entry, token).ConfigureAwait(false);
            await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);

            _Logging.Info(_Header + "enqueued " + entry.Id + " branch " + entry.BranchName + " -> " + entry.TargetBranch);
            return entry;
        }

        /// <inheritdoc />
        public async Task ProcessQueueAsync(CancellationToken token = default)
        {
            // ProcessQueueAsync is a background/system method (called from Admiral loop).
            // It processes all tenants' entries, so unscoped calls are appropriate here.
            lock (_ProcessLock)
            {
                if (_Processing) return;
                _Processing = true;
            }

            try
            {
                // Get all queued entries ordered by priority then created_utc
                List<MergeEntry> queued = await _Database.MergeEntries.EnumerateByStatusAsync(MergeStatusEnum.Queued, token).ConfigureAwait(false);

                if (queued.Count == 0) return;

                // Group by vessel + target branch (independent repos can process independently)
                IEnumerable<IGrouping<string, MergeEntry>> groups = queued.GroupBy(
                    e => (e.VesselId ?? "default") + ":" + e.TargetBranch);

                List<Task> groupTasks = new List<Task>();

                foreach (IGrouping<string, MergeEntry> group in groups)
                {
                    // Order by priority (lower = higher priority) then by creation time ascending
                    List<MergeEntry> entries = group
                        .OrderBy(e => e.Priority)
                        .ThenBy(e => e.CreatedUtc)
                        .ToList();

                    groupTasks.Add(ProcessGroupSafeAsync(entries, token));
                }

                await Task.WhenAll(groupTasks).ConfigureAwait(false);
            }
            finally
            {
                lock (_ProcessLock) { _Processing = false; }
            }
        }

        /// <inheritdoc />
        public async Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(entryId)) throw new ArgumentNullException(nameof(entryId));

            MergeEntry? entry = !String.IsNullOrEmpty(tenantId)
                ? await _Database.MergeEntries.ReadAsync(tenantId, entryId, token).ConfigureAwait(false)
                : await _Database.MergeEntries.ReadAsync(entryId, token).ConfigureAwait(false);
            if (entry != null)
            {
                entry.Status = MergeStatusEnum.Cancelled;
                entry.LastUpdateUtc = DateTime.UtcNow;
                entry.CompletedUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                await UpdateLandingJobFromEntryAsync(entry, "Cancelled by operator", token).ConfigureAwait(false);
                _Logging.Info(_Header + "cancelled " + entryId);
            }
        }

        /// <inheritdoc />
        public async Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default)
        {
            List<MergeEntry> results = !String.IsNullOrEmpty(tenantId)
                ? await _Database.MergeEntries.EnumerateAsync(tenantId, token).ConfigureAwait(false)
                : await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
            return results;
        }

        /// <inheritdoc />
        public async Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(entryId)) throw new ArgumentNullException(nameof(entryId));

            MergeEntry? entry = !String.IsNullOrEmpty(tenantId)
                ? await _Database.MergeEntries.ReadAsync(tenantId, entryId, token).ConfigureAwait(false)
                : await _Database.MergeEntries.ReadAsync(entryId, token).ConfigureAwait(false);
            if (entry == null) return null;
            if (!IsLandingState(entry.Status)) return null;

            _Logging.Info(_Header + "processing single entry " + entryId);
            await ProcessEntryAsync(entry, token).ConfigureAwait(false);

            // Re-read from DB to get updated state
            return !String.IsNullOrEmpty(tenantId)
                ? await _Database.MergeEntries.ReadAsync(tenantId, entryId, token).ConfigureAwait(false)
                : await _Database.MergeEntries.ReadAsync(entryId, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default)
        {
            MergeEntry? entry = !String.IsNullOrEmpty(tenantId)
                ? await _Database.MergeEntries.ReadAsync(tenantId, entryId, token).ConfigureAwait(false)
                : await _Database.MergeEntries.ReadAsync(entryId, token).ConfigureAwait(false);
            return entry;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(entryId)) throw new ArgumentNullException(nameof(entryId));

            MergeEntry? entry = !String.IsNullOrEmpty(tenantId)
                ? await _Database.MergeEntries.ReadAsync(tenantId, entryId, token).ConfigureAwait(false)
                : await _Database.MergeEntries.ReadAsync(entryId, token).ConfigureAwait(false);
            if (entry == null) return false;

            // Only allow deletion of terminal entries
            if (entry.Status != MergeStatusEnum.Cancelled &&
                entry.Status != MergeStatusEnum.Landed &&
                entry.Status != MergeStatusEnum.Failed)
            {
                _Logging.Warn(_Header + "cannot delete " + entryId + " in non-terminal status " + entry.Status);
                return false;
            }

            // Clean up git branches associated with this entry
            if (!String.IsNullOrEmpty(entry.BranchName))
            {
                string? repoPath = await GetRepoPathAsync(entry, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(repoPath))
                {
                    // Delete remote branch
                    try
                    {
                        await RunGitAsync(repoPath, token, "push", "origin", "--delete", entry.BranchName).ConfigureAwait(false);
                        _Logging.Info(_Header + "deleted remote branch " + entry.BranchName);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Debug(_Header + "remote branch delete for " + entry.BranchName + " skipped: " + ex.Message);
                    }

                    // Delete local branch
                    try
                    {
                        await _Git.DeleteLocalBranchAsync(repoPath, entry.BranchName, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "deleted local branch " + entry.BranchName);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Debug(_Header + "local branch delete for " + entry.BranchName + " skipped: " + ex.Message);
                    }
                }
            }

            if (!String.IsNullOrEmpty(tenantId))
            {
                await _Database.LandingJobs.DeleteByMergeEntryAsync(entryId, token).ConfigureAwait(false);
                await _Database.MergeEntries.DeleteAsync(tenantId, entryId, token).ConfigureAwait(false);
            }
            else
            {
                await _Database.LandingJobs.DeleteByMergeEntryAsync(entryId, token).ConfigureAwait(false);
                await _Database.MergeEntries.DeleteAsync(entryId, token).ConfigureAwait(false);
            }

            _Logging.Info(_Header + "deleted " + entryId);
            return true;
        }

        /// <inheritdoc />
        public async Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default)
        {
            if (entryIds == null) throw new ArgumentNullException(nameof(entryIds));

            MergeQueuePurgeResult result = new MergeQueuePurgeResult();

            foreach (string entryId in entryIds)
            {
                if (String.IsNullOrEmpty(entryId))
                {
                    result.Skipped.Add(new MergeQueuePurgeSkipped(entryId ?? "", "Empty entry ID"));
                    continue;
                }

                MergeEntry? entry = !String.IsNullOrEmpty(tenantId)
                    ? await _Database.MergeEntries.ReadAsync(tenantId, entryId, token).ConfigureAwait(false)
                    : await _Database.MergeEntries.ReadAsync(entryId, token).ConfigureAwait(false);
                if (entry == null)
                {
                    result.Skipped.Add(new MergeQueuePurgeSkipped(entryId, "Not found"));
                    continue;
                }

                bool deleted = await DeleteAsync(entryId, tenantId, token).ConfigureAwait(false);
                if (deleted)
                {
                    result.EntriesPurged++;
                }
                else
                {
                    result.Skipped.Add(new MergeQueuePurgeSkipped(entryId, "Not in terminal state (status: " + entry.Status + ")"));
                }
            }

            _Logging.Info(_Header + "batch purge: " + result.EntriesPurged + " purged, " + result.Skipped.Count + " skipped");
            return result;
        }

        /// <inheritdoc />
        public async Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default)
        {
            List<MergeStatusEnum> terminalStatuses = new List<MergeStatusEnum>
            {
                MergeStatusEnum.Landed,
                MergeStatusEnum.Failed,
                MergeStatusEnum.Cancelled
            };

            if (status != null && !terminalStatuses.Contains(status.Value))
            {
                _Logging.Warn(_Header + "purge requested for non-terminal status " + status.Value);
                return 0;
            }

            List<MergeStatusEnum> statusesToPurge = status != null
                ? new List<MergeStatusEnum> { status.Value }
                : terminalStatuses;

            List<MergeEntry> candidates = new List<MergeEntry>();
            foreach (MergeStatusEnum s in statusesToPurge)
            {
                // PurgeTerminalAsync enumerates by status (no tenant-scoped overload for EnumerateByStatusAsync).
                // Use unscoped enumeration and filter by tenantId in-memory if needed.
                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateByStatusAsync(s, token).ConfigureAwait(false);
                candidates.AddRange(entries);
            }

            if (!String.IsNullOrEmpty(vesselId))
            {
                candidates = candidates.Where(e => e.VesselId == vesselId).ToList();
            }

            int deleted = 0;
            foreach (MergeEntry entry in candidates)
            {
                bool result = await DeleteAsync(entry.Id, tenantId, token).ConfigureAwait(false);
                if (result) deleted++;
            }

            _Logging.Info(_Header + "purged " + deleted + " terminal entries");
            return deleted;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Wraps <see cref="ProcessGroupAsync"/> in a try-catch so that an
        /// unexpected exception in one group does not cancel other groups.
        /// </summary>
        private async Task ProcessGroupSafeAsync(List<MergeEntry> entries, CancellationToken token)
        {
            try
            {
                await ProcessGroupAsync(entries, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "group processing error: " + ex.Message);
            }
        }

        /// <summary>
        /// Process a group of entries that share the same vessel and target branch.
        /// Entries are processed one at a time in priority/creation order.  Each
        /// successful merge is landed immediately so the next entry merges against
        /// the updated target.
        /// </summary>
        private async Task ProcessGroupAsync(List<MergeEntry> entries, CancellationToken token)
        {
            if (entries.Count == 0) return;

            MergeEntry first = entries[0];
            string? repoPath = await GetRepoPathAsync(first, token).ConfigureAwait(false);

            if (repoPath == null)
            {
                _Logging.Warn(_Header + "unable to resolve repo path for vessel " + first.VesselId + " -- failing all entries");
                foreach (MergeEntry entry in entries)
                {
                    entry.Status = MergeStatusEnum.Failed;
                    entry.TestOutput = "Unable to resolve repository path for vessel " + first.VesselId;
                    entry.CompletedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                    await UpdateLandingJobFromEntryAsync(entry, entry.TestOutput, token).ConfigureAwait(false);
                }
                return;
            }

            _Logging.Info(_Header + "processing " + entries.Count + " entries for " + first.TargetBranch + " on vessel " + (first.VesselId ?? "default"));

            foreach (MergeEntry entry in entries)
            {
                await ProcessEntryAsync(entry, repoPath, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Process a single merge entry: fetch, merge, test, land.
        /// If any step fails the entry is marked Failed and processing continues
        /// to the next entry in the group.
        /// </summary>
        private async Task ProcessEntryAsync(MergeEntry entry, CancellationToken token)
        {
            string? repoPath = await GetRepoPathAsync(entry, token).ConfigureAwait(false);
            if (repoPath == null)
            {
                entry.Status = MergeStatusEnum.Failed;
                entry.TestOutput = "Unable to resolve repository path for vessel " + entry.VesselId;
                entry.CompletedUtc = DateTime.UtcNow;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                await UpdateLandingJobFromEntryAsync(entry, entry.TestOutput, token).ConfigureAwait(false);
                return;
            }

            await ProcessEntryAsync(entry, repoPath, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(entryId)) throw new ArgumentNullException(nameof(entryId));

            MergeEntry? entry = await _Database.MergeEntries.ReadAsync(entryId, token).ConfigureAwait(false);
            if (entry is null)
            {
                _Logging.Warn(_Header + "ProcessEntryByIdAsync: entry " + entryId + " not found");
                return;
            }

            if (!IsLandingState(entry.Status))
            {
                _Logging.Warn(_Header + "ProcessEntryByIdAsync: entry " + entryId + " is in status " + entry.Status + " -- skipping");
                return;
            }

            string? repoPath = await GetRepoPathAsync(entry, token).ConfigureAwait(false);
            if (repoPath is null)
            {
                _Logging.Warn(_Header + "ProcessEntryByIdAsync: cannot resolve repo path for entry " + entryId);
                return;
            }

            await ProcessEntryAsync(entry, repoPath, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default)
        {
            List<LandingJobStateEnum> states = new List<LandingJobStateEnum>
            {
                LandingJobStateEnum.Rebasing,
                LandingJobStateEnum.Merging,
                LandingJobStateEnum.Testing,
                LandingJobStateEnum.Passed,
                LandingJobStateEnum.Pushing,
                LandingJobStateEnum.CreatingPR,
                LandingJobStateEnum.Queued
            };

            await EnsureLandingJobsForStatesAsync(states, token).ConfigureAwait(false);

            List<LandingJob> candidates = new List<LandingJob>();
            foreach (LandingJobStateEnum state in states)
            {
                List<LandingJob> jobs = await _Database.LandingJobs.EnumerateByStateAsync(state, token).ConfigureAwait(false);
                candidates.AddRange(jobs);
            }

            if (candidates.Count == 0) return 0;

            List<LandingJob> ordered = candidates
                .OrderBy(j => LandingStateRank(j.State))
                .ThenBy(j => j.CreatedUtc)
                .ToList();

            HashSet<string> activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int advanced = 0;

            foreach (LandingJob job in ordered)
            {
                if (advanced >= 10) break;

                string key = (job.VesselId ?? "default") + ":" + job.TargetBranch;
                if (activeKeys.Contains(key)) continue;
                activeKeys.Add(key);

                MergeEntry? entry = await LoadEntryFromLandingJobAsync(job, token).ConfigureAwait(false);
                if (entry == null)
                {
                    advanced++;
                    continue;
                }

                string? repoPath = await GetRepoPathAsync(entry, token).ConfigureAwait(false);
                if (repoPath == null)
                {
                    await TransitionEntryToFailureAsync(entry, "Unable to resolve repository path for vessel " + entry.VesselId, token).ConfigureAwait(false);
                    advanced++;
                    continue;
                }

                try
                {
                    bool didAdvance = await AdvanceLandingStateMachineOneStepAsync(entry, repoPath, token).ConfigureAwait(false);
                    if (didAdvance) advanced++;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "landing state-machine error for " + entry.Id + ": " + ex.Message);
                    await TransitionEntryToFailureAsync(entry, "Landing state-machine error: " + ex.Message, token).ConfigureAwait(false);
                    advanced++;
                }
            }

            if (advanced > 0)
            {
                _Logging.Info(_Header + "advanced " + advanced + " landing state-machine entr" + (advanced == 1 ? "y" : "ies"));
            }

            return advanced;
        }

        /// <inheritdoc />
        public async Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default)
        {
            List<LandingJobStateEnum> states = new List<LandingJobStateEnum>
            {
                LandingJobStateEnum.Rebasing,
                LandingJobStateEnum.Merging,
                LandingJobStateEnum.Testing,
                LandingJobStateEnum.Passed,
                LandingJobStateEnum.Pushing,
                LandingJobStateEnum.CreatingPR
            };

            await EnsureLandingJobsForStatesAsync(states, token).ConfigureAwait(false);

            List<LandingJob> candidates = new List<LandingJob>();
            foreach (LandingJobStateEnum state in states)
            {
                List<LandingJob> jobs = await _Database.LandingJobs.EnumerateByStateAsync(state, token).ConfigureAwait(false);
                candidates.AddRange(jobs);
            }

            if (candidates.Count == 0) return 0;

            List<LandingJob> ordered = candidates
                .OrderBy(j => LandingStateRank(j.State))
                .ThenBy(j => j.CreatedUtc)
                .ToList();

            HashSet<string> activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int recovered = 0;

            foreach (LandingJob job in ordered)
            {
                if (token.IsCancellationRequested) break;

                string key = (job.VesselId ?? "default") + ":" + job.TargetBranch;
                if (!activeKeys.Add(key)) continue;

                _Logging.Info(_Header + "recovering landing job " + job.Id + " for entry " + job.MergeEntryId + " from state " + job.State);

                MergeEntry? entry = await LoadEntryFromLandingJobAsync(job, token).ConfigureAwait(false);
                if (entry == null)
                {
                    recovered++;
                    continue;
                }

                string? repoPath = await GetRepoPathAsync(entry, token).ConfigureAwait(false);
                if (repoPath == null)
                {
                    await TransitionEntryToFailureAsync(entry, "Unable to resolve repository path for vessel " + entry.VesselId + " during startup landing recovery", token).ConfigureAwait(false);
                    recovered++;
                    continue;
                }

                try
                {
                    await ProcessEntryAsync(entry, repoPath, token).ConfigureAwait(false);
                    recovered++;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "startup landing recovery error for " + entry.Id + ": " + ex.Message);
                    await TransitionEntryToFailureAsync(entry, "Startup landing recovery error: " + ex.Message, token).ConfigureAwait(false);
                    recovered++;
                }
            }

            if (recovered > 0)
            {
                _Logging.Info(_Header + "recovered " + recovered + " in-flight landing entr" + (recovered == 1 ? "y" : "ies") + " on startup");
            }

            return recovered;
        }

        /// <summary>
        /// Core single-entry processing with a known repo path.
        /// </summary>
        private async Task ProcessEntryAsync(MergeEntry entry, string repoPath, CancellationToken token)
        {
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            entry = await SyncEntryToLandingJobAsync(entry, job, token).ConfigureAwait(false);

            _Logging.Info(_Header + "processing " + entry.Id + " branch " + entry.BranchName);

            for (int i = 0; i < 10; i++)
            {
                if (!IsLandingState(entry.Status)) return;

                bool advanced = await AdvanceLandingStateMachineOneStepAsync(entry, repoPath, token).ConfigureAwait(false);
                if (!advanced) return;

                MergeEntry? refreshed = await _Database.MergeEntries.ReadAsync(entry.Id, token).ConfigureAwait(false);
                if (refreshed == null) return;
                entry = refreshed;
            }
        }

        private async Task<bool> AdvanceLandingStateMachineOneStepAsync(MergeEntry entry, string repoPath, CancellationToken token)
        {
            if (entry.Status == MergeStatusEnum.Queued || entry.Status == MergeStatusEnum.Rebasing)
            {
                await PersistStatusAsync(entry, MergeStatusEnum.Rebasing, token).ConfigureAwait(false);
                await PrepareIntegrationWorktreeAsync(entry, repoPath, token).ConfigureAwait(false);
                await PersistStatusAsync(entry, MergeStatusEnum.Merging, token).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Merging)
            {
                if (!Directory.Exists(GetIntegrationPath(entry)))
                {
                    await PersistStatusAsync(entry, MergeStatusEnum.Rebasing, token).ConfigureAwait(false);
                    return true;
                }

                await MergeIntegrationWorktreeAsync(entry, token).ConfigureAwait(false);
                if (entry.Status == MergeStatusEnum.Failed) return false;
                await PersistStatusAsync(entry, MergeStatusEnum.Testing, token, startedTests: true).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Testing)
            {
                if (!Directory.Exists(GetIntegrationPath(entry)))
                {
                    await PersistStatusAsync(entry, MergeStatusEnum.Rebasing, token).ConfigureAwait(false);
                    return true;
                }

                await TestIntegrationWorktreeAsync(entry, token).ConfigureAwait(false);
                if (entry.Status == MergeStatusEnum.Failed) return false;
                await PersistStatusAsync(entry, MergeStatusEnum.Passed, token).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Passed)
            {
                MergeStatusEnum nextStatus = _PullRequestServiceFactory != null && !String.IsNullOrEmpty(entry.AuditCriticalTrigger)
                    ? MergeStatusEnum.CreatingPR
                    : MergeStatusEnum.Pushing;
                await PersistStatusAsync(entry, nextStatus, token).ConfigureAwait(false);
                return true;
            }

            if (entry.Status == MergeStatusEnum.Pushing)
            {
                if (!Directory.Exists(GetIntegrationPath(entry)))
                {
                    await PersistStatusAsync(entry, MergeStatusEnum.Rebasing, token).ConfigureAwait(false);
                    return true;
                }

                await LandEntryAsync(entry, repoPath, GetIntegrationBranch(entry), token).ConfigureAwait(false);
                await CleanupWorktreeAsync(GetIntegrationPath(entry), token).ConfigureAwait(false);

                if (entry.Status == MergeStatusEnum.Landed)
                {
                    await CleanupLandedBranchesAsync(entry, repoPath, GetIntegrationBranch(entry), token).ConfigureAwait(false);
                }

                return true;
            }

            if (entry.Status == MergeStatusEnum.CreatingPR)
            {
                if (!String.IsNullOrEmpty(entry.PrUrl))
                {
                    await MarkExistingPullRequestOpenAsync(entry, token).ConfigureAwait(false);
                    await CleanupWorktreeAsync(GetIntegrationPath(entry), token).ConfigureAwait(false);
                    return true;
                }

                bool routedToPr = await TryOpenPullRequestAsync(entry, repoPath, forceChainedBase: false, token).ConfigureAwait(false);
                if (!routedToPr && entry.Status != MergeStatusEnum.Failed)
                {
                    await PersistStatusAsync(entry, MergeStatusEnum.Pushing, token).ConfigureAwait(false);
                }
                else if (routedToPr)
                {
                    await CleanupWorktreeAsync(GetIntegrationPath(entry), token).ConfigureAwait(false);
                }

                return true;
            }

            return false;
        }

        private async Task PrepareIntegrationWorktreeAsync(MergeEntry entry, string repoPath, CancellationToken token)
        {
            string integrationPath = GetIntegrationPath(entry);
            string integrationBranch = GetIntegrationBranch(entry);
            string? parent = Path.GetDirectoryName(integrationPath);
            if (!String.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            try { await CleanupWorktreeAsync(integrationPath, token).ConfigureAwait(false); }
            catch { }

            try { await _Git.DeleteLocalBranchAsync(repoPath, integrationBranch, token).ConfigureAwait(false); }
            catch (Exception ex) { _Logging.Debug(_Header + "integration branch cleanup skipped for " + integrationBranch + ": " + ex.Message); }

            await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);
            await _Git.CreateWorktreeAsync(repoPath, integrationPath, integrationBranch, entry.TargetBranch, token).ConfigureAwait(false);
        }

        private async Task MergeIntegrationWorktreeAsync(MergeEntry entry, CancellationToken token)
        {
            string integrationPath = GetIntegrationPath(entry);
            string entryTag = entry.Id + " branch " + entry.BranchName;
            string headBeforeMerge = await ResolveCommitAsync(integrationPath, "HEAD", token).ConfigureAwait(false);
            bool alreadyMerged = await IsAncestorAsync(integrationPath, entry.BranchName, "HEAD", token).ConfigureAwait(false);

            if (!alreadyMerged)
            {
                MergeAttemptResult mergeAttempt = await MergeBranchAsync(integrationPath, entry.BranchName, token).ConfigureAwait(false);
                if (!mergeAttempt.Ok)
                {
                    _Logging.Warn(_Header + "merge conflict for " + entryTag);

                    List<string> conflictedFiles = await CollectConflictedFilesAsync(integrationPath, token).ConfigureAwait(false);
                    int diffLineCount = await ComputeDiffLineCountAsync(integrationPath, entry.TargetBranch, entry.BranchName, token).ConfigureAwait(false);
                    MergeFailureContext mergeContext = new MergeFailureContext
                    {
                        GitExitCode = mergeAttempt.GitExitCode,
                        GitStandardOutput = mergeAttempt.StandardOutput,
                        GitStandardError = mergeAttempt.StandardError,
                        ConflictedFiles = conflictedFiles,
                        DiffLineCount = diffLineCount
                    };
                    ApplyClassification(entry, mergeContext);

                    entry.Status = MergeStatusEnum.Failed;
                    entry.TestOutput = "Merge conflict with " + entry.TargetBranch;
                    entry.CompletedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                    await UpdateLandingJobFromEntryAsync(entry, entry.TestOutput, token).ConfigureAwait(false);
                    await CleanupWorktreeAsync(integrationPath, token).ConfigureAwait(false);
                    FireRecoveryHandlerForEntry(entry.Id);
                    return;
                }
            }

            string integrationHeadAfterMerge = await ResolveCommitAsync(integrationPath, "HEAD", token).ConfigureAwait(false);
            string targetHead = await ResolveCommitAsync(integrationPath, entry.TargetBranch, token).ConfigureAwait(false);
            if (String.Equals(targetHead, integrationHeadAfterMerge, StringComparison.OrdinalIgnoreCase) ||
                (!alreadyMerged && String.Equals(headBeforeMerge, integrationHeadAfterMerge, StringComparison.OrdinalIgnoreCase)))
            {
                string failureReason = "No-op merge queue entry: branch " + entry.BranchName +
                    " does not advance target branch " + entry.TargetBranch +
                    " (HEAD remains " + integrationHeadAfterMerge + ")";
                _Logging.Warn(_Header + failureReason + " for " + entryTag);
                await TransitionEntryToFailureAsync(entry, failureReason, token, fireRecovery: false).ConfigureAwait(false);
                await CleanupWorktreeAsync(integrationPath, token).ConfigureAwait(false);
            }
        }

        private async Task TestIntegrationWorktreeAsync(MergeEntry entry, CancellationToken token)
        {
            string integrationPath = GetIntegrationPath(entry);
            string entryTag = entry.Id + " branch " + entry.BranchName;
            string? protectedPathViolation = await FindProtectedPathViolationAsync(entry, integrationPath, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(protectedPathViolation))
            {
                Vessel? violationVessel = await ReadEntryVesselAsync(entry, token).ConfigureAwait(false);
                string failureReason = ProtectedPathsValidator.FormatFailureReason(
                    protectedPathViolation,
                    violationVessel?.Name ?? entry.VesselId ?? "unknown");
                _Logging.Warn(_Header + "protected path violation for " + entryTag + ": " + failureReason);
                await TransitionEntryToFailureAsync(entry, failureReason, token, fireRecovery: false).ConfigureAwait(false);
                await CleanupWorktreeAsync(integrationPath, token).ConfigureAwait(false);
                return;
            }

            string testCommand = entry.TestCommand ?? _Settings.MergeQueueTestCommand ?? "";
            if (String.IsNullOrEmpty(testCommand)) return;

            TestResult testResult = await RunTestsAsync(integrationPath, testCommand, token).ConfigureAwait(false);
            if (testResult.ExitCode != 0)
            {
                _Logging.Warn(_Header + "tests FAILED for " + entryTag + " (exit " + testResult.ExitCode + ")");

                int diffLineCount = await ComputeDiffLineCountAsync(integrationPath, entry.TargetBranch, entry.BranchName, token).ConfigureAwait(false);
                MergeFailureContext testContext = new MergeFailureContext
                {
                    TestExitCode = testResult.ExitCode,
                    TestOutput = testResult.Output,
                    ConflictedFiles = new List<string>(),
                    DiffLineCount = diffLineCount
                };
                ApplyClassification(entry, testContext);

                entry.Status = MergeStatusEnum.Failed;
                entry.TestExitCode = testResult.ExitCode;
                entry.TestOutput = TruncateOutput(testResult.Output);
                entry.CompletedUtc = DateTime.UtcNow;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                await UpdateLandingJobFromEntryAsync(entry, entry.TestOutput, token).ConfigureAwait(false);
                await CleanupWorktreeAsync(integrationPath, token).ConfigureAwait(false);
                FireRecoveryHandlerForEntry(entry.Id);
                return;
            }

            _Logging.Info(_Header + "tests PASSED for " + entryTag);
        }

        private async Task PersistStatusAsync(MergeEntry entry, MergeStatusEnum status, CancellationToken token, bool startedTests = false)
        {
            entry.Status = status;
            entry.LastUpdateUtc = DateTime.UtcNow;
            if (startedTests && !entry.TestStartedUtc.HasValue)
            {
                entry.TestStartedUtc = entry.LastUpdateUtc;
            }
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            await UpdateLandingJobFromEntryAsync(entry, null, token).ConfigureAwait(false);
        }

        private async Task<LandingJob> EnsureLandingJobAsync(MergeEntry entry, CancellationToken token)
        {
            LandingJob? job = await _Database.LandingJobs.ReadByMergeEntryAsync(entry.Id, token).ConfigureAwait(false);
            if (job != null) return job;

            job = new LandingJob();
            job.TenantId = entry.TenantId;
            job.UserId = entry.UserId;
            job.MergeEntryId = entry.Id;
            job.MissionId = entry.MissionId;
            job.VesselId = entry.VesselId;
            job.BranchName = entry.BranchName;
            job.TargetBranch = entry.TargetBranch;
            job.State = ToLandingJobState(entry.Status);
            job.CreatedUtc = entry.CreatedUtc;
            job.LastUpdateUtc = entry.LastUpdateUtc;
            job.StartedUtc = IsStartedLandingState(job.State) ? entry.LastUpdateUtc : null;
            job.CompletedUtc = IsTerminalLandingState(job.State) ? entry.CompletedUtc ?? entry.LastUpdateUtc : null;
            job.LastError = entry.Status == MergeStatusEnum.Failed ? entry.TestOutput : null;
            return await _Database.LandingJobs.CreateAsync(job, token).ConfigureAwait(false);
        }

        private async Task EnsureLandingJobsForStatesAsync(List<LandingJobStateEnum> states, CancellationToken token)
        {
            foreach (LandingJobStateEnum state in states)
            {
                MergeStatusEnum status = ToMergeStatus(state);
                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateByStatusAsync(status, token).ConfigureAwait(false);
                foreach (MergeEntry entry in entries)
                {
                    await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
                }
            }
        }

        private async Task<MergeEntry?> LoadEntryFromLandingJobAsync(LandingJob job, CancellationToken token)
        {
            MergeEntry? entry = await _Database.MergeEntries.ReadAsync(job.MergeEntryId, token).ConfigureAwait(false);
            if (entry == null)
            {
                job.State = LandingJobStateEnum.Failed;
                job.CompletedUtc = DateTime.UtcNow;
                job.LastError = "Merge entry " + job.MergeEntryId + " is missing during landing recovery";
                await _Database.LandingJobs.UpdateAsync(job, token).ConfigureAwait(false);
                return null;
            }

            return await SyncEntryToLandingJobAsync(entry, job, token).ConfigureAwait(false);
        }

        private async Task<MergeEntry> SyncEntryToLandingJobAsync(MergeEntry entry, LandingJob job, CancellationToken token)
        {
            MergeStatusEnum jobStatus = ToMergeStatus(job.State);
            if (entry.Status != jobStatus)
            {
                entry.Status = jobStatus;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            }

            return entry;
        }

        private async Task UpdateLandingJobFromEntryAsync(MergeEntry entry, string? lastError, CancellationToken token)
        {
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            LandingJobStateEnum state = ToLandingJobState(entry.Status);

            job.TenantId = entry.TenantId;
            job.UserId = entry.UserId;
            job.MissionId = entry.MissionId;
            job.VesselId = entry.VesselId;
            job.BranchName = entry.BranchName;
            job.TargetBranch = entry.TargetBranch;
            job.State = state;
            if (IsStartedLandingState(state) && !job.StartedUtc.HasValue)
            {
                job.StartedUtc = entry.LastUpdateUtc;
            }
            if (IsTerminalLandingState(state))
            {
                job.CompletedUtc = entry.CompletedUtc ?? entry.LastUpdateUtc;
            }
            if (!String.IsNullOrEmpty(lastError))
            {
                job.LastError = lastError;
            }
            else if (state != LandingJobStateEnum.Failed)
            {
                job.LastError = null;
            }

            await _Database.LandingJobs.UpdateAsync(job, token).ConfigureAwait(false);
        }

        private static bool IsLandingState(MergeStatusEnum status)
        {
            return status == MergeStatusEnum.Queued
                || status == MergeStatusEnum.Rebasing
                || status == MergeStatusEnum.Merging
                || status == MergeStatusEnum.Testing
                || status == MergeStatusEnum.Passed
                || status == MergeStatusEnum.Pushing
                || status == MergeStatusEnum.CreatingPR;
        }

        private static int LandingStateRank(MergeStatusEnum status)
        {
            if (status == MergeStatusEnum.Queued) return 1;
            return 0;
        }

        private static int LandingStateRank(LandingJobStateEnum state)
        {
            if (state == LandingJobStateEnum.Queued) return 1;
            return 0;
        }

        private static LandingJobStateEnum ToLandingJobState(MergeStatusEnum status)
        {
            return status switch
            {
                MergeStatusEnum.Queued => LandingJobStateEnum.Queued,
                MergeStatusEnum.Rebasing => LandingJobStateEnum.Rebasing,
                MergeStatusEnum.Merging => LandingJobStateEnum.Merging,
                MergeStatusEnum.Testing => LandingJobStateEnum.Testing,
                MergeStatusEnum.Passed => LandingJobStateEnum.Passed,
                MergeStatusEnum.Pushing => LandingJobStateEnum.Pushing,
                MergeStatusEnum.CreatingPR => LandingJobStateEnum.CreatingPR,
                MergeStatusEnum.Landed => LandingJobStateEnum.Landed,
                MergeStatusEnum.Failed => LandingJobStateEnum.Failed,
                MergeStatusEnum.PullRequestOpen => LandingJobStateEnum.PullRequestOpen,
                MergeStatusEnum.Cancelled => LandingJobStateEnum.Cancelled,
                _ => LandingJobStateEnum.Failed
            };
        }

        private static MergeStatusEnum ToMergeStatus(LandingJobStateEnum state)
        {
            return state switch
            {
                LandingJobStateEnum.Queued => MergeStatusEnum.Queued,
                LandingJobStateEnum.Rebasing => MergeStatusEnum.Rebasing,
                LandingJobStateEnum.Merging => MergeStatusEnum.Merging,
                LandingJobStateEnum.Testing => MergeStatusEnum.Testing,
                LandingJobStateEnum.Passed => MergeStatusEnum.Passed,
                LandingJobStateEnum.Pushing => MergeStatusEnum.Pushing,
                LandingJobStateEnum.CreatingPR => MergeStatusEnum.CreatingPR,
                LandingJobStateEnum.Landed => MergeStatusEnum.Landed,
                LandingJobStateEnum.Failed => MergeStatusEnum.Failed,
                LandingJobStateEnum.PullRequestOpen => MergeStatusEnum.PullRequestOpen,
                LandingJobStateEnum.Cancelled => MergeStatusEnum.Cancelled,
                _ => MergeStatusEnum.Failed
            };
        }

        private static bool IsStartedLandingState(LandingJobStateEnum state)
        {
            return state != LandingJobStateEnum.Queued;
        }

        private static bool IsTerminalLandingState(LandingJobStateEnum state)
        {
            return state == LandingJobStateEnum.Landed
                || state == LandingJobStateEnum.Failed
                || state == LandingJobStateEnum.PullRequestOpen
                || state == LandingJobStateEnum.Cancelled;
        }

        private string GetIntegrationBranch(MergeEntry entry)
        {
            return "armada/merge-queue/" + entry.Id;
        }

        private string GetIntegrationPath(MergeEntry entry)
        {
            return Path.Combine(_Settings.DocksDirectory, "_merge-queue", entry.Id);
        }

        /// <inheritdoc />
        public async Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(mergeEntryId)) throw new ArgumentNullException(nameof(mergeEntryId));

            MergeEntry? entry = await _Database.MergeEntries.ReadAsync(mergeEntryId, token).ConfigureAwait(false);
            if (entry == null)
            {
                _Logging.Warn(_Header + "PR-fallback recovery: entry not found " + mergeEntryId);
                return false;
            }

            string? repoPath = await GetRepoPathAsync(entry, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(repoPath))
            {
                _Logging.Warn(_Header + "PR-fallback recovery: repo path missing for " + mergeEntryId);
                return false;
            }

            // Force the trigger so TryOpenPullRequestAsync's internal callers and the
            // outbound PR body both reflect the recovery reason. This is idempotent --
            // the existing trigger is preserved if it is already set to a recovery
            // marker.
            if (String.IsNullOrEmpty(entry.AuditCriticalTrigger))
            {
                entry.AuditCriticalTrigger = "recovery_exhausted";
                entry.LastUpdateUtc = DateTime.UtcNow;
                try
                {
                    await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "PR-fallback recovery: stamp-trigger failed for " + mergeEntryId + ": " + ex.Message);
                }
            }

            return await TryOpenPullRequestAsync(entry, repoPath, forceChainedBase: false, token).ConfigureAwait(false);
        }

        /// <summary>
        /// PR-fallback path. Push the captain branch to origin and open a platform PR
        /// targeting either the vessel default branch or, when forceChainedBase is true OR
        /// the upstream dep is itself in PullRequestOpen, the upstream captain branch.
        /// On success the entry transitions to <see cref="MergeStatusEnum.PullRequestOpen"/>
        /// and the linked mission to <see cref="MissionStatusEnum.PullRequestOpen"/>.
        /// On failure the entry transitions to Failed and the mission to LandingFailed.
        /// Returns true when the entry was routed to PR (caller skips LandEntryAsync).
        /// </summary>
        private async Task<bool> TryOpenPullRequestAsync(MergeEntry entry, string repoPath, bool forceChainedBase, CancellationToken token)
        {
            if (_PullRequestServiceFactory == null) return false;

            if (!String.IsNullOrEmpty(entry.PrUrl))
            {
                await MarkExistingPullRequestOpenAsync(entry, token).ConfigureAwait(false);
                return true;
            }

            Vessel? vessel = !String.IsNullOrEmpty(entry.VesselId)
                ? await _Database.Vessels.ReadAsync(entry.VesselId, token).ConfigureAwait(false)
                : null;
            Mission? mission = !String.IsNullOrEmpty(entry.MissionId)
                ? await _Database.Missions.ReadAsync(entry.MissionId, token).ConfigureAwait(false)
                : null;

            if (vessel == null || mission == null)
            {
                _Logging.Warn(_Header + "PR-fallback skipped for " + entry.Id + ": vessel or mission missing");
                return false;
            }

            // Determine PR base. Force-chained takes priority. Otherwise consult the upstream
            // dep's most recent merge entry: if it's still in PullRequestOpen, base on the
            // upstream captain branch (chained PR) so reviewers see only THIS mission's diff.
            string baseBranch = entry.TargetBranch;
            if (forceChainedBase || !String.IsNullOrEmpty(mission.DependsOnMissionId))
            {
                MergeEntry? upstreamEntry = await ReadMostRecentMergeEntryAsync(mission.DependsOnMissionId, token).ConfigureAwait(false);
                if (forceChainedBase && upstreamEntry != null)
                    baseBranch = upstreamEntry.BranchName;
                else if (upstreamEntry != null && upstreamEntry.Status == MergeStatusEnum.PullRequestOpen)
                    baseBranch = upstreamEntry.BranchName;
            }

            // Detect platform from the vessel's repo URL (parsed once at PR-fallback time so
            // the hosted-git platform CLI choice mirrors `git remote get-url origin`).
            PullRequestPlatform platform;
            try
            {
                platform = OriginUrlParser.GetPlatform(vessel.RepoUrl ?? "");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "PR-fallback platform detection failed for " + entry.Id + ": " + ex.Message);
                await TransitionEntryToFailureAsync(entry, "PR-fallback failed: " + ex.Message, token).ConfigureAwait(false);
                return false;
            }

            // Push captain branch to origin so the PR has a remote head to target.
            try
            {
                await _Git.PushRefSpecAsync(repoPath, entry.BranchName, entry.BranchName, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "PR-fallback push failed for " + entry.Id + ": " + ex.Message);
                await TransitionEntryToFailureAsync(entry, "PR-fallback push failed: " + ex.Message, token).ConfigureAwait(false);
                return false;
            }

            // Resolve the platform-specific service. Working directory = vessel's normal
            // working tree (where `gh` / `glab` find the repo + auth context).
            string workingDirectory = vessel.WorkingDirectory ?? repoPath;
            IPullRequestService prService = _PullRequestServiceFactory(platform, workingDirectory);

            string title = mission.Title;
            string body = "Auto-routed to PR review by Armada merge queue.\n\n"
                + "Critical trigger: " + entry.AuditCriticalTrigger + "\n"
                + "Mission: " + mission.Id + "\n"
                + "Vessel: " + vessel.Name;

            string prUrl;
            try
            {
                prUrl = await prService.CreateAsync(entry.BranchName, baseBranch, title, body, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "PR-fallback create failed for " + entry.Id + ": " + ex.Message);
                await TransitionEntryToFailureAsync(entry, "PR-fallback create failed: " + ex.Message, token).ConfigureAwait(false);
                return false;
            }

            entry.Status = MergeStatusEnum.PullRequestOpen;
            entry.PrUrl = prUrl;
            entry.PrBaseBranch = baseBranch;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            await UpdateLandingJobFromEntryAsync(entry, null, token).ConfigureAwait(false);
            _Logging.Info(_Header + "opened PR for " + entry.Id + " (" + platform + ", base=" + baseBranch + "): " + prUrl);

            // Also stamp the URL on the mission record so the existing
            // HandleReconcilePullRequestAsync health-check loop (which scans for
            // PullRequestOpen missions with PrUrl != null) picks this up and flips
            // the mission to Complete once the PR merges.
            mission.Status = MissionStatusEnum.PullRequestOpen;
            mission.PrUrl = prUrl;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// PR-merge reconciliation pass for entries currently in PullRequestOpen. Walks
        /// every such entry, checks whether the linked mission has reached Complete (the
        /// existing PR-mode reconciler in MissionLandingHandler flips the mission as soon
        /// as <c>gh pr view --json state</c> reports merged), and flips the merge entry
        /// to Landed when the mission has caught up. The captain branch then becomes
        /// eligible for cleanup on the next merge-queue tick that touches the entry.
        /// </summary>
        /// <remarks>
        /// Idempotent: entries already Landed or Cancelled are skipped. Safe to call
        /// from the admiral health-check loop alongside
        /// <see cref="IAdmiralService.HealthCheckAsync"/>.
        /// </remarks>
        public async Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default)
        {
            int reconciledCount = 0;
            List<MergeEntry> open;
            try
            {
                open = await _Database.MergeEntries.EnumerateByStatusAsync(MergeStatusEnum.PullRequestOpen, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "PR reconciler: enumeration failed: " + ex.Message);
                return 0;
            }

            foreach (MergeEntry entry in open)
            {
                if (String.IsNullOrEmpty(entry.MissionId)) continue;
                Mission? mission = null;
                try
                {
                    mission = await _Database.Missions.ReadAsync(entry.MissionId, token).ConfigureAwait(false);
                }
                catch { /* best-effort; skip on read errors */ }
                if (mission == null) continue;
                if (mission.Status != MissionStatusEnum.Complete) continue;

                entry.Status = MergeStatusEnum.Landed;
                entry.CompletedUtc = DateTime.UtcNow;
                entry.LastUpdateUtc = DateTime.UtcNow;
                try
                {
                    await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                    await UpdateLandingJobFromEntryAsync(entry, null, token).ConfigureAwait(false);
                    reconciledCount++;
                    _Logging.Info(_Header + "PR reconciler: entry " + entry.Id + " landed (linked mission " + mission.Id + " merged)");
                    FireIndexRefreshForVessel(entry.VesselId);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "PR reconciler: failed to land entry " + entry.Id + ": " + ex.Message);
                }
            }

            return reconciledCount;
        }

        /// <summary>
        /// Look up the most recent merge entry for an upstream mission. Used by the
        /// PR-fallback gate (chained-PR base resolution) and by the on-land rebase check.
        /// Returns null when the mission has no merge entries yet. The implementation
        /// scans all entries -- typical volume is one entry per mission so the cost is
        /// negligible; revisit if merge_queue grows large.
        /// </summary>
        private async Task<MergeEntry?> ReadMostRecentMergeEntryAsync(string? missionId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(missionId)) return null;
            try
            {
                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                MergeEntry? mostRecent = null;
                foreach (MergeEntry entry in entries)
                {
                    if (!String.Equals(entry.MissionId, missionId, StringComparison.Ordinal)) continue;
                    if (mostRecent == null || entry.CreatedUtc > mostRecent.CreatedUtc) mostRecent = entry;
                }
                return mostRecent;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> FindProtectedPathViolationAsync(MergeEntry entry, string worktreePath, CancellationToken token)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (String.IsNullOrEmpty(worktreePath)) return null;

            List<string> changedFiles = await CollectChangedFilesAgainstTargetAsync(
                worktreePath,
                entry.TargetBranch,
                token).ConfigureAwait(false);

            Vessel? vessel = await ReadEntryVesselAsync(entry, token).ConfigureAwait(false);
            return ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changedFiles, vessel?.ProtectedPaths);
        }

        private async Task<Vessel?> ReadEntryVesselAsync(MergeEntry entry, CancellationToken token)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (String.IsNullOrEmpty(entry.VesselId)) return null;

            try
            {
                return !String.IsNullOrEmpty(entry.TenantId)
                    ? await _Database.Vessels.ReadAsync(entry.TenantId, entry.VesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(entry.VesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read vessel " + entry.VesselId + " for protected-path validation: " + ex.Message);
                return null;
            }
        }

        private async Task<List<string>> CollectChangedFilesAgainstTargetAsync(string worktreePath, string targetBranch, CancellationToken token)
        {
            List<string> results = new List<string>();
            if (String.IsNullOrEmpty(worktreePath) || String.IsNullOrEmpty(targetBranch)) return results;

            try
            {
                GitProcessResult diff = await RunGitCapturingAsync(
                    worktreePath,
                    token,
                    "diff",
                    "--name-only",
                    targetBranch + "...HEAD").ConfigureAwait(false);
                if (String.IsNullOrEmpty(diff.StandardOutput)) return results;

                string[] lines = diff.StandardOutput.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (!String.IsNullOrEmpty(trimmed)) results.Add(trimmed);
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "protected-path validation could not collect changed files in " + worktreePath + ": " + ex.Message);
            }

            return results;
        }

        private async Task TransitionEntryToFailureAsync(MergeEntry entry, string reason, CancellationToken token, bool fireRecovery = true)
        {
            entry.Status = MergeStatusEnum.Failed;
            entry.TestOutput = reason;
            entry.CompletedUtc = DateTime.UtcNow;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            await UpdateLandingJobFromEntryAsync(entry, reason, token).ConfigureAwait(false);
            await ReconcileMissionStatusAsync(entry.MissionId, MissionStatusEnum.LandingFailed, reason, token, entry.TenantId).ConfigureAwait(false);
            if (fireRecovery)
            {
                FireRecoveryHandlerForEntry(entry.Id);
            }
        }

        /// <summary>
        /// Fire-and-forget invocation of the recovery handler for a freshly Failed entry.
        /// The recovery loop runs on a background task so the merge-queue tick is not
        /// blocked by classification, redispatch persistence, or PR-fallback I/O.
        /// </summary>
        private void FireRecoveryHandlerForEntry(string entryId)
        {
            IMergeRecoveryHandler? handler = _RecoveryHandler;
            if (handler == null) return;
            if (String.IsNullOrEmpty(entryId)) return;

            string capturedEntryId = entryId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler.OnMergeFailedAsync(capturedEntryId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "recovery handler raised for entry " + capturedEntryId + ": " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Fire-and-forget code index refresh for a vessel after a successful land.
        /// Does not block the merge-queue tick.
        /// </summary>
        private void FireIndexRefreshForVessel(string? vesselId)
        {
            CodeIndexRefreshScheduler.Schedule(
                _CodeIndexService,
                _Settings.CodeIndex,
                _Logging,
                _Header,
                vesselId,
                "merge queue landed entry");
        }

        /// <summary>
        /// Land a single entry by pushing the integration branch to the target.
        /// </summary>
        private async Task LandEntryAsync(MergeEntry entry, string repoPath, string integrationBranch, CancellationToken token)
        {
            string preLandRemoteTargetHead = "";
            string integrationHead = "";
            try
            {
                preLandRemoteTargetHead = await CaptureRemoteTargetHeadAsync(repoPath, entry.TargetBranch, token).ConfigureAwait(false);
                integrationHead = await ResolveCommitAsync(repoPath, integrationBranch, token).ConfigureAwait(false);
                bool alreadyPushed = String.Equals(preLandRemoteTargetHead, integrationHead, StringComparison.OrdinalIgnoreCase);
                if (!alreadyPushed)
                {
                    await _Git.PushRefSpecAsync(repoPath, integrationBranch, entry.TargetBranch, token).ConfigureAwait(false);
                }
                else
                {
                    _Logging.Info(_Header + "push already reflected on origin/" + entry.TargetBranch + " for " + entry.Id);
                }

                await SynchronizeTargetBranchAfterPushAsync(entry, repoPath, entry.TargetBranch, token).ConfigureAwait(false);

                entry.Status = MergeStatusEnum.Landed;
                entry.CompletedUtc = DateTime.UtcNow;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                await UpdateLandingJobFromEntryAsync(entry, null, token).ConfigureAwait(false);
                _Logging.Info(_Header + "landed " + entry.Id + " branch " + entry.BranchName);
                FireIndexRefreshForVessel(entry.VesselId);

                if (entry.AuditDeepPicked.HasValue && !string.IsNullOrEmpty(entry.VesselId))
                {
                    string vesselIdForCalibration = entry.VesselId;
                    try
                    {
                        await _Database.Vessels.IncrementCalibrationCounterAsync(vesselIdForCalibration, token).ConfigureAwait(false);
                    }
                    catch (Exception incEx)
                    {
                        _Logging.Warn(_Header + "calibration counter increment failed for vessel " + vesselIdForCalibration + ": " + incEx.Message);
                    }
                }

                // Reconcile linked mission to Complete
                await ReconcileMissionStatusAsync(entry.MissionId, MissionStatusEnum.Complete,
                    "Landed via merge queue entry " + entry.Id, token, entry.TenantId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to land " + entry.Id + ": " + ex.Message);
                await RollbackTargetIfAdvancedAsync(entry, repoPath, preLandRemoteTargetHead, integrationHead, ex.Message, token).ConfigureAwait(false);
                entry.Status = MergeStatusEnum.Failed;
                entry.TestOutput = "Landing failed: " + ex.Message;
                entry.CompletedUtc = DateTime.UtcNow;
                entry.LastUpdateUtc = DateTime.UtcNow;
                await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
                await UpdateLandingJobFromEntryAsync(entry, entry.TestOutput, token).ConfigureAwait(false);

                // Reconcile linked mission to LandingFailed
                await ReconcileMissionStatusAsync(entry.MissionId, MissionStatusEnum.LandingFailed,
                    "Merge queue landing failed: " + ex.Message, token, entry.TenantId).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Capture the remote target head before landing so a later failed land
        /// can restore the branch to its original commit.
        /// </summary>
        private async Task<string> CaptureRemoteTargetHeadAsync(string repoPath, string targetBranch, CancellationToken token)
        {
            await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);

            GitProcessResult result = await RunGitCapturingAsync(repoPath, token, "rev-parse", "--verify", "refs/remotes/origin/" + targetBranch).ConfigureAwait(false);
            return result.StandardOutput.Trim();
        }

        /// <summary>
        /// Restore the target branch if a failed landing attempt already pushed
        /// the integration head to origin.
        /// </summary>
        private async Task RollbackTargetIfAdvancedAsync(MergeEntry entry, string repoPath, string preLandRemoteTargetHead, string integrationHead, string reason, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(preLandRemoteTargetHead))
            {
                _Logging.Warn(_Header + "skipping target rollback for " + entry.Id + " because origin/" + entry.TargetBranch + " had no captured pre-land head");
                return;
            }

            if (String.IsNullOrWhiteSpace(integrationHead))
            {
                _Logging.Warn(_Header + "skipping target rollback for " + entry.Id + " because integration head was not captured");
                return;
            }

            string currentHead = "";
            try
            {
                await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);
                GitProcessResult currentResult = await RunGitCapturingAsync(repoPath, token, "rev-parse", "--verify", "refs/remotes/origin/" + entry.TargetBranch).ConfigureAwait(false);
                currentHead = currentResult.StandardOutput.Trim();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not inspect origin/" + entry.TargetBranch + " for failed landing rollback on " + entry.Id + ": " + ex.Message);
                return;
            }

            if (String.IsNullOrWhiteSpace(currentHead) ||
                String.Equals(currentHead, preLandRemoteTargetHead, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!String.Equals(currentHead, integrationHead, StringComparison.OrdinalIgnoreCase))
            {
                _Logging.Warn(_Header + "skipping target rollback for " + entry.Id + " because origin/" + entry.TargetBranch + " advanced to " + currentHead + " instead of integration head " + integrationHead);
                return;
            }

            string summary = "failed landing advanced origin/" + entry.TargetBranch + " for " + entry.Id +
                " from " + preLandRemoteTargetHead + " to " + currentHead + "; rolling back target";

            _Logging.Warn(_Header + summary + ": " + reason);

            string rollbackResult = "";
            try
            {
                await RunGitAsync(repoPath, token, "push", "origin", "--force", preLandRemoteTargetHead + ":refs/heads/" + entry.TargetBranch).ConfigureAwait(false);
                await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);

                if (!await IsBranchCheckedOutInWorktreeAsync(repoPath, entry.TargetBranch, token).ConfigureAwait(false))
                {
                    await RunGitAsync(repoPath, token, "branch", "-f", entry.TargetBranch, "refs/remotes/origin/" + entry.TargetBranch).ConfigureAwait(false);
                }

                string remoteHead = await ResolveCommitAsync(repoPath, "refs/remotes/origin/" + entry.TargetBranch, token).ConfigureAwait(false);
                if (!String.Equals(remoteHead, preLandRemoteTargetHead, StringComparison.OrdinalIgnoreCase))
                {
                    rollbackResult = "rollback_failed: remote target remains at " + remoteHead;
                }
                else
                {
                    string localHead = await ResolveCommitAsync(repoPath, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
                    if (!String.Equals(localHead, preLandRemoteTargetHead, StringComparison.OrdinalIgnoreCase))
                    {
                        rollbackResult = "partial_rollback: local target remains at " + localHead;
                    }
                    else
                    {
                        rollbackResult = "rolled_back";
                    }
                }
            }
            catch (Exception ex)
            {
                rollbackResult = "rollback_failed: " + ex.Message;
                _Logging.Warn(_Header + "target rollback failed for " + entry.Id + " on " + entry.TargetBranch + ": " + ex.Message);
            }

            await EmitFailedTargetAdvancedEventAsync(entry, preLandRemoteTargetHead, currentHead, integrationHead, rollbackResult, reason, summary, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Emit an audit event when a failed merge-queue land already advanced
        /// the target branch before the failure was observed.
        /// </summary>
        private async Task EmitFailedTargetAdvancedEventAsync(MergeEntry entry, string preLandRemoteTargetHead, string advancedHead, string integrationHead, string rollbackResult, string reason, string summary, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent();
                evt.TenantId = entry.TenantId;
                evt.EventType = "merge_queue.failed_target_advanced";
                evt.EntityType = "merge_entry";
                evt.EntityId = entry.Id;
                evt.MissionId = entry.MissionId;
                evt.VesselId = entry.VesselId;
                evt.Message = summary;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    entryId = entry.Id,
                    missionId = entry.MissionId,
                    vesselId = entry.VesselId,
                    targetBranch = entry.TargetBranch,
                    previousTargetHead = preLandRemoteTargetHead,
                    advancedTargetHead = advancedHead,
                    integrationHead,
                    rollbackResult,
                    reason
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record target-advanced audit event for " + entry.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// After a successful push, make the bare repository's local target branch
        /// match the fetched remote target before advertising the merge as landed.
        /// When the target branch is checked out in a worktree, the update is skipped
        /// and a structured event is emitted rather than failing the land.
        /// </summary>
        private async Task SynchronizeTargetBranchAfterPushAsync(MergeEntry entry, string repoPath, string targetBranch, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(repoPath)) throw new ArgumentNullException(nameof(repoPath));
            if (String.IsNullOrWhiteSpace(targetBranch)) throw new ArgumentNullException(nameof(targetBranch));

            await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);

            string remoteRef = "refs/remotes/origin/" + targetBranch;
            string localRef = "refs/heads/" + targetBranch;
            string remoteHead = (await RunGitCapturingAsync(repoPath, token, "rev-parse", "--verify", remoteRef).ConfigureAwait(false)).StandardOutput.Trim();
            if (String.IsNullOrWhiteSpace(remoteHead))
            {
                throw new InvalidOperationException("Unable to verify remote target branch " + remoteRef + " after push");
            }

            if (await IsBranchCheckedOutInWorktreeAsync(repoPath, targetBranch, token).ConfigureAwait(false))
            {
                _Logging.Warn(_Header + "skipping local target ref sync for " + targetBranch + " because it is checked out in a worktree");
                await EmitTargetRefSyncSkippedAsync(entry, targetBranch, "branch_checked_out_in_worktree", token).ConfigureAwait(false);
                return;
            }

            await RunGitAsync(repoPath, token, "branch", "-f", targetBranch, remoteRef).ConfigureAwait(false);

            string localHead = (await RunGitCapturingAsync(repoPath, token, "rev-parse", "--verify", localRef).ConfigureAwait(false)).StandardOutput.Trim();
            if (!String.Equals(localHead, remoteHead, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Post-push target sync failed for " + targetBranch + ": local " + localHead + " != origin " + remoteHead);
            }

            _Logging.Info(_Header + "synced local target branch " + targetBranch + " to origin/" + targetBranch + " at " + localHead);
        }

        private async Task<string> ResolveCommitAsync(string workingDir, string refName, CancellationToken token)
        {
            GitProcessResult result = await RunGitCapturingAsync(workingDir, token, "rev-parse", "--verify", refName).ConfigureAwait(false);
            string commit = result.StandardOutput.Trim();
            if (String.IsNullOrWhiteSpace(commit))
            {
                throw new InvalidOperationException("Unable to resolve git ref " + refName + " in " + workingDir);
            }

            return commit;
        }

        private async Task<bool> IsAncestorAsync(string workingDir, string ancestorRef, string descendantRef, CancellationToken token)
        {
            GitProcessResult result = await RunGitCapturingAsync(workingDir, token, "merge-base", "--is-ancestor", ancestorRef, descendantRef).ConfigureAwait(false);
            return result.ExitCode == 0;
        }

        private async Task MarkExistingPullRequestOpenAsync(MergeEntry entry, CancellationToken token)
        {
            entry.Status = MergeStatusEnum.PullRequestOpen;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await _Database.MergeEntries.UpdateAsync(entry, token).ConfigureAwait(false);
            await UpdateLandingJobFromEntryAsync(entry, null, token).ConfigureAwait(false);

            if (String.IsNullOrEmpty(entry.MissionId)) return;

            Mission? mission = !String.IsNullOrEmpty(entry.TenantId)
                ? await _Database.Missions.ReadAsync(entry.TenantId, entry.MissionId, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(entry.MissionId, token).ConfigureAwait(false);
            if (mission == null) return;

            mission.Status = MissionStatusEnum.PullRequestOpen;
            mission.PrUrl = entry.PrUrl;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete the captain branch (per vessel BranchCleanupPolicy) and the integration branch (always)
        /// after a successful merge-queue land. Branch deletion failures are swallowed -- stale branches
        /// are better than a failed entry.
        /// </summary>
        private async Task CleanupLandedBranchesAsync(MergeEntry entry, string repoPath, string integrationBranch, CancellationToken token)
        {
            Vessel? vessel = null;
            if (!String.IsNullOrEmpty(entry.VesselId))
            {
                try
                {
                    vessel = !String.IsNullOrEmpty(entry.TenantId)
                        ? await _Database.Vessels.ReadAsync(entry.TenantId, entry.VesselId, token).ConfigureAwait(false)
                        : await _Database.Vessels.ReadAsync(entry.VesselId, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not read vessel " + entry.VesselId + " for branch cleanup: " + ex.Message);
                }
            }

            BranchCleanupPolicyEnum cleanupPolicy = vessel?.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;

            if (!String.IsNullOrEmpty(entry.BranchName))
            {
                if (cleanupPolicy == BranchCleanupPolicyEnum.None)
                {
                    _Logging.Info(_Header + "branch cleanup policy is None - retaining captain branch " + entry.BranchName);
                }
                else
                {
                    try
                    {
                        await _Git.DeleteLocalBranchAsync(repoPath, entry.BranchName, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "deleted captain branch " + entry.BranchName + " from bare repo after land");
                    }
                    catch (Exception ex)
                    {
                        _Logging.Debug(_Header + "could not delete captain branch " + entry.BranchName + " from bare repo: " + ex.Message);
                    }

                    if (cleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote && !String.IsNullOrEmpty(vessel?.WorkingDirectory))
                    {
                        try
                        {
                            await _Git.DeleteRemoteBranchAsync(vessel.WorkingDirectory, entry.BranchName, token).ConfigureAwait(false);
                            _Logging.Info(_Header + "deleted remote captain branch " + entry.BranchName + " after land");
                        }
                        catch (Exception ex)
                        {
                            _Logging.Warn(_Header + "could not delete remote captain branch " + entry.BranchName + ": " + ex.Message);
                        }
                    }
                }
            }

            // Integration branch is admiral-internal scaffolding; always remove from bare repo (never pushed to remote).
            if (!String.IsNullOrEmpty(integrationBranch))
            {
                try
                {
                    await _Git.DeleteLocalBranchAsync(repoPath, integrationBranch, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "deleted integration branch " + integrationBranch + " from bare repo");
                }
                catch (Exception ex)
                {
                    _Logging.Debug(_Header + "could not delete integration branch " + integrationBranch + " from bare repo: " + ex.Message);
                }
            }

            await SyncWorkingDirectoryAfterLandAsync(entry, vessel, token).ConfigureAwait(false);
            await RestoreBareHeadAsync(entry, vessel, repoPath, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Reconcile the linked mission status after a merge queue entry reaches a terminal state.
        /// </summary>
        private async Task ReconcileMissionStatusAsync(string? missionId, MissionStatusEnum targetStatus, string reason, CancellationToken token, string? tenantId = null)
        {
            if (String.IsNullOrEmpty(missionId)) return;

            try
            {
                Mission? mission = !String.IsNullOrEmpty(tenantId)
                    ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                    : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                if (mission == null) return;

                // Only update if the mission is not already in a terminal state
                if (mission.Status == MissionStatusEnum.Complete ||
                    mission.Status == MissionStatusEnum.Failed ||
                    mission.Status == MissionStatusEnum.Cancelled)
                {
                    return;
                }

                mission.Status = targetStatus;
                mission.LastUpdateUtc = DateTime.UtcNow;
                if (targetStatus == MissionStatusEnum.Complete)
                    mission.CompletedUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + "reconciled mission " + missionId + " to " + targetStatus + ": " + reason);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to reconcile mission " + missionId + " to " + targetStatus + ": " + ex.Message);
            }
        }

        private async Task<MergeAttemptResult> MergeBranchAsync(string worktreePath, string branchName, CancellationToken token)
        {
            GitProcessResult mergeResult = await RunGitCapturingAsync(worktreePath, token, "merge", "--no-ff", branchName).ConfigureAwait(false);
            if (mergeResult.ExitCode == 0)
            {
                return new MergeAttemptResult(true, mergeResult.ExitCode, mergeResult.StandardOutput, mergeResult.StandardError);
            }

            // Abort the failed merge so the worktree is in a clean state for cleanup.
            try { await RunGitCapturingAsync(worktreePath, token, "merge", "--abort").ConfigureAwait(false); }
            catch { }
            return new MergeAttemptResult(false, mergeResult.ExitCode, mergeResult.StandardOutput, mergeResult.StandardError);
        }

        /// <summary>
        /// Capture conflicted-file paths reported by git after a failed merge. Returns an
        /// empty list when git reports nothing or the call fails. Best-effort -- the
        /// classifier degrades gracefully when the list is empty.
        /// </summary>
        private async Task<List<string>> CollectConflictedFilesAsync(string worktreePath, CancellationToken token)
        {
            List<string> results = new List<string>();
            try
            {
                GitProcessResult diff = await RunGitCapturingAsync(worktreePath, token, "diff", "--name-only", "--diff-filter=U").ConfigureAwait(false);
                if (String.IsNullOrEmpty(diff.StandardOutput)) return results;
                string[] lines = diff.StandardOutput.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (!String.IsNullOrEmpty(trimmed)) results.Add(trimmed);
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// Compute the total changed line count in the captain branch's diff against the
        /// target branch, parsed from <c>git diff --shortstat</c> output. Returns 0 on
        /// any failure -- the router's triviality heuristic treats 0 as "unknown size".
        /// </summary>
        private async Task<int> ComputeDiffLineCountAsync(string worktreePath, string targetBranch, string captainBranch, CancellationToken token)
        {
            try
            {
                GitProcessResult shortstat = await RunGitCapturingAsync(worktreePath, token, "diff", "--shortstat", targetBranch + "..." + captainBranch).ConfigureAwait(false);
                return ParseShortstatLineCount(shortstat.StandardOutput);
            }
            catch
            {
                return 0;
            }
        }

        private static int ParseShortstatLineCount(string? shortstat)
        {
            if (String.IsNullOrEmpty(shortstat)) return 0;
            int total = 0;
            int idx = 0;
            while (idx < shortstat.Length)
            {
                int digitStart = -1;
                while (idx < shortstat.Length && !Char.IsDigit(shortstat[idx])) idx++;
                if (idx >= shortstat.Length) break;
                digitStart = idx;
                while (idx < shortstat.Length && Char.IsDigit(shortstat[idx])) idx++;
                if (digitStart >= 0 && idx > digitStart)
                {
                    int n;
                    if (Int32.TryParse(shortstat.Substring(digitStart, idx - digitStart), out n))
                    {
                        // Skip whitespace, then peek the unit token.
                        int after = idx;
                        while (after < shortstat.Length && shortstat[after] == ' ') after++;
                        if (after < shortstat.Length)
                        {
                            char tag = shortstat[after];
                            if (tag == 'i' || tag == 'd') total += n;
                        }
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// Run the classifier on <paramref name="context"/> and write its result onto
        /// the merge entry. MUST be called BEFORE setting <c>entry.Status = Failed</c>
        /// so the auto-recovery handler can read a populated classification when the
        /// status-change event fires. Best-effort: a classifier exception leaves the
        /// merge entry's classification fields null and lets the failure persist.
        /// </summary>
        private void ApplyClassification(MergeEntry entry, MergeFailureContext context)
        {
            try
            {
                MergeFailureClassification classification = _Classifier.Classify(context);
                entry.MergeFailureClass = classification.FailureClass;
                entry.ConflictedFiles = JsonSerializer.Serialize(classification.ConflictedFiles);
                entry.MergeFailureSummary = classification.Summary;
                entry.DiffLineCount = context.DiffLineCount;
                _Logging.Info(_Header + "classified " + entry.Id + " as " + classification.FailureClass + ": " + classification.Summary);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "classifier threw for " + entry.Id + ": " + ex.Message);
            }
        }

        private async Task<TestResult> RunTestsAsync(string workingDir, string testCommand, CancellationToken token)
        {
            _Logging.Info(_Header + "running tests: " + testCommand + " in " + workingDir);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = GetShell(),
                Arguments = GetShellArgs(testCommand),
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();

                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

                await process.WaitForExitAsync(token).ConfigureAwait(false);

                string output = stdout;
                if (!String.IsNullOrEmpty(stderr))
                    output += "\n--- STDERR ---\n" + stderr;

                return new TestResult(process.ExitCode, output);
            }
        }

        private async Task RunGitAsync(string workingDir, CancellationToken token, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            string argsDisplay = String.Join(" ", args);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();

                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync(token).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git " + argsDisplay + " failed: " + stderr);
                }
            }
        }

        /// <summary>
        /// Run git and capture exit code, stdout, and stderr without throwing on
        /// non-zero exit. Used by the merge-attempt path so the classifier sees
        /// the raw failure signal instead of a wrapped exception message.
        /// </summary>
        private async Task<GitProcessResult> RunGitCapturingAsync(string workingDir, CancellationToken token, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);

                return new GitProcessResult(process.ExitCode, stdout, stderr);
            }
        }

        private async Task<bool> IsBranchCheckedOutInWorktreeAsync(string repoPath, string branchName, CancellationToken token)
        {
            GitProcessResult result = await RunGitCapturingAsync(repoPath, token, "worktree", "list", "--porcelain").ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("git worktree list failed: " + result.StandardError.Trim());
            }

            string targetRef = "branch refs/heads/" + branchName;
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (String.Equals(line.Trim(), targetRef, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);

        private sealed record MergeAttemptResult(bool Ok, int GitExitCode, string StandardOutput, string StandardError);

        private async Task CleanupWorktreeAsync(string worktreePath, CancellationToken token)
        {
            try
            {
                await _Git.RemoveWorktreeAsync(worktreePath, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "cleanup error for " + worktreePath + ": " + ex.Message);
            }
        }

        private async Task<string?> GetRepoPathAsync(MergeEntry entry, CancellationToken token)
        {
            if (!String.IsNullOrEmpty(entry.VesselId))
            {
                Vessel? vessel = !String.IsNullOrEmpty(entry.TenantId)
                    ? await _Database.Vessels.ReadAsync(entry.TenantId, entry.VesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(entry.VesselId, token).ConfigureAwait(false);
                if (vessel == null)
                {
                    _Logging.Warn(_Header + "vessel not found for vessel ID " + entry.VesselId);
                    return null;
                }
                if (!String.IsNullOrEmpty(vessel.LocalPath))
                    return vessel.LocalPath;

                // Fallback to default path, same as DockService
                string defaultPath = Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
                _Logging.Warn(_Header + "vessel LocalPath is empty for vessel " + vessel.Name + ", falling back to default: " + defaultPath);
                return defaultPath;
            }
            return _Settings.ReposDirectory;
        }

        private string GetShell()
        {
            if (OperatingSystem.IsWindows()) return "cmd.exe";
            // Use /bin/sh (POSIX-guaranteed) instead of /bin/bash which may
            // not exist on Alpine, minimal containers, or some Linux distros.
            return "/bin/sh";
        }

        private string GetShellArgs(string command)
        {
            if (OperatingSystem.IsWindows()) return "/c " + command;
            return "-c \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private string TruncateOutput(string output)
        {
            if (String.IsNullOrEmpty(output)) return "";
            if (output.Length > 4096) return output.Substring(0, 4096) + "\n... (truncated)";
            return output;
        }

        /// <summary>
        /// Fast-forward the vessel WorkingDirectory to the landed commit when it is
        /// clean and checked out on the configured default branch.
        /// Emits merge_queue.workdir_synced on success or merge_queue.workdir_sync_skipped
        /// with a structured reason when any precondition is not met.
        /// </summary>
        private async Task SyncWorkingDirectoryAfterLandAsync(MergeEntry entry, Vessel? vessel, CancellationToken token)
        {
            string? workingDirectory = vessel?.WorkingDirectory;
            if (String.IsNullOrEmpty(workingDirectory))
            {
                return;
            }

            if (!Directory.Exists(workingDirectory))
            {
                await EmitWorkdirSyncSkippedAsync(entry, workingDirectory, "directory_missing", token).ConfigureAwait(false);
                return;
            }

            if (!await _Git.IsRepositoryAsync(workingDirectory, token).ConfigureAwait(false))
            {
                await EmitWorkdirSyncSkippedAsync(entry, workingDirectory, "not_a_repository", token).ConfigureAwait(false);
                return;
            }

            string expectedBranch = !String.IsNullOrEmpty(vessel?.DefaultBranch) ? vessel!.DefaultBranch : entry.TargetBranch;
            string? currentBranch = null;
            try
            {
                currentBranch = await _Git.GetCurrentBranchAsync(workingDirectory, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not determine current branch of WorkingDirectory " + workingDirectory + ": " + ex.Message);
                await EmitWorkdirSyncSkippedAsync(entry, workingDirectory, "branch_check_failed", token).ConfigureAwait(false);
                return;
            }

            if (!String.Equals(currentBranch, expectedBranch, StringComparison.OrdinalIgnoreCase))
            {
                _Logging.Warn(_Header + "skipping WorkingDirectory sync for " + workingDirectory + ": on branch " + currentBranch + " not " + expectedBranch);
                await EmitWorkdirSyncSkippedAsync(entry, workingDirectory, "on_non_default_branch", token).ConfigureAwait(false);
                return;
            }

            bool isClean = false;
            try
            {
                isClean = await _Git.IsWorkingDirectoryCleanAsync(workingDirectory, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not check cleanliness of WorkingDirectory " + workingDirectory + ": " + ex.Message);
                await EmitWorkdirSyncSkippedAsync(entry, workingDirectory, "clean_check_failed", token).ConfigureAwait(false);
                return;
            }

            if (!isClean)
            {
                _Logging.Warn(_Header + "skipping WorkingDirectory sync for " + workingDirectory + ": uncommitted changes present");
                await EmitWorkdirSyncSkippedAsync(entry, workingDirectory, "dirty_working_directory", token).ConfigureAwait(false);
                return;
            }

            try
            {
                await _Git.PullFastForwardOnlyAsync(workingDirectory, token).ConfigureAwait(false);
                _Logging.Info(_Header + "fast-forwarded WorkingDirectory " + workingDirectory + " to " + expectedBranch + " after land of " + entry.Id);
                await EmitWorkdirSyncedAsync(entry, workingDirectory, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "WorkingDirectory fast-forward failed for " + workingDirectory + ": " + ex.Message);
                await EmitWorkdirSyncSkippedAsync(entry, workingDirectory, "fast_forward_failed", token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Restore the bare repository HEAD symbolic-ref to the default branch after
        /// captain and integration branches are cleaned up. Emits merge_queue.bare_head_restored
        /// on success or merge_queue.bare_head_restore_skipped when the operation cannot run.
        /// </summary>
        private async Task RestoreBareHeadAsync(MergeEntry entry, Vessel? vessel, string repoPath, CancellationToken token)
        {
            string defaultBranch = !String.IsNullOrEmpty(vessel?.DefaultBranch) ? vessel!.DefaultBranch : entry.TargetBranch;
            if (String.IsNullOrEmpty(defaultBranch) || String.IsNullOrEmpty(repoPath))
            {
                return;
            }

            try
            {
                await _Git.SetHeadSymbolicRefAsync(repoPath, "refs/heads/" + defaultBranch, token).ConfigureAwait(false);
                _Logging.Info(_Header + "restored bare repo HEAD to refs/heads/" + defaultBranch + " after land of " + entry.Id);
                await EmitBareHeadRestoredAsync(entry, repoPath, defaultBranch, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "bare repo HEAD restore failed for " + repoPath + " after land of " + entry.Id + ": " + ex.Message);
                await EmitBareHeadRestoreSkippedAsync(entry, repoPath, defaultBranch, ex.Message, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Emit a structured event when the local target ref sync is skipped because
        /// the branch is checked out in a worktree.
        /// </summary>
        private async Task EmitTargetRefSyncSkippedAsync(MergeEntry entry, string targetBranch, string reason, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent();
                evt.TenantId = entry.TenantId;
                evt.EventType = "merge_queue.target_ref_sync_skipped";
                evt.EntityType = "merge_entry";
                evt.EntityId = entry.Id;
                evt.MissionId = entry.MissionId;
                evt.VesselId = entry.VesselId;
                evt.Message = "Local target ref sync skipped for " + targetBranch + ": " + reason;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    entryId = entry.Id,
                    missionId = entry.MissionId,
                    vesselId = entry.VesselId,
                    targetBranch,
                    reason
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record target-ref-sync-skipped event for " + entry.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Emit a structured event when the WorkingDirectory is successfully fast-forwarded
        /// after a successful land.
        /// </summary>
        private async Task EmitWorkdirSyncedAsync(MergeEntry entry, string workingDirectory, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent();
                evt.TenantId = entry.TenantId;
                evt.EventType = "merge_queue.workdir_synced";
                evt.EntityType = "merge_entry";
                evt.EntityId = entry.Id;
                evt.MissionId = entry.MissionId;
                evt.VesselId = entry.VesselId;
                evt.Message = "WorkingDirectory fast-forwarded after land of " + entry.Id;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    entryId = entry.Id,
                    missionId = entry.MissionId,
                    vesselId = entry.VesselId,
                    targetBranch = entry.TargetBranch,
                    workingDirectory
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record workdir-synced event for " + entry.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Emit a structured event when the WorkingDirectory sync is skipped due to
        /// a precondition not being met (missing, dirty, wrong branch, or ff failure).
        /// </summary>
        private async Task EmitWorkdirSyncSkippedAsync(MergeEntry entry, string workingDirectory, string reason, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent();
                evt.TenantId = entry.TenantId;
                evt.EventType = "merge_queue.workdir_sync_skipped";
                evt.EntityType = "merge_entry";
                evt.EntityId = entry.Id;
                evt.MissionId = entry.MissionId;
                evt.VesselId = entry.VesselId;
                evt.Message = "WorkingDirectory sync skipped for " + entry.Id + ": " + reason;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    entryId = entry.Id,
                    missionId = entry.MissionId,
                    vesselId = entry.VesselId,
                    targetBranch = entry.TargetBranch,
                    workingDirectory,
                    reason
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record workdir-sync-skipped event for " + entry.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Emit a structured event when the bare repo HEAD is restored to the default branch
        /// after branch cleanup.
        /// </summary>
        private async Task EmitBareHeadRestoredAsync(MergeEntry entry, string repoPath, string defaultBranch, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent();
                evt.TenantId = entry.TenantId;
                evt.EventType = "merge_queue.bare_head_restored";
                evt.EntityType = "merge_entry";
                evt.EntityId = entry.Id;
                evt.MissionId = entry.MissionId;
                evt.VesselId = entry.VesselId;
                evt.Message = "Bare repo HEAD restored to refs/heads/" + defaultBranch + " after land of " + entry.Id;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    entryId = entry.Id,
                    missionId = entry.MissionId,
                    vesselId = entry.VesselId,
                    targetBranch = entry.TargetBranch,
                    defaultBranch,
                    repoPath
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record bare-head-restored event for " + entry.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Emit a structured event when the bare repo HEAD restore fails or is skipped.
        /// </summary>
        private async Task EmitBareHeadRestoreSkippedAsync(MergeEntry entry, string repoPath, string defaultBranch, string reason, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent();
                evt.TenantId = entry.TenantId;
                evt.EventType = "merge_queue.bare_head_restore_skipped";
                evt.EntityType = "merge_entry";
                evt.EntityId = entry.Id;
                evt.MissionId = entry.MissionId;
                evt.VesselId = entry.VesselId;
                evt.Message = "Bare repo HEAD restore skipped for " + entry.Id + ": " + reason;
                evt.Payload = JsonSerializer.Serialize(new
                {
                    entryId = entry.Id,
                    missionId = entry.MissionId,
                    vesselId = entry.VesselId,
                    targetBranch = entry.TargetBranch,
                    defaultBranch,
                    repoPath,
                    reason
                });
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record bare-head-restore-skipped event for " + entry.Id + ": " + ex.Message);
            }
        }

        #endregion
    }
}
