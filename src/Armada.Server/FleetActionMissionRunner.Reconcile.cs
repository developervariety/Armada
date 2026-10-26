namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class FleetActionMissionRunner
    {
        private async Task ReconcileRunAsync(string tenantId, string userId, string runId, CancellationToken token)
        {
            FleetActionRun? run = await _Database.FleetActionRuns.ReadAsync(tenantId, userId, runId, token).ConfigureAwait(false);
            if (run == null) return;
            AuthContext? actor = await _Actions.ReadActiveOperatorAsync(tenantId, userId, token).ConfigureAwait(false);
            foreach (FleetActionRunTargetState target in run.State.TargetStates)
            {
                if (target.Status != FleetActionTargetStatusEnum.Running || String.IsNullOrWhiteSpace(target.VoyageId)) continue;
                if (target.CancelRequested && actor != null)
                {
                    await CancelRequestedTargetAsync(run, actor, target, token).ConfigureAwait(false);
                    continue;
                }
                Voyage? voyage = await _Database.Voyages.ReadAsync(tenantId, userId, target.VoyageId, token).ConfigureAwait(false);
                if (voyage == null)
                {
                    await MarkTargetAsync(run, target.TargetId, FleetActionTargetStatusEnum.Uncertain,
                        "The exact saved voyage is no longer readable; its outcome is unknown.", token).ConfigureAwait(false);
                    continue;
                }
                if (voyage.Status is VoyageStatusEnum.Open or VoyageStatusEnum.InProgress) continue;
                FleetActionTargetStatusEnum status = await ClassifyVoyageAsync(voyage, token).ConfigureAwait(false);
                string reason = status == FleetActionTargetStatusEnum.Completed
                    ? "The voyage is complete; no source-producing mission work is confirmed."
                    : status == FleetActionTargetStatusEnum.WorkProduced
                        ? "Source-producing work exists, but a current-attempt landing is not confirmed."
                        : "The saved voyage reached a known terminal state.";
                await MarkTargetAsync(run, target.TargetId, status, reason, token).ConfigureAwait(false);
            }
        }

        private async Task<FleetActionTargetStatusEnum> ClassifyVoyageAsync(Voyage voyage, CancellationToken token)
        {
            if (voyage.Status == VoyageStatusEnum.Cancelled) return FleetActionTargetStatusEnum.Cancelled;
            if (voyage.Status == VoyageStatusEnum.Failed) return FleetActionTargetStatusEnum.Failed;
            List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            if (missions.Count == 0) return FleetActionTargetStatusEnum.Uncertain;
            bool work = missions.Exists(mission => !String.IsNullOrWhiteSpace(mission.CommitHash)
                || mission.Status is MissionStatusEnum.WorkProduced or MissionStatusEnum.PullRequestOpen);
            if (!work) return FleetActionTargetStatusEnum.Completed;
            bool delivered = true;
            foreach (Mission mission in missions)
            {
                bool sourceProducing = !String.IsNullOrWhiteSpace(mission.CommitHash)
                    || mission.Status is MissionStatusEnum.WorkProduced or MissionStatusEnum.PullRequestOpen;
                if (!sourceProducing) continue;
                if (mission.Status != MissionStatusEnum.Complete || String.IsNullOrWhiteSpace(mission.TenantId)) { delivered = false; continue; }
                ProductionFactPage<MissionAttemptFact> page = await _Database.MissionAttemptFacts
                    .EnumerateByMissionAsync(mission.TenantId, mission.Id, 1000, token).ConfigureAwait(false);
                int lastAttempt = page.Items.FindLastIndex(fact => fact.FactType is MissionAttemptFactTypeEnum.AttemptStarted
                    or MissionAttemptFactTypeEnum.Retried or MissionAttemptFactTypeEnum.Restarted);
                if (!page.Items.Skip(lastAttempt + 1).Any(fact => fact.FactType == MissionAttemptFactTypeEnum.Landed)) delivered = false;
            }
            return delivered ? FleetActionTargetStatusEnum.Delivered : FleetActionTargetStatusEnum.WorkProduced;
        }
    }
}
