namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// What a Harbor knows about its jobs, kept for as long as its job runner lives rather than for one link session: the
    /// jobs running now (reported in every handshake and heartbeat, and shown in Running now), each job's activity
    /// handler, and the <see cref="HarborJobOutbox"/> its lifecycle messages travel through. The owner of the reconnect
    /// loop gives the same instance to every session's <see cref="HarborLinkClient"/>, so a job started over one link is
    /// still listed, and still reports its output and exit, after the link drops and a new one opens. Thread safe.
    /// </summary>
    public class HarborJobState
    {
        #region Public-Members

        /// <summary>
        /// The job messages on their way to the Admiral.
        /// </summary>
        public HarborJobOutbox Outbox { get; } = new HarborJobOutbox();

        #endregion

        #region Private-Members

        private readonly Dictionary<string, HarborJobInfo> _LiveJobs = new Dictionary<string, HarborJobInfo>(StringComparer.Ordinal);
        private readonly object _JobLock = new object();
        private readonly ConcurrentDictionary<string, Action<RuntimeActivity>> _ActivityHandlers = new ConcurrentDictionary<string, Action<RuntimeActivity>>(StringComparer.Ordinal);
        private readonly HashSet<IHarborJobProgressSource> _Attached = new HashSet<IHarborJobProgressSource>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with no jobs and an empty outbox.
        /// </summary>
        public HarborJobState()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Listen for activity from a job runner that reports it (an <see cref="IHarborJobProgressSource"/>). Attaching the
        /// same runner again does nothing, so each activity reaches its job's handler once however many sessions use it.
        /// </summary>
        /// <param name="runner">The job runner, or null.</param>
        public void Attach(IHarborJobRunner? runner)
        {
            if (!(runner is IHarborJobProgressSource progress)) return;
            lock (_Attached)
            {
                if (!_Attached.Add(progress)) return;
            }

            progress.ActivityReported += OnActivity;
        }

        /// <summary>
        /// Identifiers of the jobs running now.
        /// </summary>
        /// <returns>Snapshot of the identifiers.</returns>
        public List<string> LiveJobIds()
        {
            lock (_JobLock) return new List<string>(_LiveJobs.Keys);
        }

        /// <summary>
        /// Copies of the jobs running now, oldest first.
        /// </summary>
        /// <returns>The jobs.</returns>
        public List<HarborJobInfo> LiveJobs()
        {
            List<HarborJobInfo> jobs = new List<HarborJobInfo>();
            lock (_JobLock)
            {
                foreach (HarborJobInfo job in _LiveJobs.Values) jobs.Add(job.Clone());
            }

            jobs.Sort((a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
            return jobs;
        }

        /// <summary>
        /// Whether a job is running now.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <returns>True when running.</returns>
        public bool IsLive(string jobId)
        {
            if (String.IsNullOrEmpty(jobId)) return false;
            lock (_JobLock) return _LiveJobs.ContainsKey(jobId);
        }

        /// <summary>
        /// Record a job as running (replacing any record with the same identifier).
        /// </summary>
        /// <param name="job">The job.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="job"/> is null.</exception>
        public void AddLiveJob(HarborJobInfo job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (_JobLock) _LiveJobs[job.JobId] = job;
        }

        /// <summary>
        /// Forget a job (it exited or failed to start).
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        public void RemoveLiveJob(string jobId)
        {
            if (String.IsNullOrEmpty(jobId)) return;
            lock (_JobLock) _LiveJobs.Remove(jobId);
        }

        /// <summary>
        /// Change a running job's record under the lock; nothing happens when the job is not running.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="update">The change.</param>
        public void UpdateLiveJob(string jobId, Action<HarborJobInfo> update)
        {
            if (String.IsNullOrEmpty(jobId) || update == null) return;
            lock (_JobLock)
            {
                if (_LiveJobs.TryGetValue(jobId, out HarborJobInfo? job)) update(job);
            }
        }

        /// <summary>
        /// Set the handler that receives a job's activity.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="handler">The handler.</param>
        public void SetActivityHandler(string jobId, Action<RuntimeActivity> handler)
        {
            if (String.IsNullOrEmpty(jobId) || handler == null) return;
            _ActivityHandlers[jobId] = handler;
        }

        /// <summary>
        /// Remove a job's activity handler.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        public void RemoveActivityHandler(string jobId)
        {
            if (String.IsNullOrEmpty(jobId)) return;
            _ActivityHandlers.TryRemove(jobId, out Action<RuntimeActivity>? _);
        }

        #endregion

        #region Private-Methods

        private void OnActivity(string jobId, RuntimeActivity activity)
        {
            if (String.IsNullOrEmpty(jobId) || activity == null) return;
            if (_ActivityHandlers.TryGetValue(jobId, out Action<RuntimeActivity>? handler))
            {
                try { handler(activity); }
                catch { }
            }
        }

        #endregion
    }
}
