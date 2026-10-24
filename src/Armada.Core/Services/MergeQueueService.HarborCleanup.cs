namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task SyncHarborLandingTargetAsync(MergeEntry entry, Dock integration, string expectedHead, CancellationToken token)
        {
            string target = HarborCheckoutAddress.ForDock(integration.Id) + "/working";
            string? branch = await _Git.GetCurrentBranchAsync(target, token).ConfigureAwait(false);
            if (!String.Equals(branch, entry.TargetBranch, StringComparison.OrdinalIgnoreCase))
            {
                await EmitWorkdirSyncSkippedAsync(entry, target, "on_non_default_branch", token).ConfigureAwait(false);
                return;
            }
            if (await _Git.HasUncommittedTrackedChangesAsync(target, token).ConfigureAwait(false))
            {
                await EmitWorkdirSyncSkippedAsync(entry, target, "dirty_working_directory", token).ConfigureAwait(false);
                return;
            }
            string? head = await _Git.GetHeadCommitHashAsync(target, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_primary_target_head_unavailable");
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            using (new HarborCheckoutOperationScope("mq-sync:" + HarborQueueAttemptId(entry.Id, job.RetryCount) + ":" + head, head!))
                await _Git.PullFastForwardOnlyAsync(target, token).ConfigureAwait(false);
            string? syncedHead = await _Git.GetHeadCommitHashAsync(target, token).ConfigureAwait(false);
            if (!String.Equals(syncedHead, expectedHead, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("harbor_primary_target_sync_unverified");
            await EmitWorkdirSyncedAsync(entry, target, token).ConfigureAwait(false);
        }

        private async Task RemoveHarborIntegrationAsync(MergeEntry entry, Dock integration, CancellationToken token)
        {
            if (!integration.Active || integration.HostCheckout == null) return;
            string address = HarborCheckoutAddress.ForDock(integration.Id);
            string? head = await _Git.GetHeadCommitHashAsync(address, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_integration_checkout_head_unavailable");
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(integration, HarborWorkspaceOperationEnum.RemoveCheckout);
            using (new HarborCheckoutOperationScope("mq-remove:" + HarborQueueAttemptId(job.MergeEntryId, job.RetryCount) + ":" + head, head!))
                await (_HarborCheckouts ?? throw new InvalidOperationException("harbor_checkout_provider_unavailable"))
                    .ExecuteAsync(integration, request, token).ConfigureAwait(false);
            integration.Active = false;
            await _Database.Docks.UpdateAsync(integration, token).ConfigureAwait(false);
        }

        private async Task CleanupHarborBranchesAsync(MergeEntry entry, Dock integration, Vessel vessel, CancellationToken token)
        {
            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            string repository = checkout + "/repository";
            string? repositoryHead = await _Git.GetHeadCommitHashAsync(repository, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(repositoryHead))
                throw new InvalidOperationException("harbor_integration_checkout_head_unavailable");
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            string key = HarborQueueAttemptId(entry.Id, job.RetryCount);
            if (vessel.BranchCleanupPolicy != BranchCleanupPolicyEnum.None && !String.IsNullOrWhiteSpace(entry.BranchName))
            {
                using (new HarborCheckoutOperationScope("mq-delete-branch:" + key + ":" + entry.BranchName, repositoryHead!))
                    await _Git.DeleteLocalBranchAsync(repository, entry.BranchName!, token).ConfigureAwait(false);
                if (vessel.BranchCleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote)
                {
                    string target = checkout + "/working";
                    string? targetHead = await _Git.GetHeadCommitHashAsync(target, token).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(targetHead)) throw new InvalidOperationException("harbor_primary_target_head_unavailable");
                    using (new HarborCheckoutOperationScope("mq-delete-remote-branch:" + key + ":" + entry.BranchName, targetHead!))
                        await _Git.DeleteRemoteBranchAsync(target, entry.BranchName!, token).ConfigureAwait(false);
                }
            }
            using (new HarborCheckoutOperationScope("mq-delete-integration-branch:" + key, repositoryHead!))
            {
                try { await _Git.DeleteLocalBranchAsync(repository, GetIntegrationBranch(entry), token).ConfigureAwait(false); }
                catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
                {
                    _Logging.Warn(_Header + "could not delete Harbor integration branch for " + entry.Id + ": " + exception.Message);
                }
            }
        }

    }
}
