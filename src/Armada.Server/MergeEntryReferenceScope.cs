namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The vessel and mission a merge entry names must be visible to the caller that enqueues it. Enqueue
    /// attaches the named mission's Judge follow-ups and audit verdict to the entry and landing merges into
    /// the named vessel, so every enqueue surface asks this one rule before the entry is stored.
    /// </summary>
    public static class MergeEntryReferenceScope
    {
        #region Public-Methods

        /// <summary>
        /// Name the first record a new merge entry refers to that the caller may not see.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="entry">Merge entry about to be enqueued.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A not-found message naming the record, or null when every reference is visible.</returns>
        public static async Task<string?> FindUnreachableAsync(DatabaseDriver database, AuthContext caller, MergeEntry entry, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (!String.IsNullOrWhiteSpace(entry.VesselId)
                && await CallerScopedRead.ReadVesselAsync(database, caller, entry.VesselId, token).ConfigureAwait(false) == null)
                return "Vessel not found: " + entry.VesselId;
            if (!String.IsNullOrWhiteSpace(entry.MissionId)
                && await CallerScopedRead.ReadMissionAsync(database, caller, entry.MissionId, token).ConfigureAwait(false) == null)
                return "Mission not found: " + entry.MissionId;
            return null;
        }

        #endregion
    }
}
