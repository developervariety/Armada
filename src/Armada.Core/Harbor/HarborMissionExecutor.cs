namespace Armada.Core.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Models;

    /// <summary>
    /// Runs mission launches on Harbor runners through the job coordinator. Each job gets a synthetic process
    /// identifier registered with <see cref="ProcessSupervisor"/> together with its stop, so the mission lifecycle,
    /// the health check, stall detection and recovery use the same rules they use for a local process. Dock
    /// provisioning, the branch and landing stay on the Admiral: a runner only runs the process.
    /// </summary>
    public sealed class HarborMissionExecutor : IHarborProcessHost
    {
        #region Private-Members

        private readonly string _Header = "[HarborMissionExecutor] ";
        private readonly HarborJobCoordinator _Coordinator;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="coordinator">Job coordinator.</param>
        /// <param name="logging">Logging module.</param>
        public HarborMissionExecutor(HarborJobCoordinator coordinator, LoggingModule logging)
        {
            _Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<int> LaunchAsync(HarborProcessLaunch launch, IHarborProcessEvents events, CancellationToken token = default)
        {
            if (launch == null) throw new ArgumentNullException(nameof(launch));
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (String.IsNullOrWhiteSpace(launch.RunnerId) || String.IsNullOrWhiteSpace(launch.LaunchKey) || launch.Request == null)
                throw new HarborLaunchException("harbor_launch_invalid");
            if (String.IsNullOrWhiteSpace(launch.OwnerTenantId) || String.IsNullOrWhiteSpace(launch.OwnerUserId))
                throw new HarborLaunchException("harbor_mission_owner_missing");

            // The mission owner is the only principal a mission job runs for; the coordinator refuses a runner
            // enrolled to anyone else.
            AuthContext owner = AuthContext.Authenticated(launch.OwnerTenantId, launch.OwnerUserId, false, false, "HarborMission", null);
            int processId = ProcessSupervisor.AllocateSyntheticProcessId();
            MissionJobObserver observer = new MissionJobObserver(processId, events, _Logging, _Header);

            // Registered before the launch is sent, so an exit the runner reports at once cannot arrive before the
            // registration it removes.
            ProcessSupervisor.RegisterSyntheticProcess(processId, stopToken => StopAsync(owner, observer, stopToken));
            HarborLaunchResult result;
            try
            {
                result = await _Coordinator.LaunchAsync(owner, launch.RunnerId, launch.LaunchKey, launch.Request, new HarborJobBinding
                {
                    MissionId = launch.MissionId,
                    CaptainId = launch.CaptainId,
                    Observer = observer
                }, token).ConfigureAwait(false);
            }
            catch
            {
                ProcessSupervisor.UnregisterSyntheticProcess(processId);
                throw;
            }
            if (!result.Accepted)
            {
                ProcessSupervisor.UnregisterSyntheticProcess(processId);
                throw new HarborLaunchException(result.Reason);
            }

            observer.JobId = result.JobId;
            _Logging.Info(_Header + "mission " + (launch.MissionId ?? "(none)") + " runs on runner " + launch.RunnerId + " as job " + result.JobId + " (process " + processId + ")");
            return processId;
        }

        #endregion

        #region Private-Methods

        private async Task StopAsync(AuthContext owner, MissionJobObserver observer, CancellationToken token)
        {
            string? jobId = observer.JobId;
            if (String.IsNullOrWhiteSpace(jobId))
            {
                _Logging.Warn(_Header + "stop for process " + observer.ProcessId + " arrived before its job was accepted; nothing was sent");
                return;
            }
            HarborCommandResult stopped = await _Coordinator.StopAsync(owner, jobId, 10000, token).ConfigureAwait(false);
            if (!stopped.Accepted)
                _Logging.Warn(_Header + "stop for job " + jobId + " was refused: " + stopped.Reason);
            else if (!String.IsNullOrEmpty(stopped.Reason))
                _Logging.Info(_Header + "stop for job " + jobId + ": " + stopped.Reason);
        }

        #endregion

        #region Private-Types

        private sealed class MissionJobObserver : IHarborJobObserver
        {
            private readonly IHarborProcessEvents _Events;
            private readonly LoggingModule _Logging;
            private readonly string _Header;

            public int ProcessId { get; }

            public string? JobId { get; set; }

            public MissionJobObserver(int processId, IHarborProcessEvents events, LoggingModule logging, string header)
            {
                ProcessId = processId;
                _Events = events;
                _Logging = logging;
                _Header = header;
            }

            public void OnStarted(HarborJobSnapshot job)
            {
                _Logging.Info(_Header + "job " + job.JobId + " started on runner " + job.RunnerId + " as host process " + job.ProcessId);
            }

            public void OnOutput(string jobId, HarborOutput output)
            {
                _Events.OnOutput(ProcessId, output.Stream, output.Data ?? String.Empty);
            }

            public void OnFinished(HarborJobSnapshot job)
            {
                try
                {
                    bool exited = job.State == HarborJobStateEnum.Exited;
                    string? reason = exited ? null : (job.FailureReason ?? "harbor_job_" + job.State.ToString().ToLowerInvariant());
                    if (!exited) _Logging.Warn(_Header + "job " + job.JobId + " ended " + job.State + ": " + reason);
                    _Events.OnExited(ProcessId, exited ? job.ExitCode : null, reason);
                }
                finally
                {
                    ProcessSupervisor.UnregisterSyntheticProcess(ProcessId);
                }
            }
        }

        #endregion
    }
}
