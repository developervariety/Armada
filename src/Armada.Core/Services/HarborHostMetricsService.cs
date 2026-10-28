namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>
    /// Authorized read of one Harbor runner's in-memory host series. The caller must be the enrolled owner or have
    /// authority over that owner. Any other caller gets the same result as an unknown runner.
    /// </summary>
    public sealed class HarborHostMetricsService
    {
        private readonly HarborHostMetricsBuffer _Buffer;
        private readonly IHarborRunnerEnrollmentMethods _Enrollments;
        private readonly IHarborRunnerAuthority _Authority;

        /// <summary>Create the read service over a shared ring.</summary>
        /// <param name="buffer">Ring the Harbor link records into.</param>
        /// <param name="enrollments">Durable runner enrollment.</param>
        /// <param name="authority">Runner authority rule.</param>
        public HarborHostMetricsService(
            HarborHostMetricsBuffer buffer,
            IHarborRunnerEnrollmentMethods enrollments,
            IHarborRunnerAuthority authority)
        {
            _Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _Enrollments = enrollments ?? throw new ArgumentNullException(nameof(enrollments));
            _Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        }

        /// <summary>
        /// Read the series the caller may see. A hidden or unknown runner returns null. A visible runner with no
        /// samples returns <see cref="HarborHostMetricsSnapshot.StateNoData"/>.
        /// </summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The series, or null when the caller may not see the runner.</returns>
        public async Task<HarborHostMetricsSnapshot?> ReadAsync(AuthContext caller, string runnerId, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated || String.IsNullOrWhiteSpace(runnerId)) return null;
            string id = runnerId.Trim();
            if (id.Length > 450) return null;
            HarborRunnerEnrollment? enrollment = await _Enrollments.ReadAsync(id, token).ConfigureAwait(false);
            if (enrollment == null || !enrollment.Active) return null;
            bool allowed = await HarborRunnerAuthorization.IsOwnerOrHasAuthorityAsync(
                _Authority, caller, enrollment.TenantId, enrollment.UserId, token).ConfigureAwait(false);
            if (!allowed) return null;
            return _Buffer.Read(id);
        }
    }
}
