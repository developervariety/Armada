namespace Armada.Core.Harbor
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule that decides whether a verified caller may send a command to a Harbor runner. A global
    /// administrator may command any runner. Otherwise the caller must be in the runner owner's tenant and be
    /// either the owner or a tenant administrator.
    /// </summary>
    public static class HarborCommandAuthorizer
    {
        /// <summary>Whether the caller may command a runner owned by the given identity.</summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="owner">Verified runner owner.</param>
        /// <returns>True when the command is authorized.</returns>
        public static bool CanCommand(AuthContext caller, HarborRunnerIdentity owner)
        {
            if (caller == null || owner == null || !caller.IsAuthenticated) return false;
            if (String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId)) return false;
            if (caller.IsAdmin) return true;
            if (!String.Equals(caller.TenantId, owner.TenantId, StringComparison.Ordinal)) return false;
            return caller.IsTenantAdmin || String.Equals(caller.UserId, owner.UserId, StringComparison.Ordinal);
        }
    }
}
