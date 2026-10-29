namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskThreadService
    {
        private static void RequireOwner(AuthContext caller)
        {
            if (caller == null || !caller.IsAuthenticated || !String.IsNullOrWhiteSpace(caller.MissionId)
                || !String.IsNullOrWhiteSpace(caller.AskThreadId) || !String.IsNullOrWhiteSpace(caller.AskTurnId)
                || String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId))
                throw new UnauthorizedAccessException("Saved conversations require an authenticated human owner.");
        }

        private async Task ValidateCaptainAsync(AuthContext caller, string? id, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            if (await _Chat.FindCaptainInScopeAsync(caller, id.Trim(), token).ConfigureAwait(false) == null)
                throw new ArgumentException("Captain not found.");
        }

        private static string? CleanCaptain(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string ValidateTitle(string? title)
        {
            if (String.IsNullOrWhiteSpace(title) || title.Trim().Length > 512)
                throw new ArgumentException("A title of 1 to 512 characters is required.");
            return title.Trim();
        }

        private static void ValidateSend(AskTurnSendRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128
                || !String.Equals(request.RequestId, request.RequestId.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("A request identifier of 1 to 128 characters without surrounding spaces is required.");
            if (String.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 32000)
                throw new ArgumentException("A message of 1 to 32000 characters is required.");
        }

        private static string RuntimeKey(AskTurn turn)
        {
            return turn.ThreadId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + turn.ThreadId + turn.RequestId;
        }

        private void Decorate(AskTurn turn)
        {
            if (turn.State != AskTurnStateEnum.Pending && turn.State != AskTurnStateEnum.Running) return;
            if (!_Active.ContainsKey(RuntimeKey(turn)))
                turn.State = AskTurnStateEnum.Uncertain;
            else
                turn.LiveToolCalls = _Chat.SnapshotLiveToolCalls(turn.ThreadId, turn.RequestId);
        }

        private AskTurnStart Replay(AskTurnReservation reservation)
        {
            AskTurn? turn = reservation.Turn;
            if (turn != null) Decorate(turn);
            int status = reservation.Result switch
            {
                AskTurnReservationResultEnum.NotFound => 404,
                AskTurnReservationResultEnum.Existing when turn?.State == AskTurnStateEnum.Completed || turn?.State == AskTurnStateEnum.Failed => 200,
                AskTurnReservationResultEnum.Existing when turn?.State == AskTurnStateEnum.Pending || turn?.State == AskTurnStateEnum.Running => 202,
                _ => 409
            };
            string? message = reservation.Result switch
            {
                AskTurnReservationResultEnum.RequestConflict => "This request identifier belongs to a different message or captain.",
                AskTurnReservationResultEnum.ThreadBusy => "This conversation already has an active or uncertain turn.",
                AskTurnReservationResultEnum.NotFound => "Conversation not found.",
                _ when turn?.State == AskTurnStateEnum.Uncertain => "The previous runtime outcome is uncertain. This request cannot run again.",
                _ => null
            };
            return new AskTurnStart { StatusCode = status, Turn = turn, Message = message };
        }
    }
}
