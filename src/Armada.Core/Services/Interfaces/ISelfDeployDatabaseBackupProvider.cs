namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Creates a provider-native backup and proves that it restores into an isolated target.
    /// </summary>
    public interface ISelfDeployDatabaseBackupProvider
    {
        /// <summary>Run the backup and isolated restore verification.</summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Backup and restore proof.</returns>
        Task<SelfDeployDatabaseBackupResult> CreateAndVerifyAsync(CancellationToken token = default);

        /// <summary>Remove the isolated verification target after candidate validation finishes.</summary>
        /// <param name="result">Proof returned by <see cref="CreateAndVerifyAsync"/>.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the isolated target was removed.</returns>
        Task<bool> CleanupAsync(SelfDeployDatabaseBackupResult result, CancellationToken token = default);

        /// <summary>Check that the result still belongs to this provider instance.</summary>
        /// <param name="result">Backup proof to check.</param>
        /// <returns>True only for an owned target and matching opaque token.</returns>
        bool OwnsTarget(SelfDeployDatabaseBackupResult result);
    }
}
