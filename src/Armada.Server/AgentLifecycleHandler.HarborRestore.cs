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
            string logFilePath = Path.Combine(_Settings.LogDirectory, "missions", mission!.Id + ".log");
            IHarborProcessEvents events = runtime.CreateHarborReattachmentEvents(job.AdmiralProcessId!.Value, logFilePath);
            long stateOwner = RestoreMissionStateOwnership(mission.Id);
            IAgentRuntime runtimeEvents = runtime;
            runtimeEvents.OnOutputReceived += HandleAgentOutput;
            runtimeEvents.OnOutputReceived += HandleAgentHeartbeat;
            runtimeEvents.OnTokenUsageReceived += HandleTokenUsage;
            runtimeEvents.OnProviderProgressReceived += HandleProviderProgress;
            if (runtimeEvents is ApiAgentRuntime compactingRuntime)
                compactingRuntime.OnContextCompacted += HandleContextCompacted;

            IRuntimeProviderErrorSource? providerErrorSource = runtimeEvents as IRuntimeProviderErrorSource;
            if (providerErrorSource != null)
            {
                RuntimeProviderErrorSubscription registration = new RuntimeProviderErrorSubscription(
                    providerErrorSource, (processId, error) => CaptureProviderError(processId, stateOwner, error));
                _ProviderErrorSources[stateOwner] = registration;
                registration.Subscribe();
            }

            if (!ProcessSupervisor.TryRestoreSyntheticProcess(job.AdmiralProcessId.Value,
                stopToken => executor.StopRestoredAsync(job, stopToken)))
            {
                CloseProviderErrorSource(stateOwner);
                ReleaseMissionState(stateOwner, mission.Id);
                events.OnExited(job.AdmiralProcessId.Value, null, "harbor_process_identity_already_owned");
                return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);
            }
            ProcessSupervisor.RecordLaunchedProcess(job.AdmiralProcessId.Value, job.AdmiralProcessStartedUtc!.Value);

            lock (_ProcessToCaptain)
            {
                if (_ProcessToCaptain.ContainsKey(job.AdmiralProcessId.Value)
                    || _ProcessToMission.ContainsKey(job.AdmiralProcessId.Value)
                    || !_ProcessStateOwners.TryAdd(job.AdmiralProcessId.Value, stateOwner))
                {
                    ProcessSupervisor.UnregisterSyntheticProcess(job.AdmiralProcessId.Value);
                    CloseProviderErrorSource(stateOwner);
                    ReleaseMissionState(stateOwner, mission.Id);
                    events.OnExited(job.AdmiralProcessId.Value, null, "harbor_process_identity_already_owned");
                    return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain);
                }
                _ProcessToCaptain[job.AdmiralProcessId.Value] = captain.Id;
                _ProcessToMission[job.AdmiralProcessId.Value] = mission.Id;
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
