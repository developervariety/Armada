namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class LandingService
    {
        private async Task<HarborLandingAttempt> PrepareHarborLandingAttemptAsync(Vessel vessel, Mission mission, Dock dock,
            HarborCheckoutBinding binding, string repository, string targetBranch, string branch, string? commitMessage,
            Action<Dock> integrationCreated, CancellationToken token)
        {
            string? targetTip = await _Git.GetRevisionCommitShaAsync(repository, "refs/heads/" + targetBranch, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(targetTip)) throw new InvalidOperationException("landing_target_unavailable: " + targetBranch);
            string? sourceTip = await _Git.GetRevisionCommitShaAsync(repository, "refs/heads/" + branch, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(sourceTip)) throw new InvalidOperationException("landing_source_branch_unavailable: " + branch);
            if (!String.IsNullOrWhiteSpace(mission.CommitHash)
                && !String.Equals(sourceTip, mission.CommitHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("landing_source_commit_changed: expected " + mission.CommitHash + " but found " + sourceTip);

            string attemptId = HarborLandingAttemptId(mission.Id, mission.LandingRetryCount, targetTip!);
            Dock integration = await (_HarborCheckouts
                ?? throw new InvalidOperationException("harbor_checkout_provider_unavailable"))
                .ProvisionAuxiliaryAsync(vessel, HarborIntegrationBranch(attemptId), binding.RunnerId,
                    binding.RepositoryGrantId, targetTip!, true, attemptId, dock.CaptainId, token).ConfigureAwait(false);
            integrationCreated(integration);
            if (!SameHarborOwner(binding, integration.HostCheckout))
                throw new InvalidOperationException("harbor_integration_checkout_owner_mismatch");

            string integrationPath = HarborCheckoutAddress.ForDock(integration.Id);
            string integrationRepository = integrationPath + "/repository";
            await FetchHarborRepositoryAsync(integrationRepository, attemptId, token).ConfigureAwait(false);
            string? fetchedTargetTip = await _Git.GetRevisionCommitShaAsync(integrationRepository,
                "refs/heads/" + targetBranch, token).ConfigureAwait(false);
            if (!String.Equals(fetchedTargetTip, targetTip, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("target branch drift: " + targetBranch + " moved from " + targetTip
                    + " to " + (fetchedTargetTip ?? "(unresolved)"));
            string? integrationHead = await _Git.GetHeadCommitHashAsync(integrationPath, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(integrationHead))
                throw new InvalidOperationException("harbor_integration_checkout_head_unavailable");
            bool alreadyMerged = String.Equals(integrationHead, sourceTip, StringComparison.OrdinalIgnoreCase)
                || await _Git.TryIsAncestorAsync(integrationPath, sourceTip!, "HEAD", token).ConfigureAwait(false) == true;
            if (!alreadyMerged)
            {
                using (new HarborCheckoutOperationScope("landing-merge:" + attemptId, integrationHead!))
                    await _Git.MergeBranchLocalAsync(integrationPath, integrationPath + "/repository", branch,
                        null, commitMessage, token).ConfigureAwait(false);
            }

            string? mergedHead = await _Git.GetHeadCommitHashAsync(integrationPath, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(mergedHead)) throw new InvalidOperationException("harbor_integration_commit_unavailable");
            return new HarborLandingAttempt(integration, targetTip!, mergedHead!, attemptId);
        }

        private static string HarborLandingAttemptId(string missionId, int retryCount, string targetCommit)
        {
            string value = missionId + "\n" + retryCount + "\n" + targetCommit.ToLowerInvariant();
            return "landing_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }

        private static string HarborIntegrationBranch(string attemptId)
        {
            return "armada-integration/" + attemptId.Substring("landing_".Length, 16);
        }

        private static bool SameHarborOwner(HarborCheckoutBinding primary, HarborCheckoutBinding? auxiliary)
        {
            return auxiliary != null && auxiliary.IsComplete
                && String.Equals(primary.RunnerId, auxiliary.RunnerId, StringComparison.Ordinal)
                && primary.EnrollmentGeneration == auxiliary.EnrollmentGeneration
                && String.Equals(primary.TenantId, auxiliary.TenantId, StringComparison.Ordinal)
                && String.Equals(primary.UserId, auxiliary.UserId, StringComparison.Ordinal)
                && String.Equals(primary.RepositoryGrantId, auxiliary.RepositoryGrantId, StringComparison.Ordinal)
                && String.Equals(primary.RepositoryFingerprint, auxiliary.RepositoryFingerprint, StringComparison.Ordinal);
        }

        private sealed record HarborLandingAttempt(Dock IntegrationDock, string TargetTip, string MergedHead, string AttemptId);
    }
}
