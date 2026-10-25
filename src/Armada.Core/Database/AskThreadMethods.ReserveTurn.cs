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
        /// <inheritdoc />
        public async Task<AskTurnReservation> TryReserveTurnAsync(string tenantId, string userId, AskTurn turn, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            if (turn == null) throw new ArgumentNullException(nameof(turn));
            if (String.IsNullOrWhiteSpace(turn.ThreadId) || String.IsNullOrWhiteSpace(turn.RequestId) || String.IsNullOrWhiteSpace(turn.CaptainId))
                throw new ArgumentException("Thread, request, and captain identifiers are required.", nameof(turn));
            if (turn.RequestId.Length > 128 || turn.Content.Length > 32000) throw new ArgumentException("Ask turn exceeds its stored field limit.", nameof(turn));

            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                    return await ReserveTurnInTransactionAsync(connection, tenantId, userId, turn, token).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<AskTurn?> ReadTurnAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _TurnSelectColumns + " FROM ask_turns t INNER JOIN ask_threads h ON h.id = t.thread_id WHERE h.tenant_id = @tenant_id AND h.user_id = @user_id AND t.thread_id = @thread_id AND t.request_id = @request_id;";
                    Add(command, "@tenant_id", tenantId);
                    Add(command, "@user_id", userId);
                    Add(command, "@thread_id", threadId);
                    Add(command, "@request_id", requestId);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadTurn(reader) : null;
                }
            }
        }
    }
}
