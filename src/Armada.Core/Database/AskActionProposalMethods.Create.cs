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
        public async Task<AskActionProposal?> TryCreateAsync(AskActionProposal proposal, CancellationToken token = default)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            ValidateOwner(proposal.TenantId, proposal.UserId);
            if (String.IsNullOrWhiteSpace(proposal.Id) || !proposal.Id.StartsWith(Constants.AskActionProposalIdPrefix, StringComparison.Ordinal)) throw new ArgumentException("Proposal identifier must use the aap_ prefix.", nameof(proposal));
            if (String.IsNullOrWhiteSpace(proposal.ThreadId) || String.IsNullOrWhiteSpace(proposal.RequestId) || String.IsNullOrWhiteSpace(proposal.ToolName)) throw new ArgumentException("Thread, request and tool identifiers are required.", nameof(proposal));
            if (proposal.ThreadId.Length > 128 || proposal.RequestId.Length > 128 || proposal.TenantId.Length > 128 || proposal.UserId.Length > 128) throw new ArgumentException("Owner and request identifiers must not exceed 128 characters.", nameof(proposal));
            if (String.IsNullOrWhiteSpace(proposal.Fingerprint) || proposal.Fingerprint.Length != 64 || !IsHex(proposal.Fingerprint)) throw new ArgumentException("Fingerprint must be a SHA-256 hex value.", nameof(proposal));
            if (proposal.ToolName.Length > 128) throw new ArgumentException("Tool name exceeds 128 characters.", nameof(proposal));
            if (String.IsNullOrEmpty(proposal.ArgumentsJson) || proposal.ArgumentsJson.Length > 32000) throw new ArgumentException("Arguments JSON is empty or exceeds the storage limit.", nameof(proposal));
            using (JsonDocument.Parse(proposal.ArgumentsJson)) { }
            proposal.State = AskProposalStateEnum.Pending;
            proposal.DecidedUtc = null; proposal.CompletedUtc = null; proposal.Outcome = null; proposal.WorkReferences = new List<AskWorkReference>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    AskActionProposal? found = await FindDuplicateAsync(connection, proposal, token).ConfigureAwait(false);
                    if (found != null) return found;
                    AskActionProposal? inserted = await InsertEligibleAsync(connection, proposal, token).ConfigureAwait(false);
                    if (inserted != null) return inserted;
                    return await FindDuplicateAsync(connection, proposal, token).ConfigureAwait(false);
                }
            }
        }

        private async Task<AskActionProposal?> InsertEligibleAsync(DbConnection connection, AskActionProposal proposal, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO ask_action_proposals (" + _Columns + ") SELECT @id, @tenant_id, @user_id, @thread_id, @request_id, @tool_name, @arguments_json, @fingerprint, @state, @created_utc, @expires_utc, NULL, NULL, NULL, @work_json WHERE EXISTS (SELECT 1 FROM ask_threads t JOIN ask_turns a ON a.thread_id=t.id WHERE t.id=@thread_id AND t.tenant_id=@tenant_id AND t.user_id=@user_id AND t.archived=@false AND t.active_turn_id=@request_id AND a.request_id=@request_id AND a.state=@running);";
                BindProposal(command, proposal);
                Add(command, "@false", _Provider == DatabaseTypeEnum.Sqlite ? 0 : false);
                Add(command, "@running", "Running");
                try { return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1 ? proposal : null; }
                catch (DbException exception) when (IsUniqueViolation(exception))
                {
                    AskActionProposal? raced = await FindDuplicateAsync(connection, proposal, token).ConfigureAwait(false);
                    if (raced != null) return raced;
                    throw;
                }
            }
        }

        private async Task<AskActionProposal?> FindDuplicateAsync(DbConnection connection, AskActionProposal proposal, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT " + _Columns + " FROM ask_action_proposals WHERE tenant_id=@tenant_id AND user_id=@user_id AND thread_id=@thread_id AND request_id=@request_id AND fingerprint=@fingerprint;";
                Add(command, "@tenant_id", proposal.TenantId); Add(command, "@user_id", proposal.UserId); Add(command, "@thread_id", proposal.ThreadId); Add(command, "@request_id", proposal.RequestId); Add(command, "@fingerprint", proposal.Fingerprint);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) return await reader.ReadAsync(token).ConfigureAwait(false) ? Map(reader) : null;
            }
        }

    }
}
