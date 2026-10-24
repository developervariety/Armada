namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Clears a Harbor provision attempt only after exact remote removal is durable.</summary>
    public partial class MissionService
    {
        private async Task ClearHarborProvisionAttemptAfterConfirmedReclaimAsync(
            Mission mission, Dock dock, CancellationToken token)
        {
            if (dock.HostCheckout == null
                || !String.Equals(mission.DockProvisionAttemptId, dock.ProvisionAttemptId, StringComparison.Ordinal)) return;
            try
            {
                Dock? currentDock = await _Database.Docks.ReadAsync(dock.TenantId!, dock.Id, token).ConfigureAwait(false);
                if (!HarborRemovalIsConfirmed(dock, currentDock)) return;
                Mission? latest = !String.IsNullOrWhiteSpace(mission.TenantId)
                    ? await _Database.Missions.ReadAsync(mission.TenantId!, mission.Id, token).ConfigureAwait(false)
                    : null;
                if (latest == null) latest = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                if (latest == null || !String.Equals(latest.DockProvisionAttemptId, dock.ProvisionAttemptId, StringComparison.Ordinal)) return;
                Armada.Core.Enums.MissionStatusEnum expectedStatus = latest.Status;
                latest.DockProvisionAttemptId = null;
                latest.LastUpdateUtc = DateTime.UtcNow;
                if (!await _Database.Missions.TryUpdateIfStatusAsync(latest, expectedStatus, token).ConfigureAwait(false)) return;
                mission.DockProvisionAttemptId = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _Logging.Warn(_Header + "cannot confirm Harbor checkout reclaim for mission " + mission.Id
                    + "; its provision attempt remains fenced: " + exception.Message);
            }
        }
    }
}
