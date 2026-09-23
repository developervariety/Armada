namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Authorizes commands to Harbor runners and binds every job to its runner, its durable enrollment
    /// generation and the connection generation allowed to report for it. Runner events for a job are accepted
    /// only from that connection, and every claim and result is revalidated against durable enrollment first.
    /// Durable reads and writes run outside this coordinator's lock; the lock guards in-memory state only.
    /// </summary>
    public sealed class HarborJobCoordinator
    {
        #region Public-Members

        /// <summary>Reason recorded for a job that was not terminal when the Admiral started.</summary>
        public const string ReasonAdmiralRestarted = "harbor_admiral_restarted";

        /// <summary>Reason recorded for a job whose runner stayed disconnected past the grace period.</summary>
        public const string ReasonRunnerDisconnected = "harbor_runner_disconnected";

        /// <summary>Reason recorded when a stop releases a job whose runner is not connected.</summary>
        public const string ReasonReleasedRunnerUnavailable = "harbor_job_released_runner_unavailable";

        /// <summary>Refusal for mission work on a runner enrolled to another tenant or user.</summary>
        public const string ReasonRunnerOwnerMismatch = "harbor_runner_owner_mismatch";

        /// <summary>Refusal for a launch whose durable record could not be written.</summary>
        public const string ReasonJobStoreUnavailable = "harbor_job_store_unavailable";

        /// <summary>Durable writes that failed. Each failure is logged with the job it concerns.</summary>
        public int PersistFailureCount => Volatile.Read(ref _PersistFailures);

        /// <summary>Observer notifications that threw. Each failure is logged with the job it concerns.</summary>
        public int ObserverFailureCount => Volatile.Read(ref _ObserverFailures);

        #endregion

        #region Private-Members

        private readonly string _Header = "[HarborJobCoordinator] ";
        private readonly object _Gate = new object();
        private readonly HarborRunnerSessionRegistry _Registry;
        private readonly IHarborRunnerAuthority _Authority;
        private readonly IHarborJobMethods? _Store;
        private readonly LoggingModule? _Logging;
        private readonly Dictionary<string, RunnerLink> _Links = new Dictionary<string, RunnerLink>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _DetachedUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, JobRecord> _Jobs = new Dictionary<string, JobRecord>(StringComparer.Ordinal);
        private int _PersistFailures;
        private int _ObserverFailures;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate a coordinator over a session registry.</summary>
        /// <param name="registry">Registry that owns runner sessions.</param>
        /// <param name="authority">Shared runner authority rule, the same one enrollment uses.</param>
        /// <param name="store">Durable job records. Without a store jobs live only in memory, which only isolated tests use.</param>
        /// <param name="logging">Logging module for durable write and observer failures.</param>
        public HarborJobCoordinator(HarborRunnerSessionRegistry registry, IHarborRunnerAuthority authority, IHarborJobMethods? store = null, LoggingModule? logging = null)
        {
            _Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _Authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _Store = store;
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Fail every durable job an earlier Admiral process left unfinished. Jobs live in one Admiral process; after a
        /// restart none of them has a link or a mission process left to report for it, so each becomes lost with
        /// <see cref="ReasonAdmiralRestarted"/> instead of being forgotten. A runner that later reports one is refused.
        /// </summary>
        /// <param name="store">Durable job records.</param>
        /// <param name="nowUtc">Time of the change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of jobs failed.</returns>
        public static Task<int> ReconcileAfterRestartAsync(IHarborJobMethods store, DateTime nowUtc, CancellationToken token = default)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            return store.FailActiveAsync(ReasonAdmiralRestarted, nowUtc, token);
        }

        /// <summary>Attach the send channel of a newly accepted session. A replacement link supersedes the old one.</summary>
        /// <param name="session">Accepted session.</param>
        /// <param name="maxConcurrentJobs">Capacity advertised in the handshake.</param>
        /// <param name="send">Channel that writes to this link only.</param>
        public void Attach(HarborRunnerSession session, int maxConcurrentJobs, Func<HarborMessage, CancellationToken, Task> send)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (send == null) throw new ArgumentNullException(nameof(send));
            lock (_Gate)
            {
                _Links[session.Identity.RunnerId] = new RunnerLink(session, Math.Max(1, maxConcurrentJobs), send);
                _DetachedUtc.Remove(session.Identity.RunnerId);
            }
        }

        /// <summary>Detach a link only when it is still the runner's current link. A stale link cannot detach its replacement.</summary>
        /// <param name="session">Session whose link closed.</param>
        /// <returns>True when this session's link was detached.</returns>
        public bool Detach(HarborRunnerSession session)
        {
            if (session == null) return false;
            lock (_Gate)
            {
                if (!_Links.TryGetValue(session.Identity.RunnerId, out RunnerLink? link) || !Object.ReferenceEquals(link.Session, session)) return false;
                _Links.Remove(session.Identity.RunnerId);
                _DetachedUtc[session.Identity.RunnerId] = DateTime.UtcNow;
                return true;
            }
        }

        /// <summary>
        /// Launch a job on one named runner. There is no fallback to another runner or to local execution: a
        /// runner that is not connected, not authorized for the caller, at capacity, or already running the same
        /// launch key refuses the launch with a stable reason.
        /// </summary>
        /// <param name="caller">Verified caller; authorized when it is the runner owner or has authority over the owner.</param>
        /// <param name="runnerId">Runner that must run the job.</param>
        /// <param name="launchKey">Key of the work being launched; one live job per key within the caller's tenant.</param>
        /// <param name="request">Launch plan. Its job identifier is ignored and replaced by a server-issued one.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Launch result.</returns>
        public Task<HarborLaunchResult> LaunchAsync(AuthContext caller, string runnerId, string launchKey, HarborLaunchRequest request, CancellationToken token = default)
        {
            return LaunchCoreAsync(caller, runnerId, launchKey, request, null, token);
        }

        /// <summary>
        /// Launch mission work on one named runner. The caller must be exactly the runner's enrolled tenant and user;
        /// otherwise the launch is refused with <see cref="ReasonRunnerOwnerMismatch"/>. The durable record is written
        /// before the runner sees the launch.
        /// </summary>
        /// <param name="caller">Mission owner.</param>
        /// <param name="runnerId">Runner that must run the job.</param>
        /// <param name="launchKey">Key of the work being launched.</param>
        /// <param name="request">Launch plan.</param>
        /// <param name="binding">Mission binding and observer.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Launch result.</returns>
        public Task<HarborLaunchResult> LaunchAsync(AuthContext caller, string runnerId, string launchKey, HarborLaunchRequest request, HarborJobBinding binding, CancellationToken token = default)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            return LaunchCoreAsync(caller, runnerId, launchKey, request, binding, token);
        }

        /// <summary>
        /// Send a stop for a job to the runner and connection that own it. The caller must be authorized for the
        /// job's owner. When the owning runner is not connected the Admiral releases the job instead: it becomes
        /// lost with <see cref="ReasonReleasedRunnerUnavailable"/>, and a later report for it is refused.
        /// </summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="jobId">Server-issued job identifier.</param>
        /// <param name="gracefulTimeoutMs">Grace period before the runner kills the process tree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Stop result.</returns>
        public async Task<HarborCommandResult> StopAsync(AuthContext caller, string jobId, int gracefulTimeoutMs = 10000, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(jobId)) return HarborCommandResult.Reject("harbor_job_id_invalid");
            string runnerId;
            string ownerTenantId;
            string ownerUserId;
            lock (_Gate)
            {
                if (!_Jobs.TryGetValue(jobId, out JobRecord? job)) return HarborCommandResult.Reject("harbor_job_unknown");
                if (HarborJobRecord.IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_not_running");
                runnerId = job.RunnerId;
                ownerTenantId = job.OwnerTenantId;
                ownerUserId = job.OwnerUserId;
            }
            if (!await HarborRunnerAuthorization.IsOwnerOrHasAuthorityAsync(_Authority, caller, ownerTenantId, ownerUserId, token).ConfigureAwait(false))
                return HarborCommandResult.Reject("harbor_command_unauthorized");

            if (!_Registry.TryGetCurrent(runnerId, out HarborRunnerSession? session) || session == null)
                return await ReleaseAsync(jobId, ReasonReleasedRunnerUnavailable).ConfigureAwait(false);
            HarborRunnerCheck revalidated = await _Registry.RevalidateAsync(session, token).ConfigureAwait(false);
            string revalidation = revalidated.FailureReason;
            if (!revalidated.Accepted)
            {
                await MarkSessionEndedAsync(session, revalidation).ConfigureAwait(false);
                return HarborCommandResult.Reject(revalidation);
            }

            RunnerLink? link;
            List<HarborJobRecord> persist = new List<HarborJobRecord>();
            lock (_Gate)
            {
                JobRecord job = _Jobs[jobId];
                if (HarborJobRecord.IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_not_running");
                if (job.EnrollmentGeneration != session.EnrollmentGeneration || job.SessionGeneration != session.Generation)
                    return HarborCommandResult.Reject("harbor_job_not_bound");
                if (!_Links.TryGetValue(runnerId, out link) || !Object.ReferenceEquals(link.Session, session))
                    return HarborCommandResult.Reject("harbor_runner_unavailable");
                job.State = HarborJobStateEnum.Stopping;
                Touch(job, persist);
            }
            await PersistAsync(persist).ConfigureAwait(false);
            try
            {
                await link.Send(new HarborKillRequest { JobId = jobId, GracefulTimeoutMs = gracefulTimeoutMs }, token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return HarborCommandResult.Reject("harbor_stop_send_failed: " + exception.Message);
            }
            return HarborCommandResult.Accept();
        }

        /// <summary>
        /// Apply a runner event. The link must still be current, the enrollment must pass durable revalidation, the
        /// event must come from the connection generation bound to the job, and output must arrive in sequence.
        /// </summary>
        /// <param name="session">Session of the link that delivered the event.</param>
        /// <param name="message">Runner event.</param>
        /// <returns>Event result. A result that ends the session means the link must be closed.</returns>
        public async Task<HarborCommandResult> HandleRunnerEventAsync(HarborRunnerSession session, HarborMessage message)
        {
            if (session == null || message == null) return HarborCommandResult.Reject("harbor_message_invalid");
            if (!_Registry.IsCurrent(session)) return HarborCommandResult.EndSession("harbor_session_stale");

            // A claim or a result from a runner whose enrollment was revoked or re-issued on any Admiral instance is
            // refused by name before it changes a job.
            HarborRunnerCheck revalidated = await _Registry.RevalidateAsync(session, CancellationToken.None).ConfigureAwait(false);
            string revalidation = revalidated.FailureReason;
            if (!revalidated.Accepted)
            {
                await MarkSessionEndedAsync(session, revalidation).ConfigureAwait(false);
                return HarborCommandResult.EndSession(revalidation);
            }

            List<HarborJobRecord> persist = new List<HarborJobRecord>();
            HarborCommandResult result;
            lock (_Gate)
            {
                if (!_Registry.IsCurrent(session)) return HarborCommandResult.EndSession("harbor_session_stale");
                result = ApplyEvent(session, message, persist);
            }
            await PersistAsync(persist).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Apply a revalidated heartbeat. Reported jobs from the same runner and enrollment generation are rebound
        /// to the reporting connection. Jobs from an earlier enrollment generation are never rebound and become
        /// lost, as do jobs a reconnected runner no longer reports. A reported job this Admiral process does not
        /// hold but the durable store knows is refused as not rebindable. The accepted output sequence of every live
        /// job is persisted.
        /// </summary>
        /// <param name="session">Session that sent the heartbeat and passed revalidation.</param>
        /// <param name="liveJobIds">Jobs the runner reports alive.</param>
        /// <returns>Heartbeat result.</returns>
        public async Task<HarborHeartbeatResult> ApplyHeartbeatAsync(HarborRunnerSession session, IReadOnlyCollection<string> liveJobIds)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            List<KeyValuePair<string, string>> rejected = new List<KeyValuePair<string, string>>();
            List<string> unknown = new List<string>();
            List<HarborJobRecord> persist = new List<HarborJobRecord>();
            int rebound = 0;
            HashSet<string> live = new HashSet<string>(liveJobIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            lock (_Gate)
            {
                if (!_Registry.IsCurrent(session))
                {
                    foreach (string jobId in live) rejected.Add(new KeyValuePair<string, string>(jobId, "harbor_session_stale"));
                    return new HarborHeartbeatResult(0, rejected);
                }
                foreach (string jobId in live)
                {
                    if (!_Jobs.TryGetValue(jobId, out JobRecord? job))
                    {
                        unknown.Add(jobId);
                        continue;
                    }
                    if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal))
                    {
                        rejected.Add(new KeyValuePair<string, string>(jobId, "harbor_job_not_owned"));
                        continue;
                    }
                    if (job.EnrollmentGeneration != session.EnrollmentGeneration || HarborJobRecord.IsTerminal(job.State))
                    {
                        if (!HarborJobRecord.IsTerminal(job.State)) Finish(job, HarborJobStateEnum.Lost, null, "harbor_enrollment_changed", persist);
                        rejected.Add(new KeyValuePair<string, string>(jobId, "harbor_job_not_rebindable"));
                        continue;
                    }
                    if (job.SessionGeneration != session.Generation)
                    {
                        job.SessionGeneration = session.Generation;
                        rebound++;
                        Touch(job, persist);
                    }
                }
                foreach (JobRecord job in _Jobs.Values)
                {
                    if (HarborJobRecord.IsTerminal(job.State) || !String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)) continue;
                    if (live.Contains(job.JobId))
                    {
                        if (job.NextOutputSequence != job.PersistedOutputSequence && !persist.Exists(r => r.JobId == job.JobId)) Touch(job, persist);
                        continue;
                    }
                    if (job.EnrollmentGeneration != session.EnrollmentGeneration)
                        Finish(job, HarborJobStateEnum.Lost, null, "harbor_enrollment_changed", persist);
                    else if (job.SessionGeneration != session.Generation)
                        Finish(job, HarborJobStateEnum.Lost, null, "harbor_job_not_reported_after_reconnect", persist);
                    else if (job.State != HarborJobStateEnum.Pending)
                        Finish(job, HarborJobStateEnum.Lost, null, "harbor_job_not_reported", persist);
                }
            }

            foreach (string jobId in unknown)
                rejected.Add(new KeyValuePair<string, string>(jobId, await ClassifyUnknownJobAsync(session, jobId).ConfigureAwait(false)));
            await PersistAsync(persist).ConfigureAwait(false);
            return new HarborHeartbeatResult(rebound, rejected);
        }

        /// <summary>
        /// Record that a session ended because it failed revalidation. Its link is detached; its jobs become lost
        /// unless a current session with the same enrollment generation can still rebind them.
        /// </summary>
        /// <param name="session">Session that ended.</param>
        /// <param name="reason">Stable reason.</param>
        public async Task MarkSessionEndedAsync(HarborRunnerSession session, string reason)
        {
            if (session == null) return;
            List<HarborJobRecord> persist = new List<HarborJobRecord>();
            lock (_Gate)
            {
                if (_Links.TryGetValue(session.Identity.RunnerId, out RunnerLink? link) && Object.ReferenceEquals(link.Session, session))
                {
                    _Links.Remove(session.Identity.RunnerId);
                    _DetachedUtc[session.Identity.RunnerId] = DateTime.UtcNow;
                }
                bool replacementCanRebind = _Registry.TryGetCurrent(session.Identity.RunnerId, out HarborRunnerSession? current)
                    && current != null
                    && current.EnrollmentGeneration == session.EnrollmentGeneration;
                if (!replacementCanRebind)
                {
                    foreach (JobRecord job in _Jobs.Values)
                    {
                        if (HarborJobRecord.IsTerminal(job.State)) continue;
                        if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)) continue;
                        if (job.EnrollmentGeneration != session.EnrollmentGeneration) continue;
                        Finish(job, HarborJobStateEnum.Lost, null, String.IsNullOrWhiteSpace(reason) ? "harbor_session_ended" : reason, persist);
                    }
                }
            }
            await PersistAsync(persist).ConfigureAwait(false);
        }

        /// <summary>
        /// Lose every live job whose runner has had no link for longer than the grace period. A process on a runner
        /// that never reconnects is then treated like a local process that died.
        /// </summary>
        /// <param name="grace">How long a runner may stay disconnected.</param>
        /// <param name="nowUtc">Current time.</param>
        /// <returns>Number of jobs marked lost.</returns>
        public async Task<int> ExpireDetachedRunnersAsync(TimeSpan grace, DateTime nowUtc)
        {
            List<HarborJobRecord> persist = new List<HarborJobRecord>();
            int expired = 0;
            lock (_Gate)
            {
                foreach (JobRecord job in _Jobs.Values)
                {
                    if (HarborJobRecord.IsTerminal(job.State) || _Links.ContainsKey(job.RunnerId)) continue;
                    DateTime since = _DetachedUtc.TryGetValue(job.RunnerId, out DateTime detached) ? detached : job.CreatedUtc;
                    if (nowUtc - since < grace) continue;
                    Finish(job, HarborJobStateEnum.Lost, null, ReasonRunnerDisconnected, persist);
                    expired++;
                }
            }
            await PersistAsync(persist).ConfigureAwait(false);
            return expired;
        }

        /// <summary>Read an immutable view of a job this Admiral process holds.</summary>
        /// <param name="jobId">Server-issued job identifier.</param>
        /// <param name="snapshot">Job view when known.</param>
        /// <returns>True when the job is known.</returns>
        public bool TryGetJob(string jobId, out HarborJobSnapshot? snapshot)
        {
            snapshot = null;
            if (String.IsNullOrWhiteSpace(jobId)) return false;
            lock (_Gate)
            {
                if (!_Jobs.TryGetValue(jobId, out JobRecord? job)) return false;
                snapshot = job.Snapshot();
                return true;
            }
        }

        /// <summary>Read the current durable view of a job this Admiral process holds, including unpersisted output progress.</summary>
        /// <param name="jobId">Server-issued job identifier.</param>
        /// <param name="record">Record when known.</param>
        /// <returns>True when the job is known.</returns>
        public bool TryGetRecord(string jobId, out HarborJobRecord? record)
        {
            record = null;
            if (String.IsNullOrWhiteSpace(jobId)) return false;
            lock (_Gate)
            {
                if (!_Jobs.TryGetValue(jobId, out JobRecord? job)) return false;
                record = job.ToDurable();
                return true;
            }
        }

        #endregion

        #region Private-Methods

        private async Task<HarborLaunchResult> LaunchCoreAsync(AuthContext caller, string runnerId, string launchKey, HarborLaunchRequest request, HarborJobBinding? binding, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(runnerId) || String.IsNullOrWhiteSpace(launchKey) || launchKey.Length > 200
                || request == null || String.IsNullOrWhiteSpace(request.Runtime) || String.IsNullOrWhiteSpace(request.WorkingDirectory))
                return HarborLaunchResult.Reject("harbor_launch_invalid");
            if (caller == null || !caller.IsAuthenticated || String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId))
                return HarborLaunchResult.Reject("harbor_command_unauthorized");
            if (!_Registry.TryGetCurrent(runnerId.Trim(), out HarborRunnerSession? session) || session == null)
                return HarborLaunchResult.Reject("harbor_runner_unavailable");
            HarborRunnerCheck revalidated = await _Registry.RevalidateAsync(session, token).ConfigureAwait(false);
            string revalidation = revalidated.FailureReason;
            if (!revalidated.Accepted)
            {
                await MarkSessionEndedAsync(session, revalidation).ConfigureAwait(false);
                return HarborLaunchResult.Reject(revalidation);
            }
            if (binding != null)
            {
                if (!HarborRunnerAuthorization.IsOwner(caller, session.Identity.TenantId, session.Identity.UserId))
                    return HarborLaunchResult.Reject(ReasonRunnerOwnerMismatch);
            }
            else if (!await HarborRunnerAuthorization.IsOwnerOrHasAuthorityAsync(_Authority, caller, session.Identity.TenantId, session.Identity.UserId, token).ConfigureAwait(false))
            {
                return HarborLaunchResult.Reject("harbor_command_unauthorized");
            }

            JobRecord record;
            RunnerLink? link;
            long createdRevision;
            lock (_Gate)
            {
                if (!_Links.TryGetValue(session.Identity.RunnerId, out link) || !Object.ReferenceEquals(link.Session, session))
                    return HarborLaunchResult.Reject("harbor_runner_unavailable");
                int liveOnRunner = 0;
                foreach (JobRecord existing in _Jobs.Values)
                {
                    if (HarborJobRecord.IsTerminal(existing.State)) continue;
                    if (String.Equals(existing.LaunchKey, launchKey, StringComparison.Ordinal)
                        && String.Equals(existing.RequestedByTenantId, caller.TenantId, StringComparison.Ordinal))
                        return HarborLaunchResult.Reject("harbor_job_duplicate");
                    if (String.Equals(existing.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)) liveOnRunner++;
                }
                if (liveOnRunner >= link.MaxConcurrentJobs) return HarborLaunchResult.Reject("harbor_runner_capacity_exhausted");
                record = new JobRecord(
                    "hjob_" + Guid.NewGuid().ToString("N"),
                    session.Identity.RunnerId,
                    launchKey,
                    caller.TenantId!,
                    caller.UserId!,
                    session.Identity.TenantId,
                    session.Identity.UserId,
                    session.EnrollmentGeneration,
                    session.Generation,
                    binding);
                _Jobs[record.JobId] = record;
                createdRevision = record.Revision;
            }

            if (_Store != null)
            {
                HarborJobRecord created;
                lock (_Gate) created = record.ToDurable();
                try
                {
                    await _Store.CreateAsync(created, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordPersistFailure(record.JobId, "create", exception);
                    lock (_Gate)
                    {
                        record.Observer = null;
                        Finish(record, HarborJobStateEnum.Failed, null, ReasonJobStoreUnavailable, null);
                    }
                    return HarborLaunchResult.Reject(ReasonJobStoreUnavailable);
                }
            }

            List<HarborJobRecord> persist = new List<HarborJobRecord>();
            lock (_Gate)
            {
                record.Durable = true;
                if (record.Revision > createdRevision) persist.Add(record.ToDurable());
            }
            await PersistAsync(persist).ConfigureAwait(false);

            HarborLaunchRequest outbound = new HarborLaunchRequest
            {
                CorrelationId = request.CorrelationId,
                TraceParent = request.TraceParent,
                JobId = record.JobId,
                Runtime = request.Runtime,
                WorkingDirectory = request.WorkingDirectory,
                Model = request.Model,
                Prompt = request.Prompt,
                PromptViaStdin = request.PromptViaStdin,
                Arguments = new List<string>(request.Arguments ?? new List<string>()),
                Environment = new Dictionary<string, string>(request.Environment ?? new Dictionary<string, string>())
            };
            try
            {
                await link.Send(outbound, token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                persist.Clear();
                lock (_Gate)
                {
                    record.Observer = null;
                    Finish(record, HarborJobStateEnum.Failed, null, "harbor_launch_send_failed: " + exception.Message, persist);
                }
                await PersistAsync(persist).ConfigureAwait(false);
                return HarborLaunchResult.Reject("harbor_launch_send_failed");
            }
            return HarborLaunchResult.Accept(record.JobId, record.Completion.Task);
        }

        private HarborCommandResult ApplyEvent(HarborRunnerSession session, HarborMessage message, List<HarborJobRecord> persist)
        {
            string reason;
            JobRecord? job;
            switch (message)
            {
                case HarborStarted started:
                    job = OwnedJob(session, started.JobId, out reason);
                    if (job == null) return HarborCommandResult.Reject(reason);
                    if (job.State != HarborJobStateEnum.Pending) return HarborCommandResult.Reject("harbor_job_started_duplicate");
                    if (started.ProcessId <= 0) return HarborCommandResult.Reject("harbor_process_id_invalid");
                    job.ProcessId = started.ProcessId;
                    job.State = HarborJobStateEnum.Running;
                    Touch(job, persist);
                    HarborJobSnapshot startedView = job.Snapshot();
                    Notify(job, observer => observer.OnStarted(startedView));
                    return HarborCommandResult.Accept();

                case HarborOutput output:
                    job = OwnedJob(session, output.JobId, out reason);
                    if (job == null) return HarborCommandResult.Reject(reason);
                    if (job.State != HarborJobStateEnum.Running && job.State != HarborJobStateEnum.Stopping)
                        return HarborCommandResult.Reject("harbor_job_not_running");
                    if (output.Sequence < job.NextOutputSequence) return HarborCommandResult.Reject("harbor_output_replayed");
                    if (output.Sequence > job.NextOutputSequence) return HarborCommandResult.Reject("harbor_output_gap");
                    HarborOutput accepted = new HarborOutput { JobId = job.JobId, Sequence = output.Sequence, Stream = output.Stream, Data = output.Data ?? String.Empty };
                    if (job.Observer == null) job.Output.Add(accepted);
                    job.NextOutputSequence++;
                    string outputJobId = job.JobId;
                    Notify(job, observer => observer.OnOutput(outputJobId, accepted));
                    return HarborCommandResult.Accept();

                case HarborExited exited:
                    job = OwnedJob(session, exited.JobId, out reason);
                    if (job == null) return HarborCommandResult.Reject(reason);
                    if (HarborJobRecord.IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_exit_duplicate");
                    Finish(job, HarborJobStateEnum.Exited, exited.ExitCode, null, persist);
                    return HarborCommandResult.Accept();

                case HarborError error:
                    if (String.IsNullOrWhiteSpace(error.JobId)) return HarborCommandResult.Accept();
                    job = OwnedJob(session, error.JobId, out reason);
                    if (job == null) return HarborCommandResult.Reject(reason);
                    if (HarborJobRecord.IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_exit_duplicate");
                    Finish(job, HarborJobStateEnum.Failed, null, "harbor_runner_error: " + (error.Message ?? String.Empty), persist);
                    return HarborCommandResult.Accept();

                default:
                    return HarborCommandResult.Reject("harbor_message_unexpected");
            }
        }

        private async Task<HarborCommandResult> ReleaseAsync(string jobId, string reason)
        {
            List<HarborJobRecord> persist = new List<HarborJobRecord>();
            lock (_Gate)
            {
                if (!_Jobs.TryGetValue(jobId, out JobRecord? job)) return HarborCommandResult.Reject("harbor_job_unknown");
                if (HarborJobRecord.IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_not_running");
                Finish(job, HarborJobStateEnum.Lost, null, reason, persist);
            }
            await PersistAsync(persist).ConfigureAwait(false);
            return HarborCommandResult.AcceptReleased(reason);
        }

        private async Task<string> ClassifyUnknownJobAsync(HarborRunnerSession session, string jobId)
        {
            if (_Store == null) return "harbor_job_unknown";
            try
            {
                HarborJobRecord? stored = await _Store.ReadAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                if (stored == null) return "harbor_job_unknown";
                return String.Equals(stored.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                    ? "harbor_job_not_rebindable"
                    : "harbor_job_not_owned";
            }
            catch (Exception exception)
            {
                RecordPersistFailure(jobId, "read", exception);
                return ReasonJobStoreUnavailable;
            }
        }

        private JobRecord? OwnedJob(HarborRunnerSession session, string jobId, out string reason)
        {
            reason = String.Empty;
            if (String.IsNullOrWhiteSpace(jobId))
            {
                reason = "harbor_job_id_invalid";
                return null;
            }
            if (!_Jobs.TryGetValue(jobId, out JobRecord? job))
            {
                reason = "harbor_job_unknown";
                return null;
            }
            if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                || job.EnrollmentGeneration != session.EnrollmentGeneration)
            {
                reason = "harbor_job_not_owned";
                return null;
            }
            if (job.SessionGeneration != session.Generation)
            {
                reason = "harbor_job_not_bound";
                return null;
            }
            return job;
        }

        private void Touch(JobRecord job, List<HarborJobRecord>? persist)
        {
            job.Revision++;
            job.LastUpdateUtc = DateTime.UtcNow;
            if (persist != null && job.Durable)
            {
                persist.RemoveAll(existing => String.Equals(existing.JobId, job.JobId, StringComparison.Ordinal));
                persist.Add(job.ToDurable());
                job.PersistedOutputSequence = job.NextOutputSequence;
            }
        }

        private void Finish(JobRecord job, HarborJobStateEnum state, int? exitCode, string? reason, List<HarborJobRecord>? persist)
        {
            job.State = state;
            job.ExitCode = exitCode;
            job.FailureReason = reason;
            job.CompletedUtc = DateTime.UtcNow;
            Touch(job, persist);
            HarborJobSnapshot finished = job.Snapshot();
            job.Completion.TrySetResult(finished);
            Notify(job, observer => observer.OnFinished(finished));
        }

        /// <summary>
        /// Queue a notification behind the job's earlier notifications. Called under the lock, so the queue order is
        /// the order in which the events were accepted; delivery runs after the lock is released.
        /// </summary>
        private void Notify(JobRecord job, Action<IHarborJobObserver> notification)
        {
            IHarborJobObserver? observer = job.Observer;
            if (observer == null) return;
            string jobId = job.JobId;
            job.NotificationTail = job.NotificationTail.ContinueWith(_ =>
            {
                try
                {
                    notification(observer);
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _ObserverFailures);
                    _Logging?.Warn(_Header + "observer for job " + jobId + " failed: " + exception.Message);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private async Task PersistAsync(List<HarborJobRecord> records)
        {
            if (_Store == null || records.Count == 0) return;
            foreach (HarborJobRecord record in records)
            {
                try
                {
                    await _Store.TryUpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordPersistFailure(record.JobId, "update to " + record.State, exception);
                }
            }
        }

        private void RecordPersistFailure(string jobId, string operation, Exception exception)
        {
            Interlocked.Increment(ref _PersistFailures);
            _Logging?.Warn(_Header + "durable " + operation + " failed for job " + jobId + ": " + exception.Message);
        }

        #endregion

        #region Private-Types

        private sealed class RunnerLink
        {
            public HarborRunnerSession Session { get; }
            public int MaxConcurrentJobs { get; }
            public Func<HarborMessage, CancellationToken, Task> Send { get; }

            public RunnerLink(HarborRunnerSession session, int maxConcurrentJobs, Func<HarborMessage, CancellationToken, Task> send)
            {
                Session = session;
                MaxConcurrentJobs = maxConcurrentJobs;
                Send = send;
            }
        }

        private sealed class JobRecord
        {
            public string JobId { get; }
            public string RunnerId { get; }
            public string LaunchKey { get; }
            public string RequestedByTenantId { get; }
            public string RequestedByUserId { get; }
            public string OwnerTenantId { get; }
            public string OwnerUserId { get; }
            public long EnrollmentGeneration { get; }
            public string? MissionId { get; }
            public string? CaptainId { get; }
            public DateTime CreatedUtc { get; } = DateTime.UtcNow;
            public long SessionGeneration { get; set; }
            public HarborJobStateEnum State { get; set; } = HarborJobStateEnum.Pending;
            public int? ProcessId { get; set; }
            public int? ExitCode { get; set; }
            public string? FailureReason { get; set; }
            public long NextOutputSequence { get; set; }
            public long PersistedOutputSequence { get; set; }
            public long Revision { get; set; } = 1;
            public bool Durable { get; set; }
            public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
            public DateTime? CompletedUtc { get; set; }
            public IHarborJobObserver? Observer { get; set; }
            public Task NotificationTail { get; set; } = Task.CompletedTask;
            public List<HarborOutput> Output { get; } = new List<HarborOutput>();
            public TaskCompletionSource<HarborJobSnapshot> Completion { get; } = new TaskCompletionSource<HarborJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            public JobRecord(string jobId, string runnerId, string launchKey, string tenantId, string userId, string ownerTenantId, string ownerUserId,
                long enrollmentGeneration, long sessionGeneration, HarborJobBinding? binding)
            {
                JobId = jobId;
                RunnerId = runnerId;
                LaunchKey = launchKey;
                RequestedByTenantId = tenantId;
                RequestedByUserId = userId;
                OwnerTenantId = ownerTenantId;
                OwnerUserId = ownerUserId;
                EnrollmentGeneration = enrollmentGeneration;
                SessionGeneration = sessionGeneration;
                MissionId = binding?.MissionId;
                CaptainId = binding?.CaptainId;
                Observer = binding?.Observer;
            }

            public HarborJobSnapshot Snapshot()
            {
                return new HarborJobSnapshot(JobId, RunnerId, LaunchKey, RequestedByTenantId, RequestedByUserId,
                    EnrollmentGeneration, SessionGeneration, State, ProcessId, ExitCode, FailureReason, Output.ToArray(),
                    OwnerTenantId, OwnerUserId, MissionId, CaptainId, NextOutputSequence);
            }

            public HarborJobRecord ToDurable()
            {
                return new HarborJobRecord
                {
                    JobId = JobId,
                    RunnerId = RunnerId,
                    LaunchKey = LaunchKey,
                    TenantId = OwnerTenantId,
                    UserId = OwnerUserId,
                    EnrollmentGeneration = EnrollmentGeneration,
                    SessionGeneration = SessionGeneration,
                    State = State,
                    ProcessId = ProcessId,
                    ExitCode = ExitCode,
                    FailureReason = FailureReason,
                    NextOutputSequence = NextOutputSequence,
                    MissionId = MissionId,
                    CaptainId = CaptainId,
                    Revision = Revision,
                    CreatedUtc = CreatedUtc,
                    LastUpdateUtc = LastUpdateUtc,
                    CompletedUtc = CompletedUtc
                };
            }
        }

        #endregion
    }
}
