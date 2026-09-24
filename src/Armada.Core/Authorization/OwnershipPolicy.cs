namespace Armada.Core.Authorization
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The one ownership rule for tenant-wide and user-specific records. Native memory, personas,
    /// pipelines and prompt templates all decide visibility and edit rights through this class.
    ///
    /// A global administrator sees and changes everything. Nobody else crosses a tenant boundary.
    /// Inside the tenant a tenant administrator sees and changes every record, any user sees
    /// tenant-wide records, and only the owning user sees or changes a user-specific record.
    /// A shared (built-in) record is readable by every authenticated caller.
    /// </summary>
    public static class OwnershipPolicy
    {
        #region Public-Methods

        /// <summary>
        /// Tenant a caller acts in. A caller without a tenant acts in the default tenant.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <returns>Tenant identifier.</returns>
        public static string TenantOf(AuthContext auth)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            return String.IsNullOrWhiteSpace(auth.TenantId) ? Constants.DefaultTenantId : auth.TenantId!;
        }

        /// <summary>
        /// User a caller acts as. A caller without a user acts as the default user.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <returns>User identifier.</returns>
        public static string UserOf(AuthContext auth)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            return String.IsNullOrWhiteSpace(auth.UserId) ? Constants.DefaultUserId : auth.UserId!;
        }

        /// <summary>
        /// True when the caller is a tenant or global administrator.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <returns>True for an administrator.</returns>
        public static bool IsAdministrator(AuthContext auth)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            return auth.IsAdmin || auth.IsTenantAdmin;
        }

        /// <summary>
        /// Decide whether a caller may read a record.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <param name="tenantId">Owning tenant.</param>
        /// <param name="userId">Owning user.</param>
        /// <param name="scope">Ownership scope.</param>
        /// <param name="isShared">True for a server-seeded record every caller may read.</param>
        /// <returns>True when the caller may read the record.</returns>
        public static bool CanView(AuthContext auth, string? tenantId, string? userId, OwnershipScopeEnum scope, bool isShared = false)
        {
            if (auth == null || !auth.IsAuthenticated) return false;
            if (auth.IsAdmin) return true;
            if (isShared) return true;
            if (!String.Equals(TenantOfRecord(tenantId), TenantOf(auth), StringComparison.Ordinal)) return false;
            if (auth.IsTenantAdmin) return true;
            if (scope == OwnershipScopeEnum.TenantWide) return true;
            return String.Equals(UserOfRecord(userId), UserOf(auth), StringComparison.Ordinal);
        }

        /// <summary>
        /// Decide whether a caller may change or delete a record. Being shared never grants edit rights.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <param name="tenantId">Owning tenant.</param>
        /// <param name="userId">Owning user.</param>
        /// <param name="scope">Ownership scope.</param>
        /// <returns>True when the caller may change the record.</returns>
        public static bool CanEdit(AuthContext auth, string? tenantId, string? userId, OwnershipScopeEnum scope)
        {
            if (auth == null || !auth.IsAuthenticated) return false;
            if (auth.IsAdmin) return true;
            if (!String.Equals(TenantOfRecord(tenantId), TenantOf(auth), StringComparison.Ordinal)) return false;
            if (auth.IsTenantAdmin) return true;
            if (scope != OwnershipScopeEnum.UserSpecific) return false;
            return String.Equals(UserOfRecord(userId), UserOf(auth), StringComparison.Ordinal);
        }

        /// <summary>
        /// Decide whether a record may be used on behalf of the owner of other work, such as the
        /// vessel or mission a dispatch runs for. The owner is treated as an ordinary user, so a
        /// user-specific record of one user never enters another user's work, even when an
        /// administrator started it.
        /// </summary>
        /// <param name="ownerTenantId">Tenant that owns the work.</param>
        /// <param name="ownerUserId">User that owns the work.</param>
        /// <param name="tenantId">Record's owning tenant.</param>
        /// <param name="userId">Record's owning user.</param>
        /// <param name="scope">Record's ownership scope.</param>
        /// <param name="isShared">True for a server-seeded record.</param>
        /// <returns>True when the record may be used for the owner's work.</returns>
        public static bool CanUseFor(string? ownerTenantId, string? ownerUserId, string? tenantId, string? userId, OwnershipScopeEnum scope, bool isShared)
        {
            if (isShared) return true;
            if (!String.Equals(TenantOfRecord(tenantId), TenantOfRecord(ownerTenantId), StringComparison.Ordinal)) return false;
            if (scope == OwnershipScopeEnum.TenantWide) return true;
            return String.Equals(UserOfRecord(userId), UserOfRecord(ownerUserId), StringComparison.Ordinal);
        }

        /// <summary>
        /// True when two stored records belong to the same tenant. A record without a tenant belongs to
        /// the default tenant. A system path that reads a linked record by id, without a caller, applies
        /// this rule so a link written by one tenant never reaches another tenant's record.
        /// </summary>
        /// <param name="tenantId">First record's tenant, possibly empty.</param>
        /// <param name="otherTenantId">Second record's tenant, possibly empty.</param>
        /// <returns>True when both records belong to the same tenant.</returns>
        public static bool SameTenant(string? tenantId, string? otherTenantId)
        {
            return String.Equals(TenantOfRecord(tenantId), TenantOfRecord(otherTenantId), StringComparison.Ordinal);
        }

        /// <summary>
        /// Tenant a stored record belongs to. A record without a tenant belongs to the default tenant,
        /// exactly as a caller without a tenant acts in it, so records written before tenancy keep working.
        /// </summary>
        /// <param name="tenantId">Stored tenant, possibly empty.</param>
        /// <returns>Tenant identifier.</returns>
        public static string TenantOfRecord(string? tenantId)
        {
            return String.IsNullOrWhiteSpace(tenantId) ? Constants.DefaultTenantId : tenantId!;
        }

        /// <summary>
        /// User a stored record belongs to. A record without a user belongs to the default user,
        /// exactly as a caller without a user acts as it.
        /// </summary>
        /// <param name="userId">Stored user, possibly empty.</param>
        /// <returns>User identifier.</returns>
        public static string UserOfRecord(string? userId)
        {
            return String.IsNullOrWhiteSpace(userId) ? Constants.DefaultUserId : userId!;
        }

        /// <summary>
        /// Decide whether a caller may read an owned configuration record.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <param name="record">Record.</param>
        /// <returns>True when the caller may read the record.</returns>
        public static bool CanView(AuthContext auth, IOwnedRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return CanView(auth, record.TenantId, record.UserId, record.OwnershipScope, record.IsBuiltIn);
        }

        /// <summary>
        /// Decide whether a caller may change an owned configuration record. A built-in record is used by
        /// every tenant, so only a global administrator may change it; a tenant administrator of the tenant
        /// that stores it may not.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <param name="record">Record.</param>
        /// <returns>True when the caller may change the record.</returns>
        public static bool CanEdit(AuthContext auth, IOwnedRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.IsBuiltIn) return auth != null && auth.IsAuthenticated && auth.IsAdmin;
            return CanEdit(auth, record.TenantId, record.UserId, record.OwnershipScope);
        }

        /// <summary>
        /// Decide whether an owned configuration record may be used for another record owner's work.
        /// </summary>
        /// <param name="ownerTenantId">Tenant that owns the work.</param>
        /// <param name="ownerUserId">User that owns the work.</param>
        /// <param name="record">Record.</param>
        /// <returns>True when the record may be used.</returns>
        public static bool CanUseFor(string? ownerTenantId, string? ownerUserId, IOwnedRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return CanUseFor(ownerTenantId, ownerUserId, record.TenantId, record.UserId, record.OwnershipScope, record.IsBuiltIn);
        }

        #endregion
    }
}
