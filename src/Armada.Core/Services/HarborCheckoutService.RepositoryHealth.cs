namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    public sealed partial class HarborCheckoutService
    {
        /// <summary>
        /// Read repository health on the enrolled runner that owns the vessel. A missing runner,
        /// a disabled Harbor link, or a refused result is unavailable. This method does not read
        /// an Admiral-local checkout.
        /// </summary>
        /// <param name="vessel">Repository owner and repository URL.</param>
        /// <param name="route">Harbor-owned route for the vessel.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Facts from the owner, or unavailable.</returns>
        public async Task<RepositoryHealthOwnerReply> ReadRepositoryHealthAsync(Vessel vessel, HarborMissionRoute route, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (route == null) throw new ArgumentNullException(nameof(route));
            if (!_Settings.Harbor.Enabled || _Channel == null || String.IsNullOrWhiteSpace(route.RepositoryGrantId) || String.IsNullOrWhiteSpace(route.RunnerId))
                return Unavailable();
            if (String.IsNullOrWhiteSpace(vessel.TenantId) || String.IsNullOrWhiteSpace(vessel.UserId) || String.IsNullOrWhiteSpace(vessel.RepoUrl))
                return Unavailable();

            HarborRunnerEnrollment enrollment;
            try
            {
                enrollment = await ReadActiveEnrollmentAsync(vessel, route.RunnerId, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return Unavailable();
            }

            UserMaster? ownerUser = await _Database.Users.ReadAsync(vessel.TenantId!, vessel.UserId!, token).ConfigureAwait(false);
            if (ownerUser == null || !ownerUser.Active) return Unavailable();

            string branch = String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch.Trim();
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vessel.RepoUrl!.Trim()))).ToLowerInvariant();
            HarborWorkspaceOperationRequest request = new HarborWorkspaceOperationRequest
            {
                Operation = HarborWorkspaceOperationEnum.ReadRepositoryHealth,
                VesselId = vessel.Id,
                RunnerId = enrollment.RunnerId,
                RepositoryGrantId = route.RepositoryGrantId,
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                EnrollmentGeneration = enrollment.Generation,
                ExpectedRepositoryFingerprint = fingerprint,
                RequestedRevision = branch
            };
            AuthContext owner = AuthContext.Authenticated(vessel.TenantId, vessel.UserId, false, false, "HarborCheckout");
            IHarborWorkspaceOperations channel = _Channel;
            HarborWorkspaceOperationResult result = await channel.ExecuteAsync(owner, request, token).ConfigureAwait(false);
            if (!result.Accepted)
                return Unavailable(result.Reason);
            RepositoryHealthGitFacts? facts = RepositoryHealthGitFacts.Parse(result.Text);
            if (facts == null) return Unavailable();
            return new RepositoryHealthOwnerReply { Facts = facts };
        }

        private static RepositoryHealthOwnerReply Unavailable(string? reason = null)
        {
            return new RepositoryHealthOwnerReply
            {
                Unavailable = true,
                Reason = String.IsNullOrWhiteSpace(reason) ? RepositoryHealthService.OwnerHostUnavailable : reason
            };
        }
    }
}
