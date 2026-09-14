namespace Armada.Core.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for who may see or command Harbor work: the runner owner, or a caller with authority over that
    /// owner under the same rule that governs enrollment and revocation. Launch, stop, the job REST routes and the
    /// job MCP tools all call it.
    /// </summary>
    public static class HarborRunnerAuthorization
    {
        #region Public-Methods

        /// <summary>Whether the caller is the owner or has authority over the owner.</summary>
        /// <param name="authority">Shared runner authority rule.</param>
        /// <param name="caller">Verified caller.</param>
        /// <param name="ownerTenantId">Owner tenant.</param>
        /// <param name="ownerUserId">Owner user.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when authorized.</returns>
        public static async Task<bool> IsOwnerOrHasAuthorityAsync(
            IHarborRunnerAuthority authority,
            AuthContext caller,
            string ownerTenantId,
            string ownerUserId,
            CancellationToken token = default)
        {
            if (authority == null) throw new ArgumentNullException(nameof(authority));
            if (caller == null || !caller.IsAuthenticated) return false;
            if (String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId)) return false;
            if (String.IsNullOrWhiteSpace(ownerTenantId) || String.IsNullOrWhiteSpace(ownerUserId)) return false;
            if (IsOwner(caller, ownerTenantId, ownerUserId)) return true;
            return await authority.HasAuthorityOverOwnerAsync(caller, ownerTenantId, ownerUserId, token).ConfigureAwait(false);
        }

        /// <summary>Whether the caller is exactly the owner: same tenant and same user.</summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="ownerTenantId">Owner tenant.</param>
        /// <param name="ownerUserId">Owner user.</param>
        /// <returns>True for the owner.</returns>
        public static bool IsOwner(AuthContext caller, string ownerTenantId, string ownerUserId)
        {
            if (caller == null || !caller.IsAuthenticated) return false;
            return String.Equals(caller.TenantId, ownerTenantId, StringComparison.Ordinal)
                && String.Equals(caller.UserId, ownerUserId, StringComparison.Ordinal);
        }

        #endregion
    }
}
