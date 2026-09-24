namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule every mission update applies: only the metadata fields the request named are merged onto the
    /// stored mission.
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
        /// Merge the metadata fields a request named onto the stored mission. A field the request did not name keeps
        /// its stored value; an empty dependency or parent clears the link.
        /// </summary>
        /// <param name="existing">Stored mission; changed in place only when the update is accepted.</param>
        /// <param name="patch">The fields the request named.</param>
        /// <returns>Null when accepted; otherwise the refusal message, and the stored mission is unchanged.</returns>
        public static string? Apply(Mission existing, MissionMetadataPatch patch)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (patch == null) throw new ArgumentNullException(nameof(patch));

            string? bindingError = CheckBindings(existing, patch.HasVesselId, patch.VesselId, patch.HasVoyageId, patch.VoyageId);
            if (bindingError != null) return bindingError;

            if (patch.Has("title")) existing.Title = patch.Title ?? existing.Title;
            if (patch.Has("description")) existing.Description = patch.Description;
            if (patch.Has("priority") && patch.Priority.HasValue) existing.Priority = patch.Priority.Value;
            if (patch.Has("branchName")) existing.BranchName = patch.BranchName;
            if (patch.Has("prUrl")) existing.PrUrl = patch.PrUrl;
            if (patch.Has("parentMissionId")) existing.ParentMissionId = EmptyAsNull(patch.ParentMissionId);
            if (patch.Has("dependsOnMissionId")) existing.DependsOnMissionId = EmptyAsNull(patch.DependsOnMissionId);
            if (patch.Has("persona")) existing.Persona = EmptyAsNull(patch.Persona);
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

        #region Private-Methods

        private static string? EmptyAsNull(string? value)
        {
            return String.IsNullOrEmpty(value) ? null : value;
        }

        #endregion
    }
}
