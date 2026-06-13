namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Resolves the running server's self-vessel, computes commit-level drift against
    /// the landed default-branch HEAD, and returns a <see cref="BuildDriftReport"/>.
    /// </summary>
    public interface IBuildDriftService
    {
        /// <summary>
        /// Computes and returns a <see cref="BuildDriftReport"/> describing the drift
        /// between the running server build and the latest landed commit on the default branch.
        /// Never throws -- callers may treat any exception as a non-critical informational gap.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A <see cref="BuildDriftReport"/> describing the current drift state.</returns>
        Task<BuildDriftReport> GetReportAsync(CancellationToken token = default);
    }
}
