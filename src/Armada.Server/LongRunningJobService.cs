namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;

    /// <summary>
    /// Tracks bounded process-local background jobs independently of initiating requests.
    /// </summary>
    public class LongRunningJobService
    {
        #region Private-Members

        private const int _DefaultMaxRetainedTerminalJobs = 100;
        private const int _MaxFailureMessageLength = 1024;

        private readonly ConcurrentDictionary<string, LongRunningJob> _Jobs = new ConcurrentDictionary<string, LongRunningJob>(StringComparer.Ordinal);
        private readonly object _EvictionLock = new object();
        private readonly int _MaxRetainedTerminalJobs;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Initialize a process-local long-running job tracker.
        /// </summary>
        /// <param name="maxRetainedTerminalJobs">Maximum succeeded or failed jobs retained for status reads.</param>
        public LongRunningJobService(int maxRetainedTerminalJobs = _DefaultMaxRetainedTerminalJobs)
        {
            _MaxRetainedTerminalJobs = Math.Max(1, maxRetainedTerminalJobs);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Accept an operation for request-independent background execution.
        /// </summary>
        /// <param name="operation">Operation name exposed in status responses.</param>
        /// <param name="operationAsync">Asynchronous operation to execute.</param>
        /// <returns>An accepted snapshot captured before execution is scheduled.</returns>
        public LongRunningJob Start(string operation, Func<CancellationToken, Task<object?>> operationAsync)
        {
            if (String.IsNullOrWhiteSpace(operation)) throw new ArgumentException("Operation is required.", nameof(operation));
            if (operationAsync == null) throw new ArgumentNullException(nameof(operationAsync));

            LongRunningJob trackedJob = new LongRunningJob
            {
                JobId = Constants.IdGenerator.GenerateKSortable("job_", 24),
                Operation = operation.Trim(),
                Status = LongRunningJobStatusEnum.Accepted,
                SubmittedAtUtc = DateTime.UtcNow
            };

            if (!_Jobs.TryAdd(trackedJob.JobId, trackedJob))
                throw new InvalidOperationException("Unable to allocate a unique job identifier.");

            LongRunningJob acceptedSnapshot = trackedJob.CreateSnapshot();
            _ = Task.Run(
                () => ExecuteAsync(trackedJob.JobId, operationAsync),
                CancellationToken.None);

            return acceptedSnapshot;
        }

        /// <summary>
        /// Try to read a defensive snapshot of a tracked job.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="job">Defensive job snapshot when found.</param>
        /// <returns>True when the job is currently retained.</returns>
        public bool TryGetStatus(string jobId, out LongRunningJob? job)
        {
            job = null;
            if (String.IsNullOrWhiteSpace(jobId)) return false;

            if (!_Jobs.TryGetValue(jobId, out LongRunningJob? trackedJob)) return false;
            job = trackedJob.CreateSnapshot();
            return true;
        }

        #endregion

        #region Private-Methods

        private async Task ExecuteAsync(string jobId, Func<CancellationToken, Task<object?>> operationAsync)
        {
            if (!_Jobs.TryGetValue(jobId, out LongRunningJob? acceptedJob)) return;

            LongRunningJob runningJob = acceptedJob.CreateSnapshot();
            runningJob.Status = LongRunningJobStatusEnum.Running;
            runningJob.StartedAtUtc = DateTime.UtcNow;
            _Jobs[jobId] = runningJob;

            try
            {
                object? result = await operationAsync(CancellationToken.None).ConfigureAwait(false);
                LongRunningJob succeededJob = runningJob.CreateSnapshot();
                succeededJob.Status = LongRunningJobStatusEnum.Succeeded;
                succeededJob.CompletedAtUtc = DateTime.UtcNow;
                succeededJob.Result = result == null
                    ? null
                    : JsonSerializer.SerializeToElement(result, result.GetType());
                _Jobs[jobId] = succeededJob;
            }
            catch (Exception ex)
            {
                LongRunningJob failedJob = runningJob.CreateSnapshot();
                failedJob.Status = LongRunningJobStatusEnum.Failed;
                failedJob.CompletedAtUtc = DateTime.UtcNow;
                failedJob.FailureMessage = BoundFailureMessage(ex);
                _Jobs[jobId] = failedJob;
            }

            EvictOldestTerminalJobs();
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
                    .Where(job => job.Status == LongRunningJobStatusEnum.Succeeded || job.Status == LongRunningJobStatusEnum.Failed)
                    .OrderBy(job => job.CompletedAtUtc ?? DateTime.MaxValue)
                    .ThenBy(job => job.SubmittedAtUtc)
                    .ThenBy(job => job.JobId, StringComparer.Ordinal)
                    .ToList();

                int removeCount = terminalJobs.Count - _MaxRetainedTerminalJobs;
                for (int index = 0; index < removeCount; index++)
                    _Jobs.TryRemove(terminalJobs[index].JobId, out LongRunningJob? _);
            }
        }

        #endregion
    }
}
