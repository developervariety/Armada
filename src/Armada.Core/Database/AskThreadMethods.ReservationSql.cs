namespace Armada.Core.Database
{
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public sealed partial class AskThreadMethods
    {
        private async Task<AskTurn?> ReadTurnWithinTransactionAsync(DbConnection connection, DbTransaction transaction, string tenantId, string userId, string threadId, string requestId, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT " + _TurnSelectColumns + " FROM ask_turns t INNER JOIN ask_threads h ON h.id = t.thread_id WHERE h.tenant_id = @tenant_id AND h.user_id = @user_id AND t.thread_id = @thread_id AND t.request_id = @request_id;";
                Add(command, "@tenant_id", tenantId);
                Add(command, "@user_id", userId);
                Add(command, "@thread_id", threadId);
                Add(command, "@request_id", requestId);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadTurn(reader) : null;
            }
        }

        private static async Task<string?> ReadActiveTurnIdAsync(DbConnection connection, DbTransaction? transaction, string tenantId, string userId, string threadId, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT active_turn_id FROM ask_threads WHERE id = @thread_id AND tenant_id = @tenant_id AND user_id = @user_id;";
                Add(command, "@thread_id", threadId);
                Add(command, "@tenant_id", tenantId);
                Add(command, "@user_id", userId);
                object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return value == null || value == System.DBNull.Value ? null : System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static async Task<long> NextSequenceAsync(DbConnection connection, DbTransaction transaction, string threadId, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM ask_turns WHERE thread_id = @thread_id;";
                Add(command, "@thread_id", threadId);
                return System.Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private async Task InsertTurnAsync(DbConnection connection, DbTransaction transaction, AskTurn turn, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO ask_turns (" + _TurnColumns + ") VALUES (@thread_id, @request_id, @sequence, @captain_id, @content, @show_thinking, @state, @response_json, @created_utc, @completed_utc);";
                Add(command, "@thread_id", turn.ThreadId);
                Add(command, "@request_id", turn.RequestId);
                Add(command, "@sequence", turn.Sequence);
                Add(command, "@captain_id", turn.CaptainId);
                Add(command, "@content", turn.Content);
                ProductionFactSql.AddBool(command, "@show_thinking", turn.ShowThinking, _Provider);
                Add(command, "@state", turn.State.ToString());
                Add(command, "@response_json", null);
                AddUtc(command, "@created_utc", "ask_turns", "created_utc", turn.CreatedUtc);
                AddUtc(command, "@completed_utc", "ask_turns", "completed_utc", null);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
