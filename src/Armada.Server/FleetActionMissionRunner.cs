namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>Runs durable mission action targets through the shared voyage dispatcher.</summary>
    public sealed partial class FleetActionMissionRunner
    {
        private readonly DatabaseDriver _Database;
        private readonly FleetActionService _Actions;
        private readonly IAdmiralService _Admiral;
        private readonly VoyageDispatchService _Dispatcher;
        private readonly MissionOperations _Operations;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private string? _TickAfterRunId;

        /// <summary>Create the tracked worker used by the server lifecycle.</summary>
        public FleetActionMissionRunner(DatabaseDriver database, FleetActionService actions, IAdmiralService admiral,
            VoyageDispatchService dispatcher, MissionOperations operations, ArmadaSettings settings, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _Operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? new LoggingModule();
        }

        /// <summary>Fence interrupted dispatches and reconcile exact saved voyage references.</summary>
        public async Task RecoverAsync(CancellationToken token = default)
        {
            string? afterId = null;
            while (true)
            {
                List<FleetActionRun> runs = await _Database.FleetActionRuns.EnumerateActiveAsync(100, afterId, token).ConfigureAwait(false);
                foreach (FleetActionRun run in runs)
                {
                    await RecoverRunAsync(run, token).ConfigureAwait(false);
                }
                if (runs.Count < 100) return;
                afterId = runs[runs.Count - 1].Id;
            }
        }

        /// <summary>Process pending targets and update known saved voyage outcomes.</summary>
        public async Task TickAsync(CancellationToken token = default)
        {
            List<FleetActionRun> runs = await _Database.FleetActionRuns.EnumerateActiveAsync(100, _TickAfterRunId, token).ConfigureAwait(false);
            if (runs.Count == 0 && _TickAfterRunId != null)
            {
                _TickAfterRunId = null;
                runs = await _Database.FleetActionRuns.EnumerateActiveAsync(100, null, token).ConfigureAwait(false);
            }
            if (runs.Count > 0) _TickAfterRunId = runs[runs.Count - 1].Id;
            foreach (FleetActionRun run in runs)
            {
                token.ThrowIfCancellationRequested();
                await ReconcileRunAsync(run.TenantId, run.UserId, run.Id, token).ConfigureAwait(false);
                await DispatchPendingAsync(run.TenantId, run.UserId, run.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<FleetActionRun?> SaveStateAsync(FleetActionRun run, FleetActionRunState state, CancellationToken token)
        {
            FleetActionRunStateBuilder.Recalculate(state);
            return await _Database.FleetActionRuns.TryCompareExchangeStateAsync(
                run.TenantId, run.UserId, run.Id, run.Revision, state, token).ConfigureAwait(false);
        }
    }
}
