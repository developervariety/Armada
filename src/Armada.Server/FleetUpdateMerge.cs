namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule every full fleet update applies: the request replaces client-editable fields only. Ownership and
    /// creation time come from the stored record, because every provider writes every column on update, and a field
    /// with a model default keeps its stored value unless the request names it.
    /// </summary>
    public static class FleetUpdateMerge
    {
        #region Public-Methods

        /// <summary>
        /// Copy the server-owned and unnamed defaulted fields from the stored fleet onto the requested update.
        /// </summary>
        /// <param name="existing">Stored fleet.</param>
        /// <param name="updated">Requested update; changed in place.</param>
        /// <param name="requestFieldNames">Top-level field names the request carried, compared case-insensitively.</param>
        public static void KeepServerOwnedFields(Fleet existing, Fleet updated, ISet<string> requestFieldNames)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (updated == null) throw new ArgumentNullException(nameof(updated));
            if (requestFieldNames == null) throw new ArgumentNullException(nameof(requestFieldNames));

            updated.Id = existing.Id;
            updated.TenantId = existing.TenantId;
            updated.UserId = existing.UserId;
            updated.CreatedUtc = existing.CreatedUtc;
            if (!ContainsField(requestFieldNames, nameof(Fleet.Active)))
                updated.Active = existing.Active;
            if (!ContainsField(requestFieldNames, nameof(Fleet.DefaultPlaybooks)))
                updated.DefaultPlaybooks = existing.DefaultPlaybooks;
        }

        #endregion

        #region Private-Methods

        private static bool ContainsField(ISet<string> names, string field)
        {
            foreach (string name in names)
            {
                if (String.Equals(name, field, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        #endregion
    }
}
