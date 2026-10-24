namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public sealed partial class HarborCheckoutService
    {
        private static readonly KeyedAsyncLock _ProvisionAttemptLocks = new KeyedAsyncLock(StringComparer.Ordinal);

        /// <summary>Provision or resume the unique dock intent for a durable mission attempt.</summary>
        /// <param name="vessel">Repository owner and repository URL.</param>
        /// <param name="captain">Captain assigned to the mission.</param>
        /// <param name="branchName">Branch name to attach, when not detached.</param>
        /// <param name="runnerId">Runner enrollment that owns the configured grant.</param>
        /// <param name="repositoryGrantId">Configured repository grant.</param>
        /// <param name="requestedRevision">Ref or full commit fixed for this attempt.</param>
        /// <param name="detached">Whether to provision a detached checkout.</param>
        /// <param name="provisionAttemptId">Stable durable ID retained across retries.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The persisted dock intent and completed checkout.</returns>
        public Task<Dock> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string runnerId,
            string repositoryGrantId, string requestedRevision, bool detached, string provisionAttemptId,
            CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            return ProvisionCoreAsync(vessel, captain.Id, branchName, runnerId, repositoryGrantId,
                requestedRevision, detached, provisionAttemptId, token);
        }

        /// <summary>Provision or resume a standalone auxiliary checkout without a mission captain.</summary>
        /// <param name="vessel">Repository owner and repository URL.</param>
        /// <param name="branchName">Branch name to attach, when not detached.</param>
        /// <param name="runnerId">Runner enrollment that owns the configured grant.</param>
        /// <param name="repositoryGrantId">Configured repository grant.</param>
        /// <param name="requestedRevision">Ref or full commit fixed for this attempt.</param>
        /// <param name="detached">Whether to provision a detached checkout.</param>
        /// <param name="provisionAttemptId">Stable durable ID retained across retries.</param>
        /// <param name="captainId">Optional captain for a mission-associated auxiliary checkout.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The persisted dock intent and completed checkout.</returns>
        public Task<Dock> ProvisionAuxiliaryAsync(Vessel vessel, string branchName, string runnerId,
            string repositoryGrantId, string requestedRevision, bool detached, string provisionAttemptId,
            string? captainId = null, CancellationToken token = default)
        {
            return ProvisionCoreAsync(vessel, captainId, branchName, runnerId, repositoryGrantId,
                requestedRevision, detached, provisionAttemptId, token);
        }

        private async Task<Dock> ProvisionCoreAsync(Vessel vessel, string? captainId, string branchName,
            string runnerId, string repositoryGrantId, string requestedRevision, bool detached,
            string provisionAttemptId, CancellationToken token)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrWhiteSpace(provisionAttemptId) || provisionAttemptId.Length > 191)
                throw new InvalidOperationException("harbor_provision_attempt_id_invalid");
            if (String.IsNullOrWhiteSpace(branchName)) throw new ArgumentException("A branch name is required.", nameof(branchName));

            using (IDisposable attemptLock = await _ProvisionAttemptLocks.AcquireAsync(provisionAttemptId, token).ConfigureAwait(false))
            {
                Dock? existing = await _Database.Docks.ReadByProvisionAttemptAsync(provisionAttemptId, token).ConfigureAwait(false);
                if (existing != null)
                {
                    ValidateExistingAttempt(existing, vessel, captainId, branchName, runnerId, repositoryGrantId, provisionAttemptId);
                    return await CompleteProvisionAsync(existing, detached, token).ConfigureAwait(false);
                }

                HarborRunnerEnrollment enrollment = await ReadActiveEnrollmentAsync(vessel, runnerId, token).ConfigureAwait(false);
                HarborWorkspaceOperationResult observed = await InspectRepositoryAsync(vessel, enrollment, repositoryGrantId,
                    requestedRevision, token).ConfigureAwait(false);
                HarborCheckoutBinding binding = new HarborCheckoutBinding
                {
                    RunnerId = runnerId,
                    EnrollmentGeneration = enrollment.Generation,
                    TenantId = vessel.TenantId!,
                    UserId = vessel.UserId!,
                    RepositoryGrantId = repositoryGrantId,
                    RepositoryFingerprint = observed.RepositoryFingerprint!,
                    CheckoutId = "hco_" + Guid.NewGuid().ToString("N"),
                    Revision = Guid.NewGuid().ToString("N"),
                    BaseCommit = observed.HeadCommit!,
                    BranchName = branchName
                };
                Dock dock = new Dock(vessel.Id)
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    CaptainId = captainId,
                    BranchName = branchName,
                    ProvisionAttemptId = provisionAttemptId,
                    HostCheckout = binding
                };
                dock.WorktreePath = HarborCheckoutAddress.ForDock(dock.Id);

                try
                {
                    await _Database.Docks.CreateAsync(dock, token).ConfigureAwait(false);
                }
                catch (Exception createException) when (!(createException is OperationCanceledException && token.IsCancellationRequested))
                {
                    Dock? committed = await _Database.Docks.ReadByProvisionAttemptAsync(provisionAttemptId, CancellationToken.None).ConfigureAwait(false);
                    if (committed == null) throw;
                    ValidateExistingAttempt(committed, vessel, captainId, branchName, runnerId, repositoryGrantId, provisionAttemptId);
                    return await CompleteProvisionAsync(committed, detached, token).ConfigureAwait(false);
                }
                return await CompleteProvisionAsync(dock, detached, token).ConfigureAwait(false);
            }
        }

    }
}
