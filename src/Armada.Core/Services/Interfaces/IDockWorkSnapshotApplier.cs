namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Applies a mission's saved dock work -- the newest <c>recover/{missionId}-{n}</c> branch written by
    /// <see cref="IGitService.SnapshotDockWorkAsync"/> -- to a freshly provisioned checkout. A git service
    /// that cannot do so does not implement this interface.
    /// </summary>
    public interface IDockWorkSnapshotApplier
    {
        /// <summary>
        /// Applies the newest saved work of a mission to a checkout as uncommitted changes and creates no commit.
        /// </summary>
        /// <remarks>
        /// Nothing is applied unless the checkout is clean and its HEAD is the parent of the saved work, so the
        /// changes land on exactly the commit they were made on. A conflict leaves the checkout as it was.
        /// </remarks>
        /// <param name="worktreePath">Checkout to apply the work to.</param>
        /// <param name="missionId">Mission whose saved work is applied.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What was applied, or why nothing was.</returns>
        Task<DockWorkRestore> ApplyDockWorkAsync(string worktreePath, string missionId, CancellationToken token = default);
    }
}
