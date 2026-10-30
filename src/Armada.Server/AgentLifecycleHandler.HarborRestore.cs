namespace Armada.Server
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;

    public partial class AgentLifecycleHandler : IHarborJobRecoveryHost
    {
        /// <inheritdoc />
        public async Task<HarborJobRecoveryResult> RestoreAsync(HarborJobSnapshot job, CancellationToken token = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (!HasMissionAttempt(job))
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);

            Mission? mission = await _Database.Missions.ReadAsync(job.MissionId!, token).ConfigureAwait(false);
            Captain? captain = await _Database.Captains.ReadAsync(job.CaptainId!, token).ConfigureAwait(false);
            Dock? dock = await _Database.Docks.ReadAsync(job.DockId!, token).ConfigureAwait(false);
            if (!MatchesActiveAttempt(job, mission, captain, dock))
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);

            if (_HarborHost is not HarborMissionExecutor executor)
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);

            BaseAgentRuntime runtime = CreateHarborAdapter(captain!);
            runtime.HarborExitCompletionWaiter = WaitForProcessExitCompletionAsync;
            string logFilePath = MissionAttemptArtifacts.LogPath(_Settings.LogDirectory, mission!);
            IHarborProcessEvents events = runtime.CreateHarborReattachmentEvents(job.AdmiralProcessId!.Value, logFilePath);
            if (!ProcessSupervisor.TryRestoreSyntheticProcess(job.AdmiralProcessId.Value,
                stopToken => executor.StopRestoredAsync(job, stopToken)))
            {
                (events as IDisposable)?.Dispose();
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);
            }
            long stateOwner;
            try
            {
                stateOwner = await RestoreMissionStateOwnershipAsync(mission.Id, MissionExecutionBoundary.GenerationOf(mission), token).ConfigureAwait(false);
            }
            catch
            {
                ProcessSupervisor.UnregisterSyntheticProcess(job.AdmiralProcessId.Value);
                (events as IDisposable)?.Dispose();
                throw;
            }
            IAgentRuntime runtimeEvents = runtime;
            runtimeEvents.OnOutputReceived += (processId, line) => HandleAgentOutputForLaunch(processId, line, stateOwner);
            runtimeEvents.OnOutputReceived += (processId, line) => HandleAgentHeartbeatForLaunch(processId, line, stateOwner);
            runtimeEvents.OnTokenUsageReceived += (processId, sample) => HandleTokenUsageForLaunch(processId, sample, stateOwner);
            runtimeEvents.OnProviderProgressReceived += (processId, sample) => HandleProviderProgressForLaunch(processId, sample, stateOwner);
            if (runtimeEvents is ApiAgentRuntime compactingRuntime)
                compactingRuntime.OnContextCompacted += (processId, sample) => HandleContextCompactedForLaunch(processId, sample, stateOwner);

            IRuntimeProviderErrorSource? providerErrorSource = runtimeEvents as IRuntimeProviderErrorSource;
            if (providerErrorSource != null)
            {
                RuntimeProviderErrorSubscription registration = new RuntimeProviderErrorSubscription(
                    providerErrorSource, (processId, error) => CaptureProviderError(processId, stateOwner, error));
                _ProviderErrorSources[stateOwner] = registration;
                registration.Subscribe();
            }

            ProcessSupervisor.RecordLaunchedProcess(job.AdmiralProcessId.Value, job.AdmiralProcessStartedUtc!.Value);

            bool registered;
            lock (_ProcessToCaptain)
            {
                registered = !_ProcessToCaptain.ContainsKey(job.AdmiralProcessId.Value)
                    && !_ProcessToMission.ContainsKey(job.AdmiralProcessId.Value)
                    && _ProcessStateOwners.TryAdd(job.AdmiralProcessId.Value, stateOwner);
                if (registered)
                {
                    _ProcessToCaptain[job.AdmiralProcessId.Value] = captain.Id;
                    _ProcessToMission[job.AdmiralProcessId.Value] = mission.Id;
                }
            }
            if (!registered)
            {
                ProcessSupervisor.UnregisterSyntheticProcess(job.AdmiralProcessId.Value);
                CloseProviderErrorSource(stateOwner);
                ReleaseMissionState(stateOwner, mission.Id);
                (events as IDisposable)?.Dispose();
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);
            }

            _HarborProcessIds[job.AdmiralProcessId.Value] = job.JobId;
            _RestoredHarborEvents[job.JobId] = events;
            runtimeEvents.OnProcessExited += (processId, exitCode) =>
            {
                CloseProviderErrorSource(stateOwner);
                HandleAgentProcessExited(processId, exitCode, stateOwner);
            };
            StartProcessLivenessHeartbeat(job.AdmiralProcessId.Value, captain.Id, mission.Id);
            return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Adopted, new RestoredHarborObserver(this, job.JobId, job.AdmiralProcessId.Value, events));
        }
    }
}
