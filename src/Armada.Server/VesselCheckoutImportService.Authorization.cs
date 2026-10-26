namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class VesselCheckoutImportService
    {
        /// <summary>Check import caller claims. The service also checks current database authority.</summary>
        /// <param name="caller">Caller to inspect.</param>
        /// <returns>True for an authenticated global-administrator operator identity.</returns>
        public static bool IsUnscopedGlobalAdminCaller(AuthContext? caller)
        {
            return caller != null && caller.IsAuthenticated && caller.IsAdmin
                && caller.MissionId == null && caller.AskThreadId == null && caller.AskTurnId == null;
        }

        private async Task ValidateCallerAsync(AuthContext? caller, CancellationToken token)
        {
            if (caller == null || !IsUnscopedGlobalAdminCaller(caller)
                || String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId))
                throw new UnauthorizedAccessException("Checkout discovery and import require global administrator operator credentials.");

            UserMaster? user = await _Database.Users.ReadByIdAsync(caller.UserId!, token).ConfigureAwait(false);
            TenantMetadata? tenant = await _Database.Tenants.ReadAsync(caller.TenantId!, token).ConfigureAwait(false);
            if (user == null || tenant == null || !user.Active || !tenant.Active || !user.IsAdmin
                || !String.Equals(user.TenantId, caller.TenantId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The current user is not an active global administrator.");
        }
    }
}
