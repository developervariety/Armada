namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule every full vessel update applies: the request replaces client-editable fields only. Ownership,
    /// creation time and server-maintained counters come from the stored record, because the providers write every
    /// column on update. The token override is write-only, so an update that omits it keeps the stored credential and
    /// only an explicit value (empty clears) replaces it.
    /// </summary>
    public static class VesselUpdateMerge
    {
        #region Public-Methods

        /// <summary>
        /// Copy the server-owned fields from the stored vessel onto the requested update.
        /// </summary>
        /// <param name="existing">Stored vessel.</param>
        /// <param name="updated">Requested update; changed in place.</param>
        public static void KeepServerOwnedFields(Vessel existing, Vessel updated)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (updated == null) throw new ArgumentNullException(nameof(updated));

            updated.Id = existing.Id;
            updated.TenantId = existing.TenantId;
            updated.UserId = existing.UserId;
            updated.CreatedUtc = existing.CreatedUtc;
            updated.AutoLandCalibrationLandedCount = existing.AutoLandCalibrationLandedCount;
            updated.GitHubTokenOverride = Vessel.ResolveGitHubTokenOverride(
                existing.GitHubTokenOverride, updated.GitHubTokenOverrideSpecified, updated.GitHubTokenOverride);
        }

        #endregion
    }
}
