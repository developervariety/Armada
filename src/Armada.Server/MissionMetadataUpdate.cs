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

            string? bindingError = CheckBindings(existing, bindings.HasVesselId, bindings.VesselId, bindings.HasVoyageId, bindings.VoyageId);
            if (bindingError != null) return bindingError;

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

        /// <summary>
        /// Refuse an update that names a different vessel or voyage. Every mission update surface, full or partial,
        /// calls this before it changes anything.
        /// </summary>
        /// <param name="existing">Stored mission.</param>
        /// <param name="hasVesselId">True when the request carried a vessel id.</param>
        /// <param name="vesselId">Requested vessel id.</param>
        /// <param name="hasVoyageId">True when the request carried a voyage id.</param>
        /// <param name="voyageId">Requested voyage id.</param>
        /// <returns>Null when the bindings are unchanged; otherwise the refusal message.</returns>
        public static string? CheckBindings(Mission existing, bool hasVesselId, string? vesselId, bool hasVoyageId, string? voyageId)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if ((hasVesselId && !String.Equals(existing.VesselId, vesselId, StringComparison.OrdinalIgnoreCase))
                || (hasVoyageId && !String.Equals(existing.VoyageId, voyageId, StringComparison.OrdinalIgnoreCase)))
            {
                return BindingChangeRefusedMessage;
            }
            return null;
        }

        #endregion
    }
}
