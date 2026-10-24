namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public sealed partial class HarborCheckoutService
    {
        /// <summary>Finish provisioning from a persisted intent without changing its owner or base.</summary>
        public async Task<Dock> CompleteProvisionAsync(Dock dock, bool detached, CancellationToken token = default)
        {
            HarborCheckoutBinding binding = RequireBinding(dock);
            HarborWorkspaceOperationRequest provision = CreateRequest(dock, HarborWorkspaceOperationEnum.ProvisionCheckout);
            provision.Detached = detached;
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope("provision:" + binding.Revision, binding.BaseCommit))
            {
                HarborWorkspaceOperationResult result = await ExecuteAsync(dock, provision, token).ConfigureAwait(false);
                if (!String.Equals(result.HeadCommit, binding.BaseCommit, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("harbor_checkout_base_mismatch");
                string expectedBranch = detached ? "(detached)" : binding.BranchName;
                if (!String.Equals(result.BranchName, expectedBranch, StringComparison.Ordinal))
                    throw new InvalidOperationException("harbor_checkout_provision_mode_mismatch");
            }
            return dock;
        }

        private static void ValidateExistingAttempt(Dock dock, Vessel vessel, string? captainId,
            string branchName, string runnerId, string repositoryGrantId, string provisionAttemptId)
        {
            HarborCheckoutBinding binding = RequireBinding(dock);
            if (!dock.Active || !String.Equals(dock.ProvisionAttemptId, provisionAttemptId, StringComparison.Ordinal)
                || !String.Equals(dock.VesselId, vessel.Id, StringComparison.Ordinal)
                || !String.Equals(dock.TenantId, vessel.TenantId, StringComparison.Ordinal)
                || !String.Equals(dock.UserId, vessel.UserId, StringComparison.Ordinal)
                || !String.Equals(dock.CaptainId, captainId, StringComparison.Ordinal)
                || !String.Equals(dock.BranchName, branchName, StringComparison.Ordinal)
                || !String.Equals(binding.RunnerId, runnerId, StringComparison.Ordinal)
                || !String.Equals(binding.RepositoryGrantId, repositoryGrantId, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_provision_attempt_identity_conflict");
        }
    }
}
