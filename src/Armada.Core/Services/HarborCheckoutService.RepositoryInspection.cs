namespace Armada.Core.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    public sealed partial class HarborCheckoutService
    {
        /// <summary>Inspect a configured repository revision under the enrolled runner's exact owner.</summary>
        /// <param name="vessel">Repository owner and repository URL.</param>
        /// <param name="runnerId">Runner enrollment that owns the configured grant.</param>
        /// <param name="repositoryGrantId">Configured repository grant.</param>
        /// <param name="requestedRevision">Ref or full commit to resolve.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified repository head and fingerprint.</returns>
        public async Task<HarborWorkspaceOperationResult> InspectRepositoryAsync(Vessel vessel, string runnerId,
            string repositoryGrantId, string requestedRevision, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (!_Settings.Harbor.Enabled) throw new InvalidOperationException("harbor_checkout_disabled");
            if (String.IsNullOrWhiteSpace(vessel.TenantId) || String.IsNullOrWhiteSpace(vessel.UserId)
                || String.IsNullOrWhiteSpace(vessel.RepoUrl) || String.IsNullOrWhiteSpace(repositoryGrantId)
                || String.IsNullOrWhiteSpace(runnerId) || String.IsNullOrWhiteSpace(requestedRevision))
                throw new InvalidOperationException("harbor_checkout_repository_identity_missing");
            HarborRunnerEnrollment enrollment = await ReadActiveEnrollmentAsync(vessel, runnerId, token).ConfigureAwait(false);
            return await InspectRepositoryAsync(vessel, enrollment, repositoryGrantId, requestedRevision, token).ConfigureAwait(false);
        }

        private async Task<HarborWorkspaceOperationResult> InspectRepositoryAsync(Vessel vessel,
            HarborRunnerEnrollment enrollment, string repositoryGrantId, string requestedRevision, CancellationToken token)
        {
            IHarborWorkspaceOperations channel = _Channel ?? throw new InvalidOperationException("harbor_workspace_unavailable");
            UserMaster? ownerUser = await _Database.Users.ReadAsync(vessel.TenantId!, vessel.UserId!, token).ConfigureAwait(false);
            if (ownerUser == null || !ownerUser.Active) throw new InvalidOperationException("harbor_checkout_owner_unavailable");
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vessel.RepoUrl!.Trim()))).ToLowerInvariant();
            HarborWorkspaceOperationRequest inspect = new HarborWorkspaceOperationRequest
            {
                Operation = HarborWorkspaceOperationEnum.InspectRepository,
                VesselId = vessel.Id,
                RunnerId = enrollment.RunnerId,
                RepositoryGrantId = repositoryGrantId,
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                EnrollmentGeneration = enrollment.Generation,
                ExpectedRepositoryFingerprint = fingerprint,
                RequestedRevision = requestedRevision
            };
            AuthContext owner = AuthContext.Authenticated(vessel.TenantId, vessel.UserId, false, false, "HarborCheckout");
            HarborWorkspaceOperationResult observed = await channel.ExecuteAsync(owner, inspect, token).ConfigureAwait(false);
            ValidateRepositoryObservation(observed, fingerprint);
            return observed;
        }

        private async Task<HarborRunnerEnrollment> ReadActiveEnrollmentAsync(Vessel vessel, string runnerId, CancellationToken token)
        {
            HarborRunnerEnrollment? enrollment = await _Database.HarborRunnerEnrollments.ReadAsync(runnerId, token).ConfigureAwait(false);
            if (enrollment == null || !enrollment.Active
                || !String.Equals(enrollment.TenantId, vessel.TenantId, StringComparison.Ordinal)
                || !String.Equals(enrollment.UserId, vessel.UserId, StringComparison.Ordinal))
                throw new InvalidOperationException("harbor_checkout_owner_mismatch");
            return enrollment;
        }

        private static void ValidateRepositoryObservation(HarborWorkspaceOperationResult result, string fingerprint)
        {
            if (!result.Accepted || result.Truncated)
                throw new InvalidOperationException(result.Reason ?? "harbor_repository_inspection_failed");
            if (!String.Equals(result.RepositoryFingerprint, fingerprint, StringComparison.Ordinal)
                || !Regex.IsMatch(result.HeadCommit ?? String.Empty, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z"))
                throw new InvalidOperationException("harbor_repository_identity_unverified");
        }
    }
}
