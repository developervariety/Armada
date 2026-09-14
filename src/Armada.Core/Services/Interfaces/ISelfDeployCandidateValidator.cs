namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Validates a built server by running its database validation mode against an owned restored copy.
    /// </summary>
    public interface ISelfDeployCandidateValidator
    {
        /// <summary>Validate the candidate server.</summary>
        /// <param name="request">Self-deploy request containing the candidate assembly.</param>
        /// <param name="backup">Successful isolated restore proof.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Candidate validation result.</returns>
        Task<SelfDeployCandidateValidationResult> ValidateAsync(
            SelfDeployPreflightRequest request,
            SelfDeployDatabaseBackupResult backup,
            CancellationToken token = default);
    }
}
