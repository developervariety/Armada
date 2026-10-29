namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    public sealed partial class AskThreadMethods
    {
        /// <inheritdoc />
        public async Task<AskThreadDeleteOutcomeEnum> TryDeleteAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            if (String.IsNullOrWhiteSpace(threadId)) return AskThreadDeleteOutcomeEnum.NotFound;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand delete = connection.CreateCommand())
                    {
                        delete.CommandText = "DELETE FROM ask_threads WHERE id = @id AND tenant_id = @tenant_id AND user_id = @user_id AND archived = @archived AND active_turn_id IS NULL"
                            + " AND NOT EXISTS (SELECT 1 FROM ask_turns WHERE ask_turns.thread_id = ask_threads.id AND ask_turns.state IN (@turn_pending, @turn_running))"
                            + " AND NOT EXISTS (SELECT 1 FROM ask_action_proposals WHERE ask_action_proposals.thread_id = ask_threads.id AND (ask_action_proposals.state = @proposal_executing OR (ask_action_proposals.state = @proposal_pending AND ask_action_proposals.expires_utc > @now)));";
                        Add(delete, "@id", threadId);
                        Add(delete, "@tenant_id", tenantId);
                        Add(delete, "@user_id", userId);
                        ProductionFactSql.AddBool(delete, "@archived", true, _Provider);
                        Add(delete, "@turn_pending", AskTurnStateEnum.Pending.ToString());
                        Add(delete, "@turn_running", AskTurnStateEnum.Running.ToString());
                        Add(delete, "@proposal_pending", AskProposalStateEnum.Pending.ToString());
                        Add(delete, "@proposal_executing", AskProposalStateEnum.Executing.ToString());
                        AddUtc(delete, "@now", "ask_action_proposals", "expires_utc", DateTime.UtcNow);
                        if (await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1) return AskThreadDeleteOutcomeEnum.Deleted;
                    }
                    return await ReadRefusalAsync(connection, tenantId, userId, threadId, token).ConfigureAwait(false);
                }
            }
        }

        private async Task<AskThreadDeleteOutcomeEnum> ReadRefusalAsync(DbConnection connection, string tenantId, string userId, string threadId, CancellationToken token)
        {
            using (DbCommand read = connection.CreateCommand())
            {
                read.CommandText = "SELECT archived, active_turn_id FROM ask_threads WHERE id = @id AND tenant_id = @tenant_id AND user_id = @user_id;";
                Add(read, "@id", threadId);
                Add(read, "@tenant_id", tenantId);
                Add(read, "@user_id", userId);
                using (DbDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return AskThreadDeleteOutcomeEnum.NotFound;
                    if (!ProductionFactSql.ReadBool(reader["archived"])) return AskThreadDeleteOutcomeEnum.NotArchived;
                    if (!String.IsNullOrEmpty(Text(reader, "active_turn_id"))) return AskThreadDeleteOutcomeEnum.OpenTurn;
                }
            }
            using (DbCommand turns = connection.CreateCommand())
            {
                turns.CommandText = "SELECT COUNT(*) FROM ask_turns WHERE thread_id = @id AND state IN (@turn_pending, @turn_running);";
                Add(turns, "@id", threadId);
                Add(turns, "@turn_pending", AskTurnStateEnum.Pending.ToString());
                Add(turns, "@turn_running", AskTurnStateEnum.Running.ToString());
                if (Convert.ToInt64(await turns.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0) return AskThreadDeleteOutcomeEnum.OpenTurn;
            }
            return AskThreadDeleteOutcomeEnum.OpenProposal;
        }
    }
}
