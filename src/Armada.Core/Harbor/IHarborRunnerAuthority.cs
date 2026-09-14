namespace Armada.Core.Harbor
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// The single definition of administrative authority over a Harbor runner owner. Enrollment, reuse of a
    /// revoked runner, revocation, launch and stop all call this rule, so they cannot drift apart.
    /// </summary>
    public interface IHarborRunnerAuthority
    {
        /// <summary>
        /// Whether the caller has administrative authority over a runner owner. A global administrator has
        /// authority over every owner. A tenant administrator has authority only over an owner in the same tenant
        /// who is not a global administrator. Any other caller has none.
        /// </summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="ownerTenantId">Owner tenant identifier.</param>
        /// <param name="ownerUserId">Owner user identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the caller has authority over the owner.</returns>
        Task<bool> HasAuthorityOverOwnerAsync(AuthContext caller, string ownerTenantId, string ownerUserId, CancellationToken token = default);
    }
}
