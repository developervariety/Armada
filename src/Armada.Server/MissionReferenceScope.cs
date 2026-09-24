namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The records a mission names by id -- its vessel, voyage, captains, dependency and parent -- must be
    /// visible to the caller that creates or changes the mission. Every mission create and update surface
    /// asks this one rule before it stores the mission, so a caller cannot dispatch into, depend on, or run
    /// on another tenant's records by naming their ids.
    /// </summary>
    public static class MissionReferenceScope
    {
        #region Public-Methods

        /// <summary>
        /// Name the first record a new mission refers to that the caller may not see.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="mission">Mission about to be created.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A not-found message naming the record, or null when every reference is visible.</returns>
        public static async Task<string?> FindUnreachableOnCreateAsync(DatabaseDriver database, AuthContext caller, Mission mission, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (!String.IsNullOrWhiteSpace(mission.VesselId)
                && await CallerScopedRead.ReadVesselAsync(database, caller, mission.VesselId, token).ConfigureAwait(false) == null)
                return "Vessel not found: " + mission.VesselId;
            if (!String.IsNullOrWhiteSpace(mission.VoyageId)
                && await CallerScopedRead.ReadVoyageAsync(database, caller, mission.VoyageId, token).ConfigureAwait(false) == null)
                return "Voyage not found: " + mission.VoyageId;
            if (!String.IsNullOrWhiteSpace(mission.CaptainId)
                && await CallerScopedRead.ReadCaptainAsync(database, caller, mission.CaptainId, token).ConfigureAwait(false) == null)
                return "Captain not found: " + mission.CaptainId;
            if (!String.IsNullOrWhiteSpace(mission.RequestedCaptainId)
                && await CallerScopedRead.ReadCaptainAsync(database, caller, mission.RequestedCaptainId, token).ConfigureAwait(false) == null)
                return "Captain not found: " + mission.RequestedCaptainId;
            return await FindUnreachableMissionLinksAsync(database, caller, mission.DependsOnMissionId, mission.ParentMissionId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Name the first mission an update links to that the caller may not see. Only a link the update
        /// changes is checked, so an update that leaves a stored link alone is never refused for it.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="existing">Stored mission.</param>
        /// <param name="incoming">Requested update.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A not-found message naming the mission, or null when every changed link is visible.</returns>
        public static async Task<string?> FindUnreachableOnUpdateAsync(DatabaseDriver database, AuthContext caller, Mission existing, Mission incoming, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (incoming == null) throw new ArgumentNullException(nameof(incoming));

            string? dependsOn = String.Equals(existing.DependsOnMissionId, incoming.DependsOnMissionId, StringComparison.Ordinal) ? null : incoming.DependsOnMissionId;
            string? parent = String.Equals(existing.ParentMissionId, incoming.ParentMissionId, StringComparison.Ordinal) ? null : incoming.ParentMissionId;
            return await FindUnreachableMissionLinksAsync(database, caller, dependsOn, parent, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static async Task<string?> FindUnreachableMissionLinksAsync(DatabaseDriver database, AuthContext caller, string? dependsOnMissionId, string? parentMissionId, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(dependsOnMissionId)
                && await CallerScopedRead.ReadMissionAsync(database, caller, dependsOnMissionId, token).ConfigureAwait(false) == null)
                return "dependsOnMissionId not found: " + dependsOnMissionId;
            if (!String.IsNullOrWhiteSpace(parentMissionId)
                && await CallerScopedRead.ReadMissionAsync(database, caller, parentMissionId, token).ConfigureAwait(false) == null)
                return "parentMissionId not found: " + parentMissionId;
            return null;
        }

        #endregion
    }
}
