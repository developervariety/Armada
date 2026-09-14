namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Provides the backup, restore verification, and candidate validation gate for self-deploy.
    /// </summary>
    public interface ISelfDeployPreflight
    {
        /// <summary>
        /// Run all safety checks required before a self-deploy cutover.
        /// </summary>
        /// <param name="request">Candidate and deployment inputs.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Proof of each required check.</returns>
        Task<SelfDeployPreflightResult> ValidateAsync(
            SelfDeployPreflightRequest request,
            CancellationToken token = default);
    }
}
