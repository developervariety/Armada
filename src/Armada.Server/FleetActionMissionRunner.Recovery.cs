namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class FleetActionMissionRunner
    {
        private async Task FenceRecoveredStateAsync(FleetActionRun run, FleetActionRunState next, CancellationToken token)
        {
            if (await SaveStateAsync(run, next, token).ConfigureAwait(false) != null) return;
            FleetActionRun? current = await _Database.FleetActionRuns.ReadAsync(run.TenantId, run.UserId, run.Id, token).ConfigureAwait(false);
            if (current != null && current.State.TargetStates.Exists(target => target.Status == FleetActionTargetStatusEnum.Dispatching))
                throw new InvalidOperationException("Startup could not fence an interrupted mission action dispatch. Resolve the concurrent writer before restarting.");
        }
    }
}
