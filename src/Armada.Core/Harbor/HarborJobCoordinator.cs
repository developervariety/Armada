namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Authorizes commands to Harbor runners and binds every job to its runner, its durable enrollment
    /// generation and the connection generation allowed to report for it. Runner events for a job are accepted
    /// only from that connection. Durable revalidation runs before this coordinator's lock is taken; the lock
    /// guards in-memory state only.
    /// </summary>
    public sealed class HarborJobCoordinator
    {
        #region Private-Members

        private readonly object _Gate = new object();
        private readonly HarborRunnerSessionRegistry _Registry;
        private readonly IHarborRunnerAuthority _Authority;
        private readonly Dictionary<string, RunnerLink> _Links = new Dictionary<string, RunnerLink>(StringComparer.Ordinal);
        private readonly Dictionary<string, JobRecord> _Jobs = new Dictionary<string, JobRecord>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate a coordinator over a session registry.</summary>
        /// <param name="registry">Registry that owns runner sessions.</param>
        /// <param name="authority">Shared runner authority rule, the same one enrollment uses.</param>
        public HarborJobCoordinator(HarborRunnerSessionRegistry registry, IHarborRunnerAuthority authority)
        {
            _Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        }

        #endregion

        #region Public-Methods

        /// <summary>Attach the send channel of a newly accepted session. A replacement link supersedes the old one.</summary>
        /// <param name="session">Accepted session.</param>
        /// <param name="maxConcurrentJobs">Capacity advertised in the handshake.</param>
        /// <param name="send">Channel that writes to this link only.</param>
        public void Attach(HarborRunnerSession session, int maxConcurrentJobs, Func<HarborMessage, CancellationToken, Task> send)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (send == null) throw new ArgumentNullException(nameof(send));
            lock (_Gate) _Links[session.Identity.RunnerId] = new RunnerLink(session, Math.Max(1, maxConcurrentJobs), send);
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
        public async Task<HarborLaunchResult> LaunchAsync(AuthContext caller, string runnerId, string launchKey, HarborLaunchRequest request, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(runnerId) || String.IsNullOrWhiteSpace(launchKey) || launchKey.Length > 200
                || request == null || String.IsNullOrWhiteSpace(request.Runtime) || String.IsNullOrWhiteSpace(request.WorkingDirectory))
                return HarborLaunchResult.Reject("harbor_launch_invalid");
            if (!_Registry.TryGetCurrent(runnerId.Trim(), out HarborRunnerSession? session) || session == null)
                return HarborLaunchResult.Reject("harbor_runner_unavailable");
            if (!_Registry.TryRevalidate(session, out string revalidation))
            {
                MarkSessionEnded(session, revalidation);
                return HarborLaunchResult.Reject(revalidation);
            }
            if (!await CanCommandAsync(caller, session.Identity, token).ConfigureAwait(false))
                return HarborLaunchResult.Reject("harbor_command_unauthorized");

            JobRecord record;
            RunnerLink? link;
            lock (_Gate)
            {
                if (!_Links.TryGetValue(session.Identity.RunnerId, out link) || !Object.ReferenceEquals(link.Session, session))
                    return HarborLaunchResult.Reject("harbor_runner_unavailable");
                int live = 0;
                foreach (JobRecord existing in _Jobs.Values)
                {
                    if (IsTerminal(existing.State)) continue;
                    if (String.Equals(existing.LaunchKey, launchKey, StringComparison.Ordinal)
                        && String.Equals(existing.RequestedByTenantId, caller.TenantId, StringComparison.Ordinal))
                        return HarborLaunchResult.Reject("harbor_job_duplicate");
                    if (String.Equals(existing.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)) live++;
                }
                if (live >= link.MaxConcurrentJobs) return HarborLaunchResult.Reject("harbor_runner_capacity_exhausted");
                record = new JobRecord(
                    "hjob_" + Guid.NewGuid().ToString("N"),
                    session.Identity.RunnerId,
                    launchKey,
                    caller.TenantId!,
                    caller.UserId!,
                    session.EnrollmentGeneration,
                    session.Generation);
                _Jobs[record.JobId] = record;
            }

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
                lock (_Gate) Finish(record, HarborJobStateEnum.Failed, null, "harbor_launch_send_failed: " + exception.Message);
                return HarborLaunchResult.Reject("harbor_launch_send_failed");
            }
            return HarborLaunchResult.Accept(record.JobId, record.Completion.Task);
        }

        /// <summary>
        /// Send a stop for a job to the runner and connection that own it. The caller must be authorized for the
        /// owning runner.
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
            lock (_Gate)
            {
                if (!_Jobs.TryGetValue(jobId, out JobRecord? job)) return HarborCommandResult.Reject("harbor_job_unknown");
                if (IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_not_running");
                runnerId = job.RunnerId;
            }
            if (!_Registry.TryGetCurrent(runnerId, out HarborRunnerSession? session) || session == null)
                return HarborCommandResult.Reject("harbor_runner_unavailable");
            if (!_Registry.TryRevalidate(session, out string revalidation))
            {
                MarkSessionEnded(session, revalidation);
                return HarborCommandResult.Reject(revalidation);
            }
            if (!await CanCommandAsync(caller, session.Identity, token).ConfigureAwait(false))
                return HarborCommandResult.Reject("harbor_command_unauthorized");

            RunnerLink? link;
            lock (_Gate)
            {
                JobRecord job = _Jobs[jobId];
                if (IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_not_running");
                if (job.EnrollmentGeneration != session.EnrollmentGeneration || job.SessionGeneration != session.Generation)
                    return HarborCommandResult.Reject("harbor_job_not_bound");
                if (!_Links.TryGetValue(runnerId, out link) || !Object.ReferenceEquals(link.Session, session))
                    return HarborCommandResult.Reject("harbor_runner_unavailable");
                job.State = HarborJobStateEnum.Stopping;
            }
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
        /// Apply a runner event. The event must come from the connection generation bound to the job, and output
        /// must arrive in sequence.
        /// </summary>
        /// <param name="session">Session of the link that delivered the event.</param>
        /// <param name="message">Runner event.</param>
        /// <returns>Event result. <c>harbor_session_stale</c> means the link must be closed.</returns>
        public HarborCommandResult HandleRunnerEvent(HarborRunnerSession session, HarborMessage message)
        {
            if (session == null || message == null) return HarborCommandResult.Reject("harbor_message_invalid");
            lock (_Gate)
            {
                if (!_Registry.IsCurrent(session)) return HarborCommandResult.Reject("harbor_session_stale");
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
                        return HarborCommandResult.Accept();

                    case HarborOutput output:
                        job = OwnedJob(session, output.JobId, out reason);
                        if (job == null) return HarborCommandResult.Reject(reason);
                        if (job.State != HarborJobStateEnum.Running && job.State != HarborJobStateEnum.Stopping)
                            return HarborCommandResult.Reject("harbor_job_not_running");
                        if (output.Sequence < job.NextOutputSequence) return HarborCommandResult.Reject("harbor_output_replayed");
                        if (output.Sequence > job.NextOutputSequence) return HarborCommandResult.Reject("harbor_output_gap");
                        job.Output.Add(new HarborOutput { JobId = job.JobId, Sequence = output.Sequence, Stream = output.Stream, Data = output.Data ?? String.Empty });
                        job.NextOutputSequence++;
                        return HarborCommandResult.Accept();

                    case HarborExited exited:
                        job = OwnedJob(session, exited.JobId, out reason);
                        if (job == null) return HarborCommandResult.Reject(reason);
                        if (IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_exit_duplicate");
                        Finish(job, HarborJobStateEnum.Exited, exited.ExitCode, null);
                        return HarborCommandResult.Accept();

                    case HarborError error:
                        if (String.IsNullOrWhiteSpace(error.JobId)) return HarborCommandResult.Accept();
                        job = OwnedJob(session, error.JobId, out reason);
                        if (job == null) return HarborCommandResult.Reject(reason);
                        if (IsTerminal(job.State)) return HarborCommandResult.Reject("harbor_job_exit_duplicate");
                        Finish(job, HarborJobStateEnum.Failed, null, "harbor_runner_error: " + (error.Message ?? String.Empty));
                        return HarborCommandResult.Accept();

                    default:
                        return HarborCommandResult.Reject("harbor_message_unexpected");
                }
            }
        }

        /// <summary>
        /// Apply a revalidated heartbeat. Reported jobs from the same runner and enrollment generation are rebound
        /// to the reporting connection. Jobs from an earlier enrollment generation are never rebound and become
        /// lost, as do jobs a reconnected runner no longer reports.
        /// </summary>
        /// <param name="session">Session that sent the heartbeat and passed revalidation.</param>
        /// <param name="liveJobIds">Jobs the runner reports alive.</param>
        /// <returns>Heartbeat result.</returns>
        public HarborHeartbeatResult ApplyHeartbeat(HarborRunnerSession session, IReadOnlyCollection<string> liveJobIds)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            List<KeyValuePair<string, string>> rejected = new List<KeyValuePair<string, string>>();
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
                        rejected.Add(new KeyValuePair<string, string>(jobId, "harbor_job_unknown"));
                        continue;
                    }
                    if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal))
                    {
                        rejected.Add(new KeyValuePair<string, string>(jobId, "harbor_job_not_owned"));
                        continue;
                    }
                    if (job.EnrollmentGeneration != session.EnrollmentGeneration || IsTerminal(job.State))
                    {
                        if (!IsTerminal(job.State)) Finish(job, HarborJobStateEnum.Lost, null, "harbor_enrollment_changed");
                        rejected.Add(new KeyValuePair<string, string>(jobId, "harbor_job_not_rebindable"));
                        continue;
                    }
                    if (job.SessionGeneration != session.Generation)
                    {
                        job.SessionGeneration = session.Generation;
                        rebound++;
                    }
                }
                foreach (JobRecord job in _Jobs.Values)
                {
                    if (IsTerminal(job.State) || live.Contains(job.JobId)) continue;
                    if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)) continue;
                    if (job.EnrollmentGeneration != session.EnrollmentGeneration)
                        Finish(job, HarborJobStateEnum.Lost, null, "harbor_enrollment_changed");
                    else if (job.SessionGeneration != session.Generation)
                        Finish(job, HarborJobStateEnum.Lost, null, "harbor_job_not_reported_after_reconnect");
                    else if (job.State != HarborJobStateEnum.Pending)
                        Finish(job, HarborJobStateEnum.Lost, null, "harbor_job_not_reported");
                }
            }
            return new HarborHeartbeatResult(rebound, rejected);
        }

        /// <summary>
        /// Record that a session ended because it failed revalidation. Its link is detached; its jobs become lost
        /// unless a current session with the same enrollment generation can still rebind them.
        /// </summary>
        /// <param name="session">Session that ended.</param>
        /// <param name="reason">Stable reason.</param>
        public void MarkSessionEnded(HarborRunnerSession session, string reason)
        {
            if (session == null) return;
            lock (_Gate)
            {
                if (_Links.TryGetValue(session.Identity.RunnerId, out RunnerLink? link) && Object.ReferenceEquals(link.Session, session))
                    _Links.Remove(session.Identity.RunnerId);
                bool replacementCanRebind = _Registry.TryGetCurrent(session.Identity.RunnerId, out HarborRunnerSession? current)
                    && current != null
                    && current.EnrollmentGeneration == session.EnrollmentGeneration;
                if (replacementCanRebind) return;
                foreach (JobRecord job in _Jobs.Values)
                {
                    if (IsTerminal(job.State)) continue;
                    if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)) continue;
                    if (job.EnrollmentGeneration != session.EnrollmentGeneration) continue;
                    Finish(job, HarborJobStateEnum.Lost, null, String.IsNullOrWhiteSpace(reason) ? "harbor_session_ended" : reason);
                }
            }
        }

        /// <summary>Read an immutable view of a job.</summary>
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

        #endregion

        #region Private-Methods

        /// <summary>
        /// A command is authorized for the runner owner, or for a caller with authority over that owner under the
        /// shared runner authority rule. The durable read runs before this coordinator's lock is taken.
        /// </summary>
        private async Task<bool> CanCommandAsync(AuthContext caller, HarborRunnerIdentity owner, CancellationToken token)
        {
            if (caller == null || owner == null || !caller.IsAuthenticated) return false;
            if (String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId)) return false;
            if (String.Equals(caller.TenantId, owner.TenantId, StringComparison.Ordinal)
                && String.Equals(caller.UserId, owner.UserId, StringComparison.Ordinal)) return true;
            return await _Authority.HasAuthorityOverOwnerAsync(caller, owner.TenantId, owner.UserId, token).ConfigureAwait(false);
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

        private static bool IsTerminal(HarborJobStateEnum state)
        {
            return state == HarborJobStateEnum.Exited || state == HarborJobStateEnum.Failed || state == HarborJobStateEnum.Lost;
        }

        private static void Finish(JobRecord job, HarborJobStateEnum state, int? exitCode, string? reason)
        {
            job.State = state;
            job.ExitCode = exitCode;
            job.FailureReason = reason;
            job.Completion.TrySetResult(job.Snapshot());
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
            public long EnrollmentGeneration { get; }
            public long SessionGeneration { get; set; }
            public HarborJobStateEnum State { get; set; } = HarborJobStateEnum.Pending;
            public int? ProcessId { get; set; }
            public int? ExitCode { get; set; }
            public string? FailureReason { get; set; }
            public long NextOutputSequence { get; set; }
            public List<HarborOutput> Output { get; } = new List<HarborOutput>();
            public TaskCompletionSource<HarborJobSnapshot> Completion { get; } = new TaskCompletionSource<HarborJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            public JobRecord(string jobId, string runnerId, string launchKey, string tenantId, string userId, long enrollmentGeneration, long sessionGeneration)
            {
                JobId = jobId;
                RunnerId = runnerId;
                LaunchKey = launchKey;
                RequestedByTenantId = tenantId;
                RequestedByUserId = userId;
                EnrollmentGeneration = enrollmentGeneration;
                SessionGeneration = sessionGeneration;
            }

            public HarborJobSnapshot Snapshot()
            {
                return new HarborJobSnapshot(JobId, RunnerId, LaunchKey, RequestedByTenantId, RequestedByUserId,
                    EnrollmentGeneration, SessionGeneration, State, ProcessId, ExitCode, FailureReason, Output.ToArray());
            }
        }

        #endregion
    }
}
