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
        public Task<AskActionProposal?> ReadAsync(string tenantId, string userId, string threadId, string proposalId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            return SelectOneAsync("tenant_id=@tenant_id AND user_id=@user_id AND thread_id=@thread_id AND id=@id", command => { Add(command, "@tenant_id", tenantId); Add(command, "@user_id", userId); Add(command, "@thread_id", threadId); Add(command, "@id", proposalId); }, token);
        }

        /// <inheritdoc />
        public Task<List<AskActionProposal>> EnumerateAsync(string tenantId, string userId, string threadId, int limit = 100, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            return SelectAsync(tenantId, userId, threadId, Math.Max(1, Math.Min(500, limit)), token);
        }

        /// <inheritdoc />
        public async Task<List<AskActionProposal>> EnumeratePendingForOwnerAsync(string tenantId, string userId, DateTime utcNow, int limit = 100, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            int bounded = Math.Max(1, Math.Min(500, limit));
            List<AskActionProposal> result = new List<AskActionProposal>();
            using (DbConnection connection = _ConnectionFactory())
            using (DbCommand command = connection.CreateCommand())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                string where = "tenant_id=@tenant_id AND user_id=@user_id AND state=@state AND expires_utc>@now";
                string order = " ORDER BY created_utc ASC, id ASC";
                command.CommandText = _Provider == DatabaseTypeEnum.SqlServer
                    ? "SELECT TOP (@limit) " + _Columns + " FROM ask_action_proposals WHERE " + where + order + ";"
                    : "SELECT " + _Columns + " FROM ask_action_proposals WHERE " + where + order + " LIMIT @limit;";
                Add(command, "@tenant_id", tenantId); Add(command, "@user_id", userId); Add(command, "@state", AskProposalStateEnum.Pending.ToString()); AddUtc(command, "@now", utcNow); Add(command, "@limit", bounded);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Map(reader));
            }
            return result;
        }

        private async Task<AskActionProposal?> SelectOneAsync(string where, Action<DbCommand> bind, CancellationToken token)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _Columns + " FROM ask_action_proposals WHERE " + where + ";"; bind(command);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) return await reader.ReadAsync(token).ConfigureAwait(false) ? Map(reader) : null;
                }
            }
        }

        private async Task<List<AskActionProposal>> SelectAsync(string tenantId, string userId, string threadId, int limit, CancellationToken token)
        {
            List<AskActionProposal> result = new List<AskActionProposal>();
            using (DbConnection connection = _ConnectionFactory())
            using (DbCommand command = connection.CreateCommand())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                command.CommandText = _Provider == DatabaseTypeEnum.SqlServer ? "SELECT TOP (@limit) " + _Columns + " FROM ask_action_proposals WHERE tenant_id=@tenant_id AND user_id=@user_id AND thread_id=@thread_id ORDER BY created_utc DESC, id DESC;" : "SELECT " + _Columns + " FROM ask_action_proposals WHERE tenant_id=@tenant_id AND user_id=@user_id AND thread_id=@thread_id ORDER BY created_utc DESC, id DESC LIMIT @limit;";
                Add(command, "@tenant_id", tenantId); Add(command, "@user_id", userId); Add(command, "@thread_id", threadId); Add(command, "@limit", limit);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Map(reader));
            }
            return result;
        }

        private static AskActionProposal Map(DbDataReader r)
        {
            return new AskActionProposal { Id = Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!, TenantId = Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)!, UserId = Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture)!, ThreadId = Convert.ToString(r.GetValue(3), CultureInfo.InvariantCulture)!, RequestId = Convert.ToString(r.GetValue(4), CultureInfo.InvariantCulture)!, ToolName = Convert.ToString(r.GetValue(5), CultureInfo.InvariantCulture)!, ArgumentsJson = Convert.ToString(r.GetValue(6), CultureInfo.InvariantCulture)!, Fingerprint = Convert.ToString(r.GetValue(7), CultureInfo.InvariantCulture)!, State = Enum.Parse<AskProposalStateEnum>(Convert.ToString(r.GetValue(8), CultureInfo.InvariantCulture)!), CreatedUtc = ProductionFactSql.ReadUtc(r.GetValue(9)), ExpiresUtc = ProductionFactSql.ReadUtc(r.GetValue(10)), DecidedUtc = ProductionFactSql.ReadNullableUtc(r.GetValue(11)), CompletedUtc = ProductionFactSql.ReadNullableUtc(r.GetValue(12)), Outcome = ProductionFactSql.ReadText(r.GetValue(13)), WorkReferences = JsonSerializer.Deserialize<List<AskWorkReference>>(Convert.ToString(r.GetValue(14), CultureInfo.InvariantCulture)!) ?? new List<AskWorkReference>() };
        }
    }
}
