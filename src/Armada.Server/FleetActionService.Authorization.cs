namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class FleetActionService
    {
        private async Task<AuthContext> ValidateOperatorAsync(AuthContext? caller, CancellationToken token)
        {
            if (caller == null || !caller.IsAuthenticated || !caller.IsAdmin
                || !String.IsNullOrWhiteSpace(caller.MissionId) || !String.IsNullOrWhiteSpace(caller.AskThreadId)
                || !String.IsNullOrWhiteSpace(caller.AskTurnId) || String.IsNullOrWhiteSpace(caller.TenantId)
                || String.IsNullOrWhiteSpace(caller.UserId))
                throw new UnauthorizedAccessException("Fleet actions require an unscoped global administrator.");
            AuthContext? current = await ReadActiveOperatorAsync(caller.TenantId!, caller.UserId!, token).ConfigureAwait(false);
            if (current == null) throw new UnauthorizedAccessException("The current user is not an active global administrator.");
            return current;
        }
    }
}
