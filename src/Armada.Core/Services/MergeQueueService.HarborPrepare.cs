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
        private async Task PrepareHarborIntegrationAsync(MergeEntry entry, HarborQueueContext context, CancellationToken token)
        {
            HarborCheckoutService checkouts = _HarborCheckouts
                ?? throw new InvalidOperationException("harbor_checkout_provider_unavailable");
            HarborWorkspaceOperationResult target = await checkouts.InspectRepositoryAsync(context.Vessel,
                context.Binding.RunnerId, context.Binding.RepositoryGrantId, "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
            HarborWorkspaceOperationResult source = await checkouts.InspectRepositoryAsync(context.Vessel,
                context.Binding.RunnerId, context.Binding.RepositoryGrantId, "refs/heads/" + entry.BranchName, token).ConfigureAwait(false);
            if (!String.Equals(target.RepositoryFingerprint, context.Binding.RepositoryFingerprint, StringComparison.Ordinal)
                || !String.Equals(source.RepositoryFingerprint, context.Binding.RepositoryFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_merge_repository_identity_changed");
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            string attemptId = HarborQueueAttemptId(entry.Id, job.RetryCount);
            Dock integration = await checkouts.ProvisionAuxiliaryAsync(context.Vessel, GetIntegrationBranch(entry),
                context.Binding.RunnerId, context.Binding.RepositoryGrantId, target.HeadCommit!, true,
                attemptId, context.SourceDock.CaptainId, token).ConfigureAwait(false);
            if (!SameHarborOwner(context.Binding, integration.HostCheckout))
                throw new InvalidOperationException("harbor_integration_checkout_owner_mismatch");
            if (!String.Equals(integration.HostCheckout!.BaseCommit, target.HeadCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("target branch drift: queued Harbor integration checkout is pinned to a different base");

            string integrationRepository = HarborCheckoutAddress.ForDock(integration.Id) + "/repository";
            string? head = await _Git.GetHeadCommitHashAsync(integrationRepository, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_integration_repository_head_unavailable");
            using (new HarborCheckoutOperationScope("mq-fetch:" + attemptId + ":" + head, head!))
                await _Git.FetchAsync(integrationRepository, token).ConfigureAwait(false);
            string? fetchedTarget = await _Git.GetRevisionCommitShaAsync(integrationRepository,
                "refs/heads/" + entry.TargetBranch, token).ConfigureAwait(false);
            if (!String.Equals(fetchedTarget, target.HeadCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("target branch drift: queued Harbor target moved after checkout provisioning");
            string? fetchedSource = await _Git.GetRevisionCommitShaAsync(integrationRepository,
                "refs/heads/" + entry.BranchName, token).ConfigureAwait(false);
            if (!String.Equals(fetchedSource, source.HeadCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("harbor_merge_source_changed: source branch moved during queue preparation");
        }

        private async Task<Dock?> ReadHarborIntegrationAsync(MergeEntry entry, CancellationToken token)
        {
            LandingJob job = await EnsureLandingJobAsync(entry, token).ConfigureAwait(false);
            string attemptId = HarborQueueAttemptId(entry.Id, job.RetryCount);
            Dock? integration = await _Database.Docks.ReadByProvisionAttemptAsync(attemptId, token).ConfigureAwait(false);
            if (integration == null) return null;

            HarborQueueContext? context = await ReadHarborQueueContextAsync(entry, token).ConfigureAwait(false);
            HarborCheckoutBinding? binding = integration.HostCheckout;
            string expectedBranch = GetIntegrationBranch(entry);
            if (context == null || !String.Equals(integration.ProvisionAttemptId, attemptId, StringComparison.Ordinal)
                || !String.Equals(integration.VesselId, entry.VesselId, StringComparison.Ordinal)
                || !String.Equals(integration.TenantId, entry.TenantId, StringComparison.Ordinal)
                || !String.Equals(integration.UserId, entry.UserId, StringComparison.Ordinal)
                || !String.Equals(integration.CaptainId, context.SourceDock.CaptainId, StringComparison.Ordinal)
                || !String.Equals(integration.BranchName, expectedBranch, StringComparison.Ordinal)
                || binding == null || !SameHarborOwner(context.Binding, binding)
                || !String.Equals(binding.BranchName, expectedBranch, StringComparison.Ordinal)
                || String.IsNullOrWhiteSpace(binding.BaseCommit))
                throw new InvalidOperationException("harbor_integration_checkout_binding_invalid");
            return integration;
        }
    }
}
