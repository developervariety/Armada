namespace Armada.Server
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class FleetActionMissionRunner
    {
        private async Task DispatchPendingAsync(string tenantId, string userId, string runId, CancellationToken token)
        {
            for (int index = 0; index < 50; index++)
            {
                FleetActionRun? run = await _Database.FleetActionRuns.ReadAsync(tenantId, userId, runId, token).ConfigureAwait(false);
                if (run == null || run.State.Status is FleetActionRunStatusEnum.Uncertain or FleetActionRunStatusEnum.Cancelled) return;
                if (run.State.TargetStates.Exists(state => state.Status == FleetActionTargetStatusEnum.Uncertain)) return;
                if (CountActiveTargets(run) >= run.Snapshot.Concurrency) return;
                FleetActionRunTarget? snapshot = run.Snapshot.Targets.FirstOrDefault(target => run.State.TargetStates.Any(state =>
                    state.TargetId == target.TargetId && state.Status == FleetActionTargetStatusEnum.Pending));
                if (snapshot == null) return;
                AuthContext? actor = await _Actions.ReadActiveOperatorAsync(tenantId, userId, token).ConfigureAwait(false);
                if (actor == null)
                {
                    await MarkTargetAsync(run, snapshot.TargetId, FleetActionTargetStatusEnum.Skipped,
                        "The initiating operator is no longer active or globally authorized.", token).ConfigureAwait(false);
                    continue;
                }
                Vessel? vessel = await CallerScopedRead.ReadVesselAsync(_Database, actor, snapshot.VesselId, token).ConfigureAwait(false);
                if (vessel == null
                    || (vessel.TenantId ?? Armada.Core.Constants.DefaultTenantId) != snapshot.VesselTenantIdSnapshot
                    || (vessel.UserId ?? Armada.Core.Constants.DefaultUserId) != snapshot.VesselUserIdSnapshot)
                {
                    await MarkTargetAsync(run, snapshot.TargetId, FleetActionTargetStatusEnum.Skipped,
                        "The snapshotted vessel is no longer available with the same owner.", token).ConfigureAwait(false);
                    continue;
                }
                Pipeline? pipeline = await _Admiral.ResolvePipelineAsync(run.Snapshot.PipelineId, vessel, token).ConfigureAwait(false);
                string? pipelineId = pipeline?.Id;
                string? pipelineHash = pipeline == null ? null : FleetActionSnapshotBuilder.HashPipeline(pipeline);
                if (pipelineId != snapshot.ResolvedPipelineId || pipelineHash != snapshot.ResolvedPipelineHash)
                {
                    await MarkTargetAsync(run, snapshot.TargetId, FleetActionTargetStatusEnum.Skipped,
                        "The effective pipeline changed after this run was saved.", token).ConfigureAwait(false);
                    continue;
                }
                run = await ClaimAsync(run, snapshot.TargetId, token).ConfigureAwait(false);
                if (run == null) return;
                await DispatchClaimedAsync(run, snapshot, actor, token).ConfigureAwait(false);
            }
        }

        private async Task<FleetActionRun?> ClaimAsync(FleetActionRun run, string targetId, CancellationToken token)
        {
            FleetActionRunState next = FleetActionRunStateBuilder.Clone(run.State);
            if (next.TargetStates.Exists(state => state.Status == FleetActionTargetStatusEnum.Uncertain)
                || CountActiveTargets(next) >= run.Snapshot.Concurrency) return null;
            FleetActionRunTargetState? target = next.TargetStates.FirstOrDefault(state => state.TargetId == targetId);
            if (target == null || target.Status != FleetActionTargetStatusEnum.Pending) return null;
            target.Status = FleetActionTargetStatusEnum.Dispatching;
            target.DispatchStartedUtc = DateTime.UtcNow;
            target.LastUpdateUtc = DateTime.UtcNow;
            FleetActionRun? claimed = await SaveStateAsync(run, next, token).ConfigureAwait(false);
            return claimed;
        }

        private static int CountActiveTargets(FleetActionRun run) => CountActiveTargets(run.State);

        private static int CountActiveTargets(FleetActionRunState state)
        {
            return state.TargetStates.FindAll(target => target.Status is FleetActionTargetStatusEnum.Dispatching or FleetActionTargetStatusEnum.Running).Count;
        }


        private async Task MarkTargetAsync(FleetActionRun run, string targetId, FleetActionTargetStatusEnum status, string reason, CancellationToken token)
        {
            FleetActionRun? current = await _Database.FleetActionRuns.ReadAsync(run.TenantId, run.UserId, run.Id, token).ConfigureAwait(false);
            while (current != null)
            {
                FleetActionRunState next = FleetActionRunStateBuilder.Clone(current.State);
                FleetActionRunTargetState? target = next.TargetStates.FirstOrDefault(state => state.TargetId == targetId);
                if (target == null || target.Status is FleetActionTargetStatusEnum.Uncertain or FleetActionTargetStatusEnum.Cancelled) return;
                FleetActionRunStateBuilder.SetTarget(target, status, reason);
                FleetActionRun? saved = await SaveStateAsync(current, next, token).ConfigureAwait(false);
                if (saved != null) return;
                current = await _Database.FleetActionRuns.ReadAsync(run.TenantId, run.UserId, run.Id, token).ConfigureAwait(false);
            }
        }
    }
}
