namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public partial class ArmadaServer
    {
        private FleetActionService _FleetActions = null!;
        private FleetActionMissionRunner _FleetActionRunner = null!;
        private Task? _FleetActionTask;
        private CancellationTokenSource? _FleetActionCancellation;

        private async Task InitializeFleetActionsAsync()
        {
            _FleetActions = new FleetActionService(_Database, _Admiral);
            VoyageDispatchService dispatcher = new VoyageDispatchService(_Database, _Admiral, _Logging,
                _CodeIndex, _ObjectiveService, _Settings, _ObjectiveDispatchPreviewService, _DispatchStalenessAdapter);
            _FleetActionRunner = new FleetActionMissionRunner(_Database, _FleetActions, _Admiral,
                dispatcher, _MissionOperations, _Settings, _Logging);
            await _FleetActionRunner.RecoverAsync(_TokenSource.Token).ConfigureAwait(false);
            _FleetActionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_TokenSource.Token);
        }

        private void StartFleetActionLoop()
        {
            _FleetActionTask = PeriodicSweepRunner.RunAsync(_FleetActionRunner.TickAsync,
                () => TimeSpan.FromSeconds(2),
                ex => _Logging.Warn(_Header + "saved mission action sweep failed: " + ex.Message),
                _FleetActionCancellation!.Token);
        }

        private void StopFleetActionLoop()
        {
            _FleetActionCancellation?.Cancel();
            WaitForBackgroundLoop("saved mission action loop", _FleetActionTask);
            _FleetActionCancellation?.Dispose();
            _FleetActionCancellation = null;
        }
    }
}
