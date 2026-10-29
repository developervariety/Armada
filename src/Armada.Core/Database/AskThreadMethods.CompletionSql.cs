namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed partial class AskThreadMethods
    {
        private async Task<bool> CompleteWithinTransactionAsync(DbConnection connection, string tenantId, string userId, string threadId, string requestId, string nextState, string responseJson, DateTime completedUtc, CancellationToken token)
        {
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                bool completed = await PersistCompletionAsync(connection, transaction, tenantId, userId, threadId, requestId, nextState, responseJson, completedUtc, token).ConfigureAwait(false);
                if (!completed)
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return false;
                }
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            }
        }

        private async Task<bool> PersistCompletionAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string threadId, string requestId, string nextState, string responseJson, DateTime completedUtc, CancellationToken token)
        {
            if (!await WriteTerminalTurnAsync(connection, transaction, tenantId, userId, threadId, requestId, nextState, responseJson, completedUtc, token).ConfigureAwait(false)) return false;
            return await ReleaseActiveTurnAsync(connection, transaction, tenantId, userId, threadId, requestId, completedUtc, token).ConfigureAwait(false);
        }

        private async Task<bool> WriteTerminalTurnAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string threadId, string requestId, string nextState, string responseJson, DateTime completedUtc, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE ask_turns SET state = @terminal, response_json = @response_json, completed_utc = @completed_utc WHERE thread_id = @thread_id AND request_id = @request_id AND state IN (@pending, @running) AND EXISTS (SELECT 1 FROM ask_threads h WHERE h.id = ask_turns.thread_id AND h.tenant_id = @tenant_id AND h.user_id = @user_id AND h.active_turn_id = @request_id);";
                Add(command, "@terminal", nextState);
                Add(command, "@response_json", responseJson);
                AddUtc(command, "@completed_utc", "ask_turns", "completed_utc", completedUtc);
                BindTurnOwner(command, tenantId, userId, threadId, requestId);
                Add(command, "@pending", Armada.Core.Enums.AskTurnStateEnum.Pending.ToString());
                Add(command, "@running", Armada.Core.Enums.AskTurnStateEnum.Running.ToString());
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            }
        }

        private async Task<bool> ReleaseActiveTurnAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string threadId, string requestId, DateTime completedUtc, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE ask_threads SET active_turn_id = NULL, last_activity_utc = @completed_utc, unread_count = unread_count + 1 WHERE id = @thread_id AND tenant_id = @tenant_id AND user_id = @user_id AND active_turn_id = @request_id;";
                AddUtc(command, "@completed_utc", "ask_threads", "last_activity_utc", completedUtc);
                BindTurnOwner(command, tenantId, userId, threadId, requestId);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            }
        }

        private static void BindTurnOwner(DbCommand command, string tenantId, string userId, string threadId, string requestId)
        {
            Add(command, "@tenant_id", tenantId);
            Add(command, "@user_id", userId);
            Add(command, "@thread_id", threadId);
            Add(command, "@request_id", requestId);
        }
    }
}
