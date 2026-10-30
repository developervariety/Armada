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

    public partial class AgentLifecycleHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IHarborProcessEvents> _RestoredHarborEvents =
            new System.Collections.Concurrent.ConcurrentDictionary<string, IHarborProcessEvents>(StringComparer.Ordinal);
        private readonly KeyedAsyncLock _HarborCompletionDeliveryLocks = new KeyedAsyncLock(StringComparer.Ordinal);

        /// <inheritdoc />
        public async Task<bool> DeliverCompletionAsync(HarborJobSnapshot job, CancellationToken token = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            using (IDisposable deliveryLock = await _HarborCompletionDeliveryLocks.AcquireAsync(job.JobId, token).ConfigureAwait(false))
            {
                return await DeliverCompletionCoreAsync(job, token).ConfigureAwait(false);
            }
        }

        private async Task<bool> DeliverCompletionCoreAsync(HarborJobSnapshot job, CancellationToken token)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (!HarborJobRecord.IsTerminal(job.State) || !job.StopConfirmed) return false;
            if (!HasMissionAttempt(job))
                return await TryAccountLegacyOperatorStoppedJobAsync(job, token).ConfigureAwait(false);

            Mission? mission = await _Database.Missions.ReadAsync(job.MissionId!, token).ConfigureAwait(false);
            Captain? captain = await _Database.Captains.ReadAsync(job.CaptainId!, token).ConfigureAwait(false);
            Dock? dock = await _Database.Docks.ReadAsync(job.DockId!, token).ConfigureAwait(false);
            if (mission == null || captain == null || dock == null)
                return false;
            if (!String.Equals(mission.HarborJobId, job.JobId, StringComparison.Ordinal))
            {
                if (!String.IsNullOrWhiteSpace(mission.HarborJobId))
                {
                    _Logging.Info(_Header + "confirmed Harbor result for stale job " + job.JobId + " does not match the current mission attempt; leaving the newer attempt unchanged");
                    return true;
                }
                return false;
            }
            HarborCompletionIntent? completionIntent;
            try
            {
                completionIntent = await _Database.HarborJobs.ReadCompletionIntentAsync(job.JobId, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
            {
                _Logging.Warn(_Header + "could not read completion intent for Harbor job " + job.JobId + ": " + exception.Message);
                return false;
            }
            if (completionIntent != null)
            {
                if (!MatchesDurableJobOwnership(job, mission, captain, dock)) return false;
                if (completionIntent.Completed) return true;
                if (_Admiral is IHarborCompletionRecovery completionRecovery)
                    return await completionRecovery.ResumeConfirmedCompletionAsync(job, token).ConfigureAwait(false);
                return false;
            }
            if (mission.Status == MissionStatusEnum.Pending
                && String.IsNullOrWhiteSpace(mission.CaptainId)
                && String.IsNullOrWhiteSpace(mission.DockId)
                && !mission.ProcessId.HasValue)
            {
                _Logging.Warn(_Header + "Harbor job " + job.JobId + " has no completion intent for its partially requeued mission; keeping the recovery fence");
                return false;
            }
            if (Armada.Core.Services.AdmiralService.IsPostWorkOrTerminal(mission!.Status)
                && mission.ProcessId != job.AdmiralProcessId
                && MatchesDurableJobOwnership(job, mission, captain, dock))
            {
                _Logging.Info(_Header + "confirmed Harbor result for job " + job.JobId
                    + " matches the mission's already-recorded post-work outcome; acknowledging without changing the current captain state");
                return true;
            }
            if (!MatchesAttemptOwnership(job, mission, captain, dock)) return false;
            if (mission!.ProcessId != job.AdmiralProcessId || captain!.ProcessId != job.AdmiralProcessId
                || mission.ProcessStartedUtc != job.AdmiralProcessStartedUtc
                || captain.ProcessStartedUtc != job.AdmiralProcessStartedUtc)
            {
                return false;
            }
            if (mission.Status != MissionStatusEnum.Assigned && mission.Status != MissionStatusEnum.InProgress
                && !String.Equals(captain.CurrentMissionId, mission.Id, StringComparison.Ordinal))
                return false;

            int processId = job.AdmiralProcessId!.Value;
            long stateOwner = await RestoreMissionStateOwnershipAsync(mission!.Id, MissionExecutionBoundary.GenerationOf(mission), token).ConfigureAwait(false);
            ProcessSupervisor.UnregisterSyntheticProcess(processId);
            _HarborProcessIds.TryRemove(processId, out _);
            int exitCode = job.State == HarborJobStateEnum.Exited && job.ExitCode.HasValue ? job.ExitCode.Value : -1;
            try
            {
                RuntimeProcessExitEvidence? exitEvidence = ProcessSupervisor.GetRuntimeExitEvidence(processId);
                await HandleAgentProcessExitedAsync(processId, exitCode, captain!.Id, mission.Id, null, stateOwner, true, exitEvidence).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not deliver confirmed Harbor completion for job " + job.JobId + ": " + ex.Message);
                return false;
            }
        }
    }
}
