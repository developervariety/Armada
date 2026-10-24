namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class MergeQueueService
    {
        private async Task CleanupDeletedHarborEntryAsync(MergeEntry entry, HarborQueueContext context, CancellationToken token)
        {
            HarborCheckoutService checkouts = _HarborCheckouts
                ?? throw new InvalidOperationException("harbor_checkout_provider_unavailable");
            Dock? integration = await ReadHarborIntegrationAsync(entry, token).ConfigureAwait(false);
            if (integration == null || !integration.Active)
            {
                HarborWorkspaceOperationResult target = await checkouts.InspectRepositoryAsync(context.Vessel,
                    context.Binding.RunnerId, context.Binding.RepositoryGrantId, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
                if (!String.Equals(target.RepositoryFingerprint, context.Binding.RepositoryFingerprint, StringComparison.Ordinal)
                    || String.IsNullOrWhiteSpace(target.HeadCommit))
                    throw new InvalidOperationException("harbor_merge_repository_identity_changed");
                string attemptId = HarborDeleteAttemptId(entry.Id);
                integration = await checkouts.ProvisionAuxiliaryAsync(context.Vessel, "armada/mq-delete/" + attemptId.Substring("mqdelete_".Length, 16),
                    context.Binding.RunnerId, context.Binding.RepositoryGrantId, target.HeadCommit!, true, attemptId,
                    context.SourceDock.CaptainId, token).ConfigureAwait(false);
            }
            if (!String.Equals(integration.VesselId, entry.VesselId, StringComparison.Ordinal)
                || !String.Equals(integration.TenantId, entry.TenantId, StringComparison.Ordinal)
                || !String.Equals(integration.UserId, entry.UserId, StringComparison.Ordinal)
                || !SameHarborOwner(context.Binding, integration.HostCheckout))
                throw new InvalidOperationException("harbor_integration_checkout_owner_mismatch");

            string checkout = HarborCheckoutAddress.ForDock(integration.Id);
            string repository = checkout + "/repository";
            string? repositoryHead = await _Git.GetHeadCommitHashAsync(repository, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(repositoryHead)) throw new InvalidOperationException("harbor_integration_repository_head_unavailable");
            try
            {
                using (new HarborCheckoutOperationScope("mq-delete-remote:" + entry.Id + ":" + repositoryHead, repositoryHead!))
                    await _Git.DeleteRemoteBranchAsync(repository, entry.BranchName!, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
            {
                if (!GitRemoteRefRule.IsRemoteRefAbsent(exception.Message))
                    _Logging.Warn(_Header + "remote branch delete for " + entry.BranchName + " failed: " + exception.Message);
            }
            try
            {
                using (new HarborCheckoutOperationScope("mq-delete-local:" + entry.Id + ":" + repositoryHead, repositoryHead!))
                    await _Git.DeleteLocalBranchAsync(repository, entry.BranchName!, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
            {
                _Logging.Debug(_Header + "local branch delete for " + entry.BranchName + " skipped: " + exception.Message);
            }
            await RemoveHarborIntegrationAsync(entry, integration, token).ConfigureAwait(false);
        }

        private static string HarborDeleteAttemptId(string entryId)
        {
            byte[] identity = Encoding.UTF8.GetBytes(entryId);
            return "mqdelete_" + Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        }
    }
}
