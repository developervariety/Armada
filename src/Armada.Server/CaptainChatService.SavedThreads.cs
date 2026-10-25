namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.WebSocket;

    public partial class CaptainChatService
    {
        /// <summary>Run a saved conversation with events restricted to its exact human owner.</summary>
        internal Task<CaptainChatResponse> ChatSavedAsync(AuthContext caller, string captainId, CaptainChatRequest request, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated || !String.IsNullOrWhiteSpace(caller.MissionId)
                || String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId))
                throw new UnauthorizedAccessException("Saved conversations require an authenticated human owner.");
            return ChatWithScopeAsync(caller, captainId, request,
                WebSocketDeliveryScope.ForInteractiveOwner(caller.TenantId, caller.UserId), token);
        }
    }
}
