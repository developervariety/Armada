namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskThreadMethods
    {
        private async Task<AskTurnReservation> ReserveTurnInTransactionAsync(DbConnection connection, string tenantId, string userId, AskTurn turn, CancellationToken token)
        {
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                AskTurn? existing = await ReadTurnWithinTransactionAsync(connection, transaction, tenantId, userId, turn.ThreadId, turn.RequestId, token).ConfigureAwait(false);
                if (existing != null)
                {
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return ExistingReservation(existing, turn);
                }
                if (!await ClaimActiveTurnAsync(connection, transaction, tenantId, userId, turn, token).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return await ResolveClaimFailureAsync(connection, tenantId, userId, turn, token).ConfigureAwait(false);
                }
                turn.Sequence = await NextSequenceAsync(connection, transaction, turn.ThreadId, token).ConfigureAwait(false);
                turn.State = AskTurnStateEnum.Pending;
                turn.CreatedUtc = DateTime.UtcNow;
                await InsertTurnAsync(connection, transaction, turn, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new AskTurnReservation { Result = AskTurnReservationResultEnum.Created, Turn = turn };
            }
        }

        private static AskTurnReservation ExistingReservation(AskTurn existing, AskTurn requested)
        {
            bool sameRequest = String.Equals(existing.Content, requested.Content, StringComparison.Ordinal)
                && existing.ShowThinking == requested.ShowThinking
                && String.Equals(existing.CaptainId, requested.CaptainId, StringComparison.Ordinal);
            return new AskTurnReservation { Result = sameRequest ? AskTurnReservationResultEnum.Existing : AskTurnReservationResultEnum.RequestConflict, Turn = existing };
        }

        private async Task<AskTurnReservation> ResolveClaimFailureAsync(DbConnection connection, string tenantId, string userId, AskTurn requested, CancellationToken token)
        {
            AskTurn? duplicate = await ReadTurnAsync(tenantId, userId, requested.ThreadId, requested.RequestId, token).ConfigureAwait(false);
            if (duplicate != null) return ExistingReservation(duplicate, requested);
            string? activeId = await ReadActiveTurnIdAsync(connection, null, tenantId, userId, requested.ThreadId, token).ConfigureAwait(false);
            if (activeId == null) return new AskTurnReservation { Result = AskTurnReservationResultEnum.NotFound };
            AskTurn? active = await ReadTurnAsync(tenantId, userId, requested.ThreadId, activeId, token).ConfigureAwait(false);
            if (String.Equals(activeId, requested.RequestId, StringComparison.Ordinal) && active != null)
                return ExistingReservation(active, requested);
            return new AskTurnReservation { Result = AskTurnReservationResultEnum.ThreadBusy, Turn = active };
        }
    }
}
