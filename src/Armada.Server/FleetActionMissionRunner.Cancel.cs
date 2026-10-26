namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class FleetActionMissionRunner
    {
        private async Task CancelRequestedTargetAsync(FleetActionRun run, AuthContext actor, FleetActionRunTargetState target, CancellationToken token)
        {
            Voyage? voyage = await CallerScopedRead.ReadVoyageAsync(_Database, actor, target.VoyageId, token).ConfigureAwait(false);
            if (voyage == null) return;
            try
            {
                VoyageCancellationResult canceled = await _Operations.CancelVoyageAsync(voyage, token).ConfigureAwait(false);
                if (canceled.Voyage.Status == VoyageStatusEnum.Cancelled)
                {
                    await MarkTargetAsync(run, target.TargetId, FleetActionTargetStatusEnum.Cancelled,
                        "The saved voyage was cancelled.", token).ConfigureAwait(false);
                }
                else if (canceled.Voyage.Status is VoyageStatusEnum.Complete or VoyageStatusEnum.Failed)
                {
                    FleetActionTargetStatusEnum status = await ClassifyVoyageAsync(canceled.Voyage, token).ConfigureAwait(false);
                    await MarkTargetAsync(run, target.TargetId, status, "The saved voyage finished before cancellation.", token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _Logging.Warn("Fleet action cancellation remains pending for run " + run.Id + " target " + target.TargetId + ": " + ex.Message);
            }
        }
    }
}
