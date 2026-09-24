namespace Armada.Core.Authorization
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for acting on a user account or on the credentials that sign in as it. A global
    /// administrator may act on any user. Any other caller may act on its own account. A tenant administrator
    /// may also act on the other users of its own tenant, except a global administrator and a protected user:
    /// changing, deactivating, deleting, resetting the password of, or minting a credential for either would
    /// let a tenant administrator take over an account with more authority than its own.
    ///
    /// The permission matrix cannot own this rule because it depends on the target user, so every user and
    /// credential handler asks it after it has read the target in the caller's tenant.
    /// </summary>
    public static class UserManagementRule
    {
        #region Public-Methods

        /// <summary>
        /// True when the caller may change, delete, or manage the credentials of the target user.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="target">Target user.</param>
        /// <returns>True when the caller may act on the target user.</returns>
        public static bool CanManage(AuthContext caller, UserMaster target)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (caller.IsAdmin) return true;
            if (!OwnershipPolicy.SameTenant(caller.TenantId, target.TenantId)) return false;
            if (!String.IsNullOrEmpty(caller.UserId) && String.Equals(caller.UserId, target.Id, StringComparison.Ordinal)) return true;
            if (!caller.IsTenantAdmin) return false;
            return !target.IsAdmin && !target.IsProtected;
        }

        /// <summary>
        /// True when the caller may read the bearer token of a credential. A global administrator reads every
        /// token; any other caller reads only the tokens of its own credentials. Everyone else sees the
        /// credential with its token redacted.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="credential">Credential.</param>
        /// <returns>True when the token may be returned.</returns>
        public static bool CanReadToken(AuthContext caller, Credential credential)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (caller.IsAdmin) return true;
            return !String.IsNullOrEmpty(caller.UserId) && String.Equals(caller.UserId, credential.UserId, StringComparison.Ordinal);
        }

        #endregion
    }
}
