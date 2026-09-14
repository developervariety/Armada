namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Durable operations for Harbor runner enrollments.</summary>
    public interface IHarborRunnerEnrollmentMethods
    {
        /// <summary>Read the current enrollment for a runner.</summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The enrollment, or null when the runner is unknown.</returns>
        Task<HarborRunnerEnrollment?> ReadAsync(string runnerId, CancellationToken token = default);

        /// <summary>
        /// Create or re-enroll a runner when the expected generation is still current. The operation is
        /// atomic so concurrent administrators cannot substitute an active owner.
        /// </summary>
        /// <param name="enrollment">New enrollment state.</param>
        /// <param name="expectedGeneration">Expected current generation, or zero for a new runner.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when this operation changed the durable row.</returns>
        Task<bool> TryEnrollAsync(HarborRunnerEnrollment enrollment, long expectedGeneration, CancellationToken token = default);

        /// <summary>Revoke an active enrollment using a compare-and-set generation check.</summary>
        /// <param name="runnerId">Runner identifier.</param>
        /// <param name="expectedGeneration">Expected current generation.</param>
        /// <param name="revokedByUserId">Administrator user identifier.</param>
        /// <param name="revokedUtc">Revocation timestamp in UTC.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the active row was revoked.</returns>
        Task<bool> TryRevokeAsync(string runnerId, long expectedGeneration, string revokedByUserId, DateTime revokedUtc, CancellationToken token = default);
    }
}
