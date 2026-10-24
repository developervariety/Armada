namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Retains Harbor checkout identity until durable removal is confirmed.</summary>
    public partial class MissionService
    {
        private async Task ReclaimAndDeleteUncommittedDockAsync(Mission mission, Dock dock)
        {
            await _Docks.ReclaimAsync(dock.Id, null, CancellationToken.None).ConfigureAwait(false);
            if (dock.HostCheckout != null)
            {
                Dock? current = await _Database.Docks.ReadAsync(dock.Id, CancellationToken.None).ConfigureAwait(false);
                if (!HarborRemovalIsConfirmed(dock, current))
                {
                    _Logging.Warn(_Header + "retaining Harbor checkout identity for dock " + dock.Id
                        + " after uncommitted assignment cleanup could not confirm remote removal for mission " + mission.Id);
                    return;
                }
                await ClearHarborProvisionAttemptAfterConfirmedReclaimAsync(mission, dock, CancellationToken.None).ConfigureAwait(false);
                Mission? latest = !String.IsNullOrWhiteSpace(mission.TenantId)
                    ? await _Database.Missions.ReadAsync(mission.TenantId!, mission.Id, CancellationToken.None).ConfigureAwait(false)
                    : null;
                if (latest == null) latest = await _Database.Missions.ReadAsync(mission.Id, CancellationToken.None).ConfigureAwait(false);
                if (latest != null && String.Equals(latest.DockProvisionAttemptId, dock.ProvisionAttemptId, StringComparison.Ordinal))
                {
                    _Logging.Warn(_Header + "retaining confirmed Harbor checkout row " + dock.Id
                        + " because its mission provision-attempt fence could not be cleared");
                    return;
                }
            }
            await _Database.Docks.DeleteAsync(dock.Id, CancellationToken.None).ConfigureAwait(false);
        }

        private static bool HarborRemovalIsConfirmed(Dock expected, Dock? current)
        {
            if (expected.HostCheckout == null || current?.HostCheckout == null || current.Active) return false;
            HarborCheckoutBinding expectedBinding = expected.HostCheckout;
            HarborCheckoutBinding currentBinding = current.HostCheckout;
            if (!String.Equals(expectedBinding.Revision, currentBinding.Revision, StringComparison.Ordinal)
                || !String.Equals(expectedBinding.CheckoutId, currentBinding.CheckoutId, StringComparison.Ordinal)
                || !String.Equals(expectedBinding.RunnerId, currentBinding.RunnerId, StringComparison.Ordinal)
                || expectedBinding.EnrollmentGeneration != currentBinding.EnrollmentGeneration
                || !String.Equals(expectedBinding.TenantId, currentBinding.TenantId, StringComparison.Ordinal)
                || !String.Equals(expectedBinding.UserId, currentBinding.UserId, StringComparison.Ordinal)
                || !String.Equals(expectedBinding.RepositoryGrantId, currentBinding.RepositoryGrantId, StringComparison.Ordinal)
                || !String.Equals(expectedBinding.RepositoryFingerprint, currentBinding.RepositoryFingerprint, StringComparison.Ordinal)
                || !String.Equals(expectedBinding.BaseCommit, currentBinding.BaseCommit, StringComparison.Ordinal)
                || !String.Equals(expectedBinding.BranchName, currentBinding.BranchName, StringComparison.Ordinal))
                return false;
            HarborCheckoutLifecycleIntent? intent = current.HostCheckoutLifecycle;
            return intent != null
                && String.Equals(intent.BindingRevision, expectedBinding.Revision, StringComparison.Ordinal)
                && intent.Phase == HarborCheckoutLifecyclePhaseEnum.Terminal
                && intent.RemovalConfirmed;
        }
    }
}
