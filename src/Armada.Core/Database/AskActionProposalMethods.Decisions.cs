namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskActionProposalMethods
    {
        /// <inheritdoc />
        public Task<bool> TryClaimAsync(string tenantId, string userId, string threadId, string proposalId, string fingerprint, DateTime utcNow, CancellationToken token = default)
            => TransitionAsync(tenantId, userId, threadId, proposalId, fingerprint, AskProposalStateEnum.Pending, AskProposalStateEnum.Executing, utcNow, true, token);

        /// <inheritdoc />
        public Task<bool> TryRejectAsync(string tenantId, string userId, string threadId, string proposalId, string fingerprint, DateTime utcNow, CancellationToken token = default)
            => TransitionAsync(tenantId, userId, threadId, proposalId, fingerprint, AskProposalStateEnum.Pending, AskProposalStateEnum.Rejected, utcNow, false, token);

        /// <inheritdoc />
        public async Task<bool> RecordExecutingWorkAsync(string tenantId, string userId, string threadId, string proposalId, List<AskWorkReference> workReferences, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_action_proposals SET work_references_json=@work_json WHERE tenant_id=@tenant_id AND user_id=@user_id AND thread_id=@thread_id AND id=@id AND state=@executing;";
                    Add(command, "@work_json", JsonSerializer.Serialize(BoundWorkReferences(workReferences))); AddOwner(command, tenantId, userId, threadId, proposalId); Add(command, "@executing", AskProposalStateEnum.Executing.ToString());
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> CompleteAsync(string tenantId, string userId, string threadId, string proposalId, AskProposalStateEnum state, string? outcome, List<AskWorkReference> workReferences, DateTime utcNow, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            if (state != AskProposalStateEnum.Executed && state != AskProposalStateEnum.Failed) throw new ArgumentException("Completion state must be Executed or Failed.", nameof(state));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_action_proposals SET state=@state, completed_utc=@now, outcome=@outcome, work_references_json=@work_json WHERE tenant_id=@tenant_id AND user_id=@user_id AND thread_id=@thread_id AND id=@id AND state=@executing;";
                    Add(command, "@state", state.ToString()); AddUtc(command, "@now", utcNow); Add(command, "@outcome", Bound(outcome, 2000));
                    Add(command, "@work_json", JsonSerializer.Serialize(BoundWorkReferences(workReferences))); AddOwner(command, tenantId, userId, threadId, proposalId); Add(command, "@executing", AskProposalStateEnum.Executing.ToString());
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        private async Task<bool> TransitionAsync(string tenantId, string userId, string threadId, string proposalId, string fingerprint, AskProposalStateEnum from, AskProposalStateEnum to, DateTime now, bool requireUnexpired, CancellationToken token)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_action_proposals SET state=@to, decided_utc=@now WHERE tenant_id=@tenant_id AND user_id=@user_id AND thread_id=@thread_id AND id=@id AND fingerprint=@fingerprint AND state=@from" + (requireUnexpired ? " AND expires_utc>@now" : String.Empty) + ";";
                    Add(command, "@to", to.ToString()); AddUtc(command, "@now", now); AddOwner(command, tenantId, userId, threadId, proposalId); Add(command, "@fingerprint", fingerprint); Add(command, "@from", from.ToString());
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

    }
}
