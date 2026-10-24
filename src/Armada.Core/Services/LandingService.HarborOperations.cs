namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class LandingService
    {
        private async Task FetchHarborRepositoryAsync(string repository, string workflowId, CancellationToken token)
        {
            string? head = await _Git.GetHeadCommitHashAsync(repository, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_repository_head_unavailable");
            using (new HarborCheckoutOperationScope("landing-fetch:" + workflowId + ":" + head, head!))
                await _Git.FetchAsync(repository, token).ConfigureAwait(false);
        }

        private async Task<bool> VerifyHarborWorkLandedAsync(string repository, Mission mission, string targetBranch, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.CommitHash)) return false;
            bool? landed = await _Git.TryIsAncestorAsync(repository, mission.CommitHash!, "refs/heads/" + targetBranch, token).ConfigureAwait(false);
            return landed == true;
        }

        private async Task<bool> SyncHarborWorkingDirectoryAsync(Vessel vessel, string target, string repository,
            string targetBranch, string attemptId, CancellationToken token)
        {
            string? branch = await _Git.GetCurrentBranchAsync(target, token).ConfigureAwait(false);
            if (!String.Equals(branch, targetBranch, StringComparison.OrdinalIgnoreCase)) return vessel.LandingMode != LandingModeEnum.LocalMerge;
            if (await _Git.HasUncommittedTrackedChangesAsync(target, token).ConfigureAwait(false))
                return vessel.LandingMode != LandingModeEnum.LocalMerge;
            string? head = await _Git.GetHeadCommitHashAsync(target, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) return false;
            using (new HarborCheckoutOperationScope("landing-sync:" + attemptId, head!))
            {
                if (vessel.LandingMode == LandingModeEnum.LocalMerge)
                    await _Git.MergeBranchLocalAsync(target, repository, targetBranch, targetBranch, token: token).ConfigureAwait(false);
                else
                    await _Git.PullFastForwardOnlyAsync(target, token).ConfigureAwait(false);
            }
            return true;
        }

        private async Task RemoveHarborIntegrationAsync(HarborCheckoutService checkouts, Dock integration,
            string attemptId, CancellationToken token)
        {
            if (integration.HostCheckout == null || !integration.Active) return;
            string address = HarborCheckoutAddress.ForDock(integration.Id);
            string? head = await _Git.GetHeadCommitHashAsync(address, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_integration_checkout_head_unavailable");
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(integration, HarborWorkspaceOperationEnum.RemoveCheckout);
            using (new HarborCheckoutOperationScope("landing-remove:" + attemptId, head!))
                await checkouts.ExecuteAsync(integration, request, token).ConfigureAwait(false);
            integration.Active = false;
            await _Database.Docks.UpdateAsync(integration, token).ConfigureAwait(false);
        }
    }
}
