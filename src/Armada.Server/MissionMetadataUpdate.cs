namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule every full mission update applies: only metadata fields are merged onto the stored mission.
    /// Status, ownership, captain, dock, process, commit and timestamps stay as stored, so an update can never move a
    /// mission past the status transition gates or into another tenant. The vessel and voyage bindings cannot change.
    /// </summary>
    public static class MissionMetadataUpdate
    {
        #region Public-Members

        /// <summary>
        /// Message returned when an update names a different vessel or voyage.
        /// </summary>
        public const string BindingChangeRefusedMessage = "Mission vesselId and voyageId cannot be changed by the metadata update route.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Merge the metadata fields of an update onto the stored mission.
        /// </summary>
        /// <param name="existing">Stored mission; changed in place only when the update is accepted.</param>
        /// <param name="incoming">Requested update.</param>
        /// <param name="bindings">The vessel and voyage members the request carried.</param>
        /// <returns>Null when accepted; otherwise the refusal message, and the stored mission is unchanged.</returns>
        public static string? Apply(Mission existing, Mission incoming, MissionBindingUpdateRequest bindings)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (incoming == null) throw new ArgumentNullException(nameof(incoming));
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));

            if ((bindings.HasVesselId && !String.Equals(existing.VesselId, bindings.VesselId, StringComparison.OrdinalIgnoreCase))
                || (bindings.HasVoyageId && !String.Equals(existing.VoyageId, bindings.VoyageId, StringComparison.OrdinalIgnoreCase)))
            {
                return BindingChangeRefusedMessage;
            }

            existing.Title = incoming.Title;
            existing.Description = incoming.Description;
            existing.Priority = incoming.Priority;
            existing.BranchName = incoming.BranchName;
            existing.PrUrl = incoming.PrUrl;
            existing.ParentMissionId = incoming.ParentMissionId;
            existing.DependsOnMissionId = incoming.DependsOnMissionId;
            existing.LastUpdateUtc = DateTime.UtcNow;
            return null;
        }

        #endregion
    }
}
