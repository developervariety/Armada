namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class AskThreadMethods
    {
        private async Task<bool> ClaimActiveTurnAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, AskTurn turn, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE ask_threads SET active_turn_id = @request_id, last_activity_utc = @now_utc WHERE id = @thread_id AND tenant_id = @tenant_id AND user_id = @user_id AND active_turn_id IS NULL AND NOT EXISTS (SELECT 1 FROM ask_turns prior WHERE prior.thread_id = ask_threads.id AND prior.request_id = @request_id);";
                Add(command, "@request_id", turn.RequestId);
                AddUtc(command, "@now_utc", "ask_threads", "last_activity_utc", DateTime.UtcNow);
                Add(command, "@thread_id", turn.ThreadId);
                Add(command, "@tenant_id", tenantId);
                Add(command, "@user_id", userId);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            }
        }
    }
}
