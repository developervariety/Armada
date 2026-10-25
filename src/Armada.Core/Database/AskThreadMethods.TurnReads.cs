namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class AskThreadMethods
    {
        /// <inheritdoc />
        public async Task<List<AskTurn>> ReadHistoryAsync(string tenantId, string userId, string threadId, int limit, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            int boundedLimit = limit < 1 ? 1 : Math.Min(limit, 50);
            List<AskTurn> turns = await ReadTurnPageAsync(tenantId, userId, threadId, null, boundedLimit, " AND t.state = @state", true, token).ConfigureAwait(false);
            turns.Reverse();
            return turns;
        }

        /// <inheritdoc />
        public async Task<List<AskTurn>> EnumerateTurnsAsync(string tenantId, string userId, string threadId, long? beforeSequence, int pageSize, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            int boundedSize = pageSize < 1 ? 50 : Math.Min(pageSize, 200);
            List<AskTurn> turns = await ReadTurnPageAsync(tenantId, userId, threadId, beforeSequence, boundedSize, String.Empty, true, token).ConfigureAwait(false);
            turns.Reverse();
            return turns;
        }

        /// <inheritdoc />
        public async Task<bool> MarkRunningAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_turns SET state = @running WHERE thread_id = @thread_id AND request_id = @request_id AND state = @pending AND EXISTS (SELECT 1 FROM ask_threads h WHERE h.id = ask_turns.thread_id AND h.tenant_id = @tenant_id AND h.user_id = @user_id AND h.active_turn_id = @request_id);";
                    Add(command, "@running", Armada.Core.Enums.AskTurnStateEnum.Running.ToString());
                    Add(command, "@thread_id", threadId);
                    Add(command, "@request_id", requestId);
                    Add(command, "@pending", Armada.Core.Enums.AskTurnStateEnum.Pending.ToString());
                    Add(command, "@tenant_id", tenantId);
                    Add(command, "@user_id", userId);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        private async Task<List<AskTurn>> ReadTurnPageAsync(string tenantId, string userId, string threadId, long? beforeSequence, int limit, string extra, bool descending, CancellationToken token)
        {
            List<AskTurn> turns = new List<AskTurn>();
            string cursor = beforeSequence.HasValue ? " AND t.sequence < @before_sequence" : String.Empty;
            string sort = descending ? " DESC" : " ASC";
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + (_Provider == Armada.Core.Enums.DatabaseTypeEnum.SqlServer ? "TOP (@row_limit) " : String.Empty) + _TurnSelectColumns + " FROM ask_turns t INNER JOIN ask_threads h ON h.id = t.thread_id WHERE h.tenant_id = @tenant_id AND h.user_id = @user_id AND t.thread_id = @thread_id" + cursor + extra + " ORDER BY t.sequence" + sort + (_Provider == Armada.Core.Enums.DatabaseTypeEnum.SqlServer ? String.Empty : " LIMIT @row_limit") + ";";
                    Add(command, "@tenant_id", tenantId);
                    Add(command, "@user_id", userId);
                    Add(command, "@thread_id", threadId);
                    if (beforeSequence.HasValue) Add(command, "@before_sequence", beforeSequence.Value);
                    if (extra.Length > 0) Add(command, "@state", Armada.Core.Enums.AskTurnStateEnum.Completed.ToString());
                    Add(command, "@row_limit", limit);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) turns.Add(ReadTurn(reader));
                }
            }
            return turns;
        }
    }
}
