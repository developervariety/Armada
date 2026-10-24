namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private static void BindOwnedVesselPaths(Vessel? vessel, Dock? dock)
        {
            if (vessel == null || dock?.HostCheckout == null) return;
            if (!dock.HostCheckout.IsComplete) throw new InvalidOperationException("harbor_checkout_binding_invalid");
            string address = HarborCheckoutAddress.ForDock(dock.Id);
            vessel.LocalPath = address + "/repository";
            vessel.WorkingDirectory = address + "/working";
        }

        private async Task RunOwnedGitMutationAsync(Dock? dock, string purpose, string targetPath, Func<Task> operation, CancellationToken token)
        {
            if (dock?.HostCheckout == null)
            {
                await operation().ConfigureAwait(false);
                return;
            }
            string? head = await (_Git ?? throw new InvalidOperationException("harbor_git_provider_unavailable")).GetHeadCommitHashAsync(targetPath, token).ConfigureAwait(false);
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(purpose, head ?? String.Empty))
            {
                await operation().ConfigureAwait(false);
            }
        }
    }
}
