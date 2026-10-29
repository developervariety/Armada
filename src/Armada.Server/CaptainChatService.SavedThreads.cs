namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.WebSocket;

    public partial class CaptainChatService
    {
        /// <summary>Run a saved conversation with events restricted to its exact human owner.</summary>
        internal Task<CaptainChatResponse> ChatSavedAsync(AuthContext caller, AskTurn turn, CaptainChatRequest request, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated || !caller.IsAdmin || !String.IsNullOrWhiteSpace(caller.MissionId)
                || String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId))
                throw new UnauthorizedAccessException("Saved conversations require an authenticated human owner.");
            if (turn == null) throw new ArgumentNullException(nameof(turn));
            return ChatWithScopeAsync(caller, turn.CaptainId, request,
                WebSocketDeliveryScope.ForInteractiveOwner(caller.TenantId, caller.UserId), turn, token);
        }

        /// <summary>Read the tool calls a saved turn has made while it runs.</summary>
        /// <param name="threadId">Thread identifier.</param>
        /// <param name="requestId">Saved request identifier.</param>
        /// <returns>The calls so far, or an empty list when the turn is not running here.</returns>
        internal List<ChatToolCall> SnapshotLiveToolCalls(string threadId, string requestId)
        {
            return _LiveToolCalls.TryGetValue(LiveToolCallKey(threadId, requestId), out ChatToolCallRecorder? recorder)
                ? recorder.Snapshot() : new List<ChatToolCall>();
        }

        /// <summary>Forget the live tool calls of a saved turn once its outcome is stored or abandoned.</summary>
        /// <param name="threadId">Thread identifier.</param>
        /// <param name="requestId">Saved request identifier.</param>
        internal void DiscardLiveToolCalls(string threadId, string requestId)
        {
            _LiveToolCalls.TryRemove(LiveToolCallKey(threadId, requestId), out ChatToolCallRecorder? removed);
        }

        private static string LiveToolCallKey(string threadId, string requestId)
        {
            return threadId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + threadId + requestId;
        }
    }
}
