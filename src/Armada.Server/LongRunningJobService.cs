namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Tracks bounded background jobs independently of initiating requests.
    /// </summary>
    /// <remarks>
    /// With a journal directory every job is written to disk before <see cref="Start"/> returns and
    /// again at each transition, so an accepted job outlives the process that accepted it. A job the
    /// previous process never finished is recorded as <see cref="LongRunningJobStatusEnum.Lost"/> by
    /// <see cref="RecoverInterruptedJobsAsync"/> at the next start: its status stays readable and the
    /// failure callback names it. A job is never resumed, because its operation is a closure over a
    /// request that ended with the process.
    /// </remarks>
    public class LongRunningJobService
    {
        #region Public-Members

        /// <summary>
        /// Failure message recorded on a job whose accepting process stopped before it finished.
        /// </summary>
        public const string LostMessage =
            "job_lost_on_restart: the admiral process that accepted this job stopped (a restart or a crash) before the job finished. " +
            "The operation did not complete and nothing will resume it. Check whether its effect exists, then submit it again.";

        /// <summary>
        /// How long a finished job's journal record is kept for status reads.
        /// </summary>
        public static readonly TimeSpan JournalRetention = TimeSpan.FromDays(14);

        /// <summary>
        /// Journal directory, or null when jobs are held in memory only.
        /// </summary>
        public string? JournalDirectory
        {
            get { return _JournalDirectory; }
        }

        /// <summary>
        /// Number of journal writes or reads that failed since start. Each one is also reported
        /// through the warning callback.
        /// </summary>
        public int JournalFailures
        {
            get { return _JournalFailures; }
        }

        #endregion

        #region Internal-Members

        /// <summary>
        /// Test seam invoked with the job id after a job's background execution has finished and
        /// recorded (or declined to record) its outcome.
        /// </summary>
        internal Action<string>? ExecutionFinished { get; set; }

        #endregion

        #region Private-Members

        private const int _DefaultMaxRetainedTerminalJobs = 100;
        private const int _MaxFailureMessageLength = 1024;
        private const string _JournalExtension = ".json";

        private static readonly JsonSerializerOptions _JournalJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ConcurrentDictionary<string, LongRunningJob> _Jobs = new ConcurrentDictionary<string, LongRunningJob>(StringComparer.Ordinal);
        private readonly object _EvictionLock = new object();
        private readonly object _JournalLock = new object();
        private readonly object _TransitionLock = new object();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _Executions = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly int _MaxRetainedTerminalJobs;
        private readonly string? _JournalDirectory;
        private readonly Func<LongRunningJob, Task>? _OnJobFailedAsync;
        private readonly Action<string>? _Warn;
        private int _JournalFailures = 0;
        private DateTime _LastPruneUtc = DateTime.MinValue;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Initialize a long-running job tracker.
        /// </summary>
        /// <param name="maxRetainedTerminalJobs">Maximum finished jobs retained in memory for status reads.</param>
        /// <param name="journalDirectory">Directory the job journal is written to; null holds jobs in memory only.</param>
        /// <param name="onJobFailedAsync">Called once for every job that ends Failed or Lost.</param>
        /// <param name="warn">Receives every journal or callback failure, so none is silent.</param>
        public LongRunningJobService(
            int maxRetainedTerminalJobs = _DefaultMaxRetainedTerminalJobs,
            string? journalDirectory = null,
            Func<LongRunningJob, Task>? onJobFailedAsync = null,
            Action<string>? warn = null)
        {
            _MaxRetainedTerminalJobs = Math.Max(1, maxRetainedTerminalJobs);
            _JournalDirectory = String.IsNullOrWhiteSpace(journalDirectory) ? null : journalDirectory;
            _OnJobFailedAsync = onJobFailedAsync;
            _Warn = warn;
            if (_JournalDirectory != null) Directory.CreateDirectory(_JournalDirectory);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Accept an operation for request-independent background execution.
        /// </summary>
        /// <param name="operation">Operation name exposed in status responses.</param>
        /// <param name="operationAsync">Asynchronous operation to execute.</param>
        /// <param name="objectiveId">Objective the job acts for, when known.</param>
        /// <param name="vesselId">Vessel the job acts on, when known.</param>
        /// <returns>An accepted snapshot captured before execution is scheduled.</returns>
        /// <exception cref="InvalidOperationException">The job could not be journalled, so it was not accepted.</exception>
        public LongRunningJob Start(
            string operation,
            Func<CancellationToken, Task<object?>> operationAsync,
            string? objectiveId = null,
            string? vesselId = null)
        {
            if (String.IsNullOrWhiteSpace(operation)) throw new ArgumentException("Operation is required.", nameof(operation));
            if (operationAsync == null) throw new ArgumentNullException(nameof(operationAsync));

            LongRunningJob trackedJob = new LongRunningJob
            {
                JobId = Constants.IdGenerator.GenerateKSortable("job_", 24),
                Operation = operation.Trim(),
                Status = LongRunningJobStatusEnum.Accepted,
                SubmittedAtUtc = DateTime.UtcNow,
                ObjectiveId = String.IsNullOrWhiteSpace(objectiveId) ? null : objectiveId.Trim(),
                VesselId = String.IsNullOrWhiteSpace(vesselId) ? null : vesselId.Trim()
            };

            // Durable before accepted: a job that cannot be journalled is refused here rather than
            // accepted into memory that the next restart erases without a trace.
            try
            {
                WriteJournal(trackedJob);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "job_journal_unavailable: the job was not accepted because its journal record could not be written: " + ex.Message, ex);
            }

            if (!_Jobs.TryAdd(trackedJob.JobId, trackedJob))
                throw new InvalidOperationException("Unable to allocate a unique job identifier.");

            LongRunningJob acceptedSnapshot = trackedJob.CreateSnapshot();
            _ = Task.Run(
                () => ExecuteAsync(trackedJob.JobId, operationAsync),
                CancellationToken.None);

            return acceptedSnapshot;
        }

        /// <summary>
        /// Try to read a defensive snapshot of a job, from memory or, once evicted or after a restart,
        /// from the journal.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="job">Defensive job snapshot when found.</param>
        /// <returns>True when the job is known.</returns>
        public bool TryGetStatus(string jobId, out LongRunningJob? job)
        {
            job = null;
            if (String.IsNullOrWhiteSpace(jobId)) return false;

            if (_Jobs.TryGetValue(jobId, out LongRunningJob? trackedJob))
            {
                job = trackedJob.CreateSnapshot();
                return true;
            }

            LongRunningJob? journalled = ReadJournal(jobId);
            if (journalled == null) return false;
            job = journalled;
            return true;
        }

        /// <summary>
        /// Jobs accepted or running and not yet finished, oldest first. A deployer reads these before
        /// restarting the admiral: each one ends <see cref="LongRunningJobStatusEnum.Lost"/> if the
        /// restart comes first.
        /// </summary>
        /// <returns>Snapshots of every unfinished job.</returns>
        public List<LongRunningJob> ListUnfinished()
        {
            return _Jobs.Values
                .Where(job => !LongRunningJob.IsTerminal(job.Status))
                .OrderBy(job => job.SubmittedAtUtc)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .Select(job => job.CreateSnapshot())
                .ToList();
        }

        /// <summary>
        /// Every job this service knows, newest submitted first: the jobs held in memory plus the
        /// journalled jobs not held in memory, which include jobs a previous process accepted. A
        /// finished journal record older than <see cref="JournalRetention"/> is left out, as pruning
        /// would remove it. A journal record that cannot be read is reported through the warning
        /// callback, counted in <see cref="JournalFailures"/>, and counted in
        /// <paramref name="unreadableRecords"/>, so a shorter list is never silent.
        /// </summary>
        /// <param name="unreadableRecords">Number of journal records, or journal listings, that could not be read.</param>
        /// <returns>Snapshots of every known job.</returns>
        public List<LongRunningJob> ListJobs(out int unreadableRecords)
        {
            unreadableRecords = 0;
            Dictionary<string, LongRunningJob> jobs = new Dictionary<string, LongRunningJob>(StringComparer.Ordinal);
            foreach (LongRunningJob tracked in _Jobs.Values)
                jobs[tracked.JobId] = tracked.CreateSnapshot();

            if (_JournalDirectory != null)
            {
                DateTime cutoff = DateTime.UtcNow - JournalRetention;
                List<string> paths;
                try
                {
                    paths = Directory.EnumerateFiles(_JournalDirectory, "job_*" + _JournalExtension).ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ReportFailure("job journal directory could not be listed: " + ex.Message);
                    unreadableRecords++;
                    paths = new List<string>();
                }

                foreach (string path in paths)
                {
                    if (jobs.ContainsKey(Path.GetFileNameWithoutExtension(path))) continue;

                    LongRunningJob? journalled = ReadJournalFile(path);
                    if (journalled == null)
                    {
                        unreadableRecords++;
                        continue;
                    }
                    if (jobs.ContainsKey(journalled.JobId)) continue;
                    if (LongRunningJob.IsTerminal(journalled.Status)
                        && (journalled.CompletedAtUtc ?? journalled.SubmittedAtUtc) < cutoff) continue;
                    jobs[journalled.JobId] = journalled;
                }
            }

            return jobs.Values
                .OrderByDescending(job => job.SubmittedAtUtc)
                .ThenByDescending(job => job.JobId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Record every journalled job that a previous process accepted and never finished as
        /// <see cref="LongRunningJobStatusEnum.Lost"/>, notify the failure callback for each, and prune
        /// finished records older than <see cref="JournalRetention"/>. Call once at startup, before
        /// any new job is accepted.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of jobs recorded as lost.</returns>
        public async Task<int> RecoverInterruptedJobsAsync(CancellationToken token = default)
        {
            if (_JournalDirectory == null) return 0;

            int lost = 0;
            foreach (string path in Directory.EnumerateFiles(_JournalDirectory, "job_*" + _JournalExtension).OrderBy(p => p, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();

                LongRunningJob? journalled = ReadJournalFile(path);
                if (journalled == null) continue;
                if (_Jobs.ContainsKey(journalled.JobId)) continue;
                if (LongRunningJob.IsTerminal(journalled.Status)) continue;

                LongRunningJob lostJob = journalled.CreateSnapshot();
                lostJob.Status = LongRunningJobStatusEnum.Lost;
                lostJob.CompletedAtUtc = DateTime.UtcNow;
                lostJob.FailureMessage = LostMessage;
                _Jobs[lostJob.JobId] = lostJob;
                TryWriteJournal(lostJob);
                lost++;
                await NotifyFailedAsync(lostJob).ConfigureAwait(false);
            }

            PruneJournal(force: true);
            EvictOldestTerminalJobs();
            return lost;
        }

        /// <summary>
        /// Fail any job stuck in Running or Accepted past the stale threshold (its worker likely died
        /// or hung), so it reaches a terminal status instead of reading as in-flight forever.
        /// A reaped job's operation is cancelled, and the Failed status is final: an operation that
        /// finishes after the reap does not change it. Invoked periodically from the Admiral health
        /// loop.
        /// </summary>
        /// <param name="staleMinutes">Minutes a job may stay Running or Accepted before it is failed; clamped to a minimum of 1.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of jobs reaped.</returns>
        public async Task<int> ReapStaleJobsAsync(int staleMinutes = 30, CancellationToken token = default)
        {
            if (staleMinutes < 1) staleMinutes = 1;

            int reaped = 0;
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-staleMinutes);

            foreach (string jobId in _Jobs.Keys.ToList())
            {
                token.ThrowIfCancellationRequested();

                LongRunningJob? failedJob = TryTransition(jobId, current =>
                {
                    DateTime? startAnchor = current.Status == LongRunningJobStatusEnum.Accepted
                        ? current.SubmittedAtUtc
                        : current.StartedAtUtc;
                    if (startAnchor.HasValue && startAnchor.Value > cutoff)
                        return null;

                    LongRunningJob failed = current.CreateSnapshot();
                    failed.Status = LongRunningJobStatusEnum.Failed;
                    failed.CompletedAtUtc = DateTime.UtcNow;
                    failed.FailureMessage = "job did not reach a terminal state within " + staleMinutes + " minutes and was reaped as stale";
                    return failed;
                });
                if (failedJob == null) continue;

                // The job's status is final; stop the work so it does not keep running unobserved.
                CancelExecution(jobId);
                reaped++;
                await NotifyFailedAsync(failedJob).ConfigureAwait(false);
            }

            PruneJournal(force: false);
            return reaped;
        }

        #endregion

        #region Private-Methods

        private async Task ExecuteAsync(string jobId, Func<CancellationToken, Task<object?>> operationAsync)
        {
            using (CancellationTokenSource execution = new CancellationTokenSource())
            {
                _Executions[jobId] = execution;
                try
                {
                    LongRunningJob? runningJob = TryTransition(jobId, current =>
                    {
                        LongRunningJob running = current.CreateSnapshot();
                        running.Status = LongRunningJobStatusEnum.Running;
                        running.StartedAtUtc = DateTime.UtcNow;
                        return running;
                    });
                    if (runningJob == null) return;

                    LongRunningJob? finishedJob;
                    try
                    {
                        object? result = await operationAsync(execution.Token).ConfigureAwait(false);
                        finishedJob = TryTransition(jobId, current =>
                        {
                            LongRunningJob succeeded = current.CreateSnapshot();
                            succeeded.Status = LongRunningJobStatusEnum.Succeeded;
                            succeeded.CompletedAtUtc = DateTime.UtcNow;
                            succeeded.Result = result == null
                                ? null
                                : JsonSerializer.SerializeToElement(result, result.GetType());
                            return succeeded;
                        });
                    }
                    catch (Exception ex)
                    {
                        finishedJob = TryTransition(jobId, current =>
                        {
                            LongRunningJob failed = current.CreateSnapshot();
                            failed.Status = LongRunningJobStatusEnum.Failed;
                            failed.CompletedAtUtc = DateTime.UtcNow;
                            failed.FailureMessage = BoundFailureMessage(ex);
                            return failed;
                        });
                    }

                    if (finishedJob != null && finishedJob.Status == LongRunningJobStatusEnum.Failed)
                        await NotifyFailedAsync(finishedJob).ConfigureAwait(false);

                    EvictOldestTerminalJobs();
                }
                finally
                {
                    _Executions.TryRemove(new KeyValuePair<string, CancellationTokenSource>(jobId, execution));
                    ExecutionFinished?.Invoke(jobId);
                }
            }
        }

        /// <summary>
        /// Apply one status transition to a tracked job. A terminal status is final: the transition
        /// is refused when the job is unknown or already terminal, or when <paramref name="next"/>
        /// declines by returning null. The check, the replacement and the journal write happen under
        /// one lock, so the reaper and the executing operation cannot overwrite each other, and the
        /// journal is written before memory exposes the new status.
        /// </summary>
        /// <returns>The job as recorded, or null when no transition was made.</returns>
        private LongRunningJob? TryTransition(string jobId, Func<LongRunningJob, LongRunningJob?> next)
        {
            lock (_TransitionLock)
            {
                if (!_Jobs.TryGetValue(jobId, out LongRunningJob? current)) return null;
                if (LongRunningJob.IsTerminal(current.Status)) return null;

                LongRunningJob? updated = next(current);
                if (updated == null) return null;

                // Journal before memory: a status a reader can see is already durable.
                TryWriteJournal(updated);
                _Jobs[jobId] = updated;
                return updated;
            }
        }

        private void CancelExecution(string jobId)
        {
            if (!_Executions.TryGetValue(jobId, out CancellationTokenSource? execution)) return;
            try
            {
                execution.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation finished and released its token after the job was reaped.
            }
            catch (AggregateException ex)
            {
                ReportFailure("job " + jobId + " was reaped but a cancellation callback of its operation failed: " + ex.Message);
            }
        }

        private async Task NotifyFailedAsync(LongRunningJob job)
        {
            if (_OnJobFailedAsync == null) return;
            try
            {
                await _OnJobFailedAsync(job.CreateSnapshot()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportFailure("job " + job.JobId + " ended " + job.Status + " but its failure could not be recorded: " + ex.Message);
            }
        }

        private static string BoundFailureMessage(Exception exception)
        {
            string message = String.IsNullOrWhiteSpace(exception.Message)
                ? "Operation failed."
                : exception.Message.Trim();
            return message.Length <= _MaxFailureMessageLength
                ? message
                : message.Substring(0, _MaxFailureMessageLength);
        }

        private void EvictOldestTerminalJobs()
        {
            lock (_EvictionLock)
            {
                List<LongRunningJob> terminalJobs = _Jobs.Values
                    .Where(job => LongRunningJob.IsTerminal(job.Status))
                    .OrderBy(job => job.CompletedAtUtc ?? DateTime.MaxValue)
                    .ThenBy(job => job.SubmittedAtUtc)
                    .ThenBy(job => job.JobId, StringComparer.Ordinal)
                    .ToList();

                int removeCount = terminalJobs.Count - _MaxRetainedTerminalJobs;
                for (int index = 0; index < removeCount; index++)
                    _Jobs.TryRemove(terminalJobs[index].JobId, out LongRunningJob? _);
            }
        }

        private void WriteJournal(LongRunningJob job)
        {
            if (_JournalDirectory == null) return;

            string path = JournalPath(job.JobId)
                ?? throw new InvalidOperationException("Job identifier is not a valid journal name: " + job.JobId);
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string json = JsonSerializer.Serialize(job, _JournalJsonOptions);
            lock (_JournalLock)
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, true);
            }
        }

        private void TryWriteJournal(LongRunningJob job)
        {
            try
            {
                WriteJournal(job);
            }
            catch (Exception ex)
            {
                ReportFailure("job " + job.JobId + " (" + job.Status + ") could not be journalled: " + ex.Message);
            }
        }

        private LongRunningJob? ReadJournal(string jobId)
        {
            if (_JournalDirectory == null) return null;
            string? path = JournalPath(jobId);
            if (path == null || !File.Exists(path)) return null;
            return ReadJournalFile(path);
        }

        private LongRunningJob? ReadJournalFile(string path)
        {
            try
            {
                string json;
                lock (_JournalLock)
                {
                    json = File.ReadAllText(path);
                }
                LongRunningJob? job = JsonSerializer.Deserialize<LongRunningJob>(json, _JournalJsonOptions);
                if (job == null || String.IsNullOrWhiteSpace(job.JobId))
                {
                    ReportFailure("job journal record " + Path.GetFileName(path) + " is empty or has no job id");
                    return null;
                }
                return job;
            }
            catch (Exception ex)
            {
                ReportFailure("job journal record " + Path.GetFileName(path) + " could not be read: " + ex.Message);
                return null;
            }
        }

        private void PruneJournal(bool force)
        {
            if (_JournalDirectory == null) return;
            DateTime now = DateTime.UtcNow;
            if (!force && now - _LastPruneUtc < TimeSpan.FromHours(1)) return;
            _LastPruneUtc = now;

            DateTime cutoff = now - JournalRetention;
            foreach (string path in Directory.EnumerateFiles(_JournalDirectory, "job_*" + _JournalExtension))
            {
                string jobId = Path.GetFileNameWithoutExtension(path);
                if (_Jobs.TryGetValue(jobId, out LongRunningJob? live) && !LongRunningJob.IsTerminal(live.Status)) continue;

                LongRunningJob? journalled = ReadJournalFile(path);
                if (journalled == null) continue;
                if (!LongRunningJob.IsTerminal(journalled.Status)) continue;
                if ((journalled.CompletedAtUtc ?? journalled.SubmittedAtUtc) >= cutoff) continue;

                try
                {
                    lock (_JournalLock)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    ReportFailure("expired job journal record " + Path.GetFileName(path) + " could not be deleted: " + ex.Message);
                }
            }
        }

        private string? JournalPath(string jobId)
        {
            if (_JournalDirectory == null || String.IsNullOrWhiteSpace(jobId)) return null;
            foreach (char c in jobId)
            {
                if (!Char.IsLetterOrDigit(c) && c != '_' && c != '-') return null;
            }
            return Path.Combine(_JournalDirectory, jobId + _JournalExtension);
        }

        private void ReportFailure(string message)
        {
            Interlocked.Increment(ref _JournalFailures);
            _Warn?.Invoke(message);
        }

        #endregion
    }
}
