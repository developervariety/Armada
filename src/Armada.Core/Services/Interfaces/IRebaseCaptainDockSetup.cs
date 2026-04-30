namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Builds the <see cref="RebaseCaptainMissionSpec"/> for a rebase-captain mission:
    /// the brief (original brief plus a conflict-context appendix) and the prestaged-files
    /// list that puts the captain branch in conflict state inside the new mission's dock.
    /// </summary>
    public interface IRebaseCaptainDockSetup
    {
        /// <summary>
        /// Build the rebase-captain mission spec for the supplied failed merge entry.
        /// </summary>
        /// <param name="mergeEntryId">Identifier of the failed merge-queue entry.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The brief and prestaged file list for the new rebase-captain mission.</returns>
        Task<RebaseCaptainMissionSpec> BuildAsync(string mergeEntryId, CancellationToken token = default);
    }

    /// <summary>
    /// Description of a rebase-captain mission to be created by the recovery handler.
    /// </summary>
    /// <param name="Brief">Mission description (original brief plus conflict appendix).</param>
    /// <param name="PrestagedFiles">Files to be staged into the new mission's dock,
    /// including the captain branch checked out at conflict state.</param>
    public sealed record RebaseCaptainMissionSpec(
        string Brief,
        IReadOnlyList<PrestagedFile> PrestagedFiles);
}
