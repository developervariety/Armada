namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    public sealed partial class HarborCheckoutService
    {
        /// <summary>Reclaims one exact completed CheckRun auxiliary dock through the durable dock lifecycle.</summary>
        /// <param name="expected">The dock returned for the CheckRun attempt.</param>
        /// <param name="provisionAttemptId">The stable attempt identity used to create the auxiliary dock.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Completes only after the dock is durably inactive.</returns>
        public async Task ReleaseAuxiliaryAsync(Dock expected, string provisionAttemptId, CancellationToken token = default)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (String.IsNullOrWhiteSpace(provisionAttemptId)) throw new ArgumentNullException(nameof(provisionAttemptId));
            Dock? current = await _Database.Docks.ReadAsync(expected.TenantId, expected.Id, token).ConfigureAwait(false);
            if (current == null || current.ProvisionAttemptId != provisionAttemptId
                || current.TenantId != expected.TenantId || current.UserId != expected.UserId
                || current.VesselId != expected.VesselId || !SameBinding(current.HostCheckout, expected.HostCheckout))
                throw new InvalidOperationException("harbor_auxiliary_cleanup_identity_mismatch");
            if (!current.Active) return;
            IDockService lifecycle = _DockLifecycle ?? throw new InvalidOperationException("harbor_dock_lifecycle_unavailable");
            await lifecycle.ReclaimAsync(current.Id, current.TenantId, token).ConfigureAwait(false);
            Dock? released = await _Database.Docks.ReadAsync(current.TenantId, current.Id, token).ConfigureAwait(false);
            if (released == null || released.ProvisionAttemptId != provisionAttemptId || released.Active
                || !SameBinding(released.HostCheckout, expected.HostCheckout))
                throw new InvalidOperationException("harbor_auxiliary_cleanup_unconfirmed");
        }

        private static bool SameBinding(HarborCheckoutBinding? left, HarborCheckoutBinding? right)
        {
            return left != null && right != null && left.RunnerId == right.RunnerId
                && left.EnrollmentGeneration == right.EnrollmentGeneration && left.TenantId == right.TenantId
                && left.UserId == right.UserId && left.RepositoryGrantId == right.RepositoryGrantId
                && left.RepositoryFingerprint == right.RepositoryFingerprint && left.CheckoutId == right.CheckoutId
                && left.BaseCommit == right.BaseCommit && left.BranchName == right.BranchName && left.Revision == right.Revision;
        }
    }
}
