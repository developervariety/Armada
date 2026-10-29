namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Checks that a saved-turn credential still names a running turn of an active human owner.</summary>
    internal static class AskTurnBinding
    {
        /// <summary>
        /// Whether the caller's thread is open and its active turn is the caller's running turn, and the owner is still an
        /// active administrator of an active tenant.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Saved-turn caller.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the turn is bound and running.</returns>
        internal static async Task<bool> IsBoundRunningTurnAsync(DatabaseDriver database, AuthContext caller, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId)) return false;
            UserMaster? user = await database.Users.ReadByIdAsync(caller.UserId).ConfigureAwait(false);
            TenantMetadata? tenant = await database.Tenants.ReadAsync(caller.TenantId).ConfigureAwait(false);
            if (user == null || !user.Active || !user.IsAdmin || user.TenantId != caller.TenantId || tenant == null || !tenant.Active) return false;
            AskThread? thread = await database.AskThreads.ReadAsync(caller.TenantId, caller.UserId, caller.AskThreadId!, token).ConfigureAwait(false);
            AskTurn? turn = await database.AskThreads.ReadTurnAsync(caller.TenantId, caller.UserId, caller.AskThreadId!, caller.AskTurnId!, token).ConfigureAwait(false);
            return thread != null && !thread.Archived && thread.ActiveTurnId == caller.AskTurnId
                && turn != null && turn.State == AskTurnStateEnum.Running;
        }
    }
}
