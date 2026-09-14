namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// The single retention rule for the self-deploy release store: keep the caller's protected releases
    /// (the running and rollback releases), every release named by an unresolved restart record, and the
    /// newest previous releases up to the retention count. Pruning runs under the record lock and removes
    /// nothing when the record cannot be read.
    /// </summary>
    public static class SelfDeployReleaseRetention
    {
        /// <summary>
        /// Bound the release store.
        /// </summary>
        /// <param name="artifacts">Release store.</param>
        /// <param name="records">Restart record store.</param>
        /// <param name="protectedDigests">Running and rollback release digests.</param>
        /// <param name="retainPrevious">Newest previous releases to keep.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Prune outcome; never throws for storage or record failures.</returns>
        public static async Task<SelfDeployReleasePruneResult> PruneAsync(
            ISelfDeployArtifactStore artifacts,
            SelfDeployRestartRecordStore records,
            IReadOnlyCollection<string> protectedDigests,
            int retainPrevious,
            CancellationToken token = default)
        {
            if (artifacts == null) throw new ArgumentNullException(nameof(artifacts));
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (protectedDigests == null) throw new ArgumentNullException(nameof(protectedDigests));
            try
            {
                return await records.ReadUnderRecordLockAsync(async read =>
                {
                    if (read.Exists && !read.IsReadable)
                        return new SelfDeployReleasePruneResult { FailureReason = "release_prune_skipped_" + read.FailureReason };

                    List<string> keep = new List<string>(protectedDigests);
                    if (read.IsReadable && !SelfDeployRestartRecord.IsTerminal(read.Record!.State))
                    {
                        keep.Add(read.Record.Candidate.Digest);
                        keep.Add(read.Record.Rollback.Digest);
                    }
                    return await artifacts.PruneAsync(keep, retainPrevious, token).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            }
            catch (SelfDeployCutoverException ex)
            {
                return new SelfDeployReleasePruneResult { FailureReason = "release_prune_skipped_" + ex.FailureReason };
            }
        }
    }
}
