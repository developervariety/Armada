namespace Armada.Server
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class FleetActionMissionRunner
    {
        private async Task DispatchClaimedAsync(FleetActionRun run, FleetActionRunTarget target, AuthContext actor, CancellationToken token)
        {
            VoyageDispatchResult result;
            try
            {
                result = await _Dispatcher.DispatchAsync(new SharedVoyageDispatchRequest
                {
                    Title = run.Snapshot.ActionName + " - " + target.VesselNameSnapshot,
                    Description = "Fleet action " + run.Id + " target " + target.TargetId,
                    VesselId = target.VesselId,
                    Missions = new System.Collections.Generic.List<MissionDescription> { new MissionDescription(run.Snapshot.ActionName, target.RenderedPrompt) },
                    PipelineId = target.ResolvedPipelineId,
                    ObjectiveAuthContext = actor,
                    Settings = _Settings
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await MarkTargetAsync(run, target.TargetId, FleetActionTargetStatusEnum.Uncertain,
                    "Dispatch was interrupted after claim; the result is unknown.", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn("Fleet action dispatch result is unknown for run " + run.Id + " target " + target.TargetId + ": " + ex.Message);
                await MarkTargetAsync(run, target.TargetId, FleetActionTargetStatusEnum.Uncertain,
                    "Dispatch failed after claim; the result is unknown.", CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (!result.Succeeded)
            {
                await MarkTargetAsync(run, target.TargetId, FleetActionTargetStatusEnum.Failed,
                    "The dispatcher refused the saved target with status " + result.StatusCode + ".", CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (result.Voyage == null)
            {
                await MarkTargetAsync(run, target.TargetId, FleetActionTargetStatusEnum.Uncertain,
                    "The dispatcher accepted the target without returning its voyage identifier.", CancellationToken.None).ConfigureAwait(false);
                return;
            }
            try
            {
                bool cancelRequested = await AttachVoyageAsync(run, target.TargetId, result.Voyage.Id, CancellationToken.None).ConfigureAwait(false);
                if (cancelRequested) await CancelExactVoyageAsync(run, target.TargetId, result.Voyage.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn("Fleet action could not save the dispatch result for run " + run.Id + " target " + target.TargetId + ": " + ex.Message);
                await MarkTargetAsync(run, target.TargetId, FleetActionTargetStatusEnum.Uncertain,
                    "Dispatch returned but its exact voyage link could not be saved.", CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task<bool> AttachVoyageAsync(FleetActionRun run, string targetId, string voyageId, CancellationToken token)
        {
            FleetActionRun? current = run;
            while (current != null)
            {
                FleetActionRunState next = FleetActionRunStateBuilder.Clone(current.State);
                FleetActionRunTargetState? target = next.TargetStates.FirstOrDefault(state => state.TargetId == targetId);
                if (target == null || target.Status != FleetActionTargetStatusEnum.Dispatching) return false;
                target.VoyageId = voyageId;
                target.Status = FleetActionTargetStatusEnum.Running;
                target.StartedUtc = DateTime.UtcNow;
                target.LastUpdateUtc = DateTime.UtcNow;
                bool cancelRequested = target.CancelRequested;
                FleetActionRun? saved = await SaveStateAsync(current, next, token).ConfigureAwait(false);
                if (saved != null) return cancelRequested;
                current = await _Database.FleetActionRuns.ReadAsync(run.TenantId, run.UserId, run.Id, token).ConfigureAwait(false);
            }
            return false;
        }

        private async Task CancelExactVoyageAsync(FleetActionRun run, string targetId, string voyageId, CancellationToken token)
        {
            AuthContext? actor = await _Actions.ReadActiveOperatorAsync(run.TenantId, run.UserId, token).ConfigureAwait(false);
            Voyage? voyage = actor == null ? null : await Armada.Core.Authorization.CallerScopedRead.ReadVoyageAsync(
                _Database, actor, voyageId, token).ConfigureAwait(false);
            if (voyage == null) return;
            try
            {
                VoyageCancellationResult result = await _Operations.CancelVoyageAsync(voyage, token).ConfigureAwait(false);
                if (result.Voyage.Status == VoyageStatusEnum.Cancelled)
                    await MarkTargetAsync(run, targetId, FleetActionTargetStatusEnum.Cancelled,
                        "The exact voyage was cancelled after dispatch.", token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn("Fleet action cancellation remains pending for run " + run.Id + " target " + targetId + ": " + ex.Message);
            }
        }
    }
}
