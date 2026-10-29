namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskThreadService
    {
        /// <summary>Reserve a request durably and run it at most once with server-owned context.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="id">Thread identifier.</param>
        /// <param name="request">Message and stable request identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The saved turn or a named refusal.</returns>
        public async Task<AskTurnStart> SendTurnAsync(AuthContext caller, string id, AskTurnSendRequest request, CancellationToken token = default)
        {
            RequireOwner(caller);
            ValidateSend(request);
            AskThread? thread = await GetThreadAsync(caller, id, token).ConfigureAwait(false);
            if (thread == null) return new AskTurnStart { StatusCode = 404, Message = "Conversation not found." };
            // A replay remains readable if a captain was removed or thread metadata changed after completion.
            AskTurn? prior = await _Database.AskThreads.ReadTurnAsync(caller.TenantId!, caller.UserId!, id, request.RequestId, token).ConfigureAwait(false);
            if (prior != null)
            {
                AskTurnReservationResultEnum result = prior.Content == request.Content && prior.ShowThinking == request.ShowThinking
                    ? AskTurnReservationResultEnum.Existing : AskTurnReservationResultEnum.RequestConflict;
                return Replay(new AskTurnReservation { Result = result, Turn = prior });
            }
            if (thread.Archived) return new AskTurnStart { StatusCode = 409, Message = "Unarchive this conversation before sending a new turn." };
            if (String.IsNullOrWhiteSpace(thread.CaptainId)) return new AskTurnStart { StatusCode = 400, Message = "Select a captain before sending." };
            await ValidateCaptainAsync(caller, thread.CaptainId, token).ConfigureAwait(false);
            AskTurn turn = new AskTurn
            {
                ThreadId = id, RequestId = request.RequestId, CaptainId = thread.CaptainId!,
                Content = request.Content, ShowThinking = request.ShowThinking
            };
            AskTurnReservation reservation = await _Database.AskThreads.TryReserveTurnAsync(caller.TenantId!, caller.UserId!, turn, token).ConfigureAwait(false);
            if (reservation.Result != AskTurnReservationResultEnum.Created) return Replay(reservation);
            return await ExecuteAsync(caller, reservation.Turn ?? turn, token).ConfigureAwait(false);
        }

        private async Task<AskTurnStart> ExecuteAsync(AuthContext caller, AskTurn turn, CancellationToken token)
        {
            if (!_Active.TryAdd(RuntimeKey(turn), true))
                return new AskTurnStart { StatusCode = 409, Message = "The saved turn has no exclusive runtime owner." };
            try
            {
                bool claimed = await _Database.AskThreads.MarkRunningAsync(caller.TenantId!, caller.UserId!, turn.ThreadId, turn.RequestId, token).ConfigureAwait(false);
                if (!claimed) throw new InvalidOperationException("The saved turn could not claim runtime execution.");
                List<AskTurn> history = await _Database.AskThreads.ReadHistoryAsync(caller.TenantId!, caller.UserId!, turn.ThreadId, 32, token).ConfigureAwait(false);
                CaptainChatRequest chatRequest = new CaptainChatRequest
                {
                    Message = turn.Content, TurnId = turn.RequestId, ShowThinking = turn.ShowThinking,
                    History = BuildHistory(history)
                };
                CaptainChatResponse response = await _Chat.ChatSavedAsync(caller, turn, chatRequest, token).ConfigureAwait(false);
                // The HTTP client disconnecting must not discard a result already returned by the runtime.
                bool stored = await _Database.AskThreads.CompleteTurnAsync(caller.TenantId!, caller.UserId!, turn.ThreadId, turn.RequestId, response, CancellationToken.None).ConfigureAwait(false);
                if (!stored) throw new InvalidOperationException("The runtime ended but its saved result could not be confirmed. The turn remains fenced.");
                AskTurn? completed = await _Database.AskThreads.ReadTurnAsync(caller.TenantId!, caller.UserId!, turn.ThreadId, turn.RequestId, CancellationToken.None).ConfigureAwait(false);
                return new AskTurnStart { StatusCode = 200, Turn = completed };
            }
            finally
            {
                _Active.TryRemove(RuntimeKey(turn), out bool removed);
                _Chat.DiscardLiveToolCalls(turn.ThreadId, turn.RequestId);
            }
        }

        private static List<CaptainChatMessage> BuildHistory(List<AskTurn> turns)
        {
            List<CaptainChatMessage> result = new List<CaptainChatMessage>();
            int characters = 0;
            for (int index = turns.Count - 1; index >= 0; index--)
            {
                AskTurn turn = turns[index];
                string reply = turn.Response?.Reply ?? String.Empty;
                if (characters + turn.Content.Length + reply.Length > 32768) break;
                result.Insert(0, new CaptainChatMessage { Role = "assistant", Content = reply });
                result.Insert(0, new CaptainChatMessage { Role = "user", Content = turn.Content });
                characters += turn.Content.Length + reply.Length;
            }
            return result;
        }
    }
}
