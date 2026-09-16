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

    /// <summary>
    /// Provider-neutral implementation of the memory proposal store. Only the migration DDL differs per
    /// provider. The store has no delete and no text update: a proposal is created once and can only
    /// move from Open to Dismissed.
    /// </summary>
    public sealed class MemoryProposalMethods : IMemoryProposalMethods
    {
        #region Private-Members

        private const string _Columns = "id, tenant_id, user_id, source, source_key, title, body, target_hint, confidence, related_record_ids_json, state, dismissed_by, dismissed_reason, dismissed_utc, created_utc, last_update_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public MemoryProposalMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<MemoryProposal> CreateAsync(MemoryProposal proposal, CancellationToken token = default)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            if (String.IsNullOrWhiteSpace(proposal.SourceKey)) throw new ArgumentException("Source key is required.", nameof(proposal));
            proposal.Source = ProductionFactSql.BoundCode(proposal.Source, MemoryProposal.MaximumSourceLength) ?? MemoryProposal.SourcePapercutSweep;
            proposal.Title = Bound(proposal.Title, MemoryProposal.MaximumTitleLength) ?? String.Empty;
            proposal.Body = Bound(proposal.Body, MemoryProposal.MaximumBodyLength) ?? String.Empty;
            proposal.TargetHint = Bound(proposal.TargetHint, MemoryProposal.MaximumTargetHintLength) ?? String.Empty;

            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO memory_proposals (" + _Columns + ") VALUES (@id, @tenant_id, @user_id, @source, @source_key, @title, @body, @target_hint, @confidence, @related_record_ids_json, @state, @dismissed_by, @dismissed_reason, @dismissed_utc, @created_utc, @last_update_utc);";
                    ProductionFactSql.Add(command, "@id", proposal.Id);
                    ProductionFactSql.Add(command, "@tenant_id", proposal.TenantId);
                    ProductionFactSql.Add(command, "@user_id", proposal.UserId);
                    ProductionFactSql.Add(command, "@source", proposal.Source);
                    ProductionFactSql.Add(command, "@source_key", proposal.SourceKey);
                    ProductionFactSql.Add(command, "@title", proposal.Title);
                    ProductionFactSql.Add(command, "@body", proposal.Body);
                    ProductionFactSql.Add(command, "@target_hint", proposal.TargetHint);
                    ProductionFactSql.Add(command, "@confidence", proposal.Confidence);
                    ProductionFactSql.Add(command, "@related_record_ids_json", JsonSerializer.Serialize(proposal.RelatedRecordIds));
                    ProductionFactSql.Add(command, "@state", proposal.State.ToString());
                    ProductionFactSql.Add(command, "@dismissed_by", proposal.DismissedBy);
                    ProductionFactSql.Add(command, "@dismissed_reason", proposal.DismissedReason);
                    ProductionFactSql.AddUtc(command, "@dismissed_utc", proposal.DismissedUtc, _Provider);
                    ProductionFactSql.AddUtc(command, "@created_utc", proposal.CreatedUtc, _Provider);
                    ProductionFactSql.AddUtc(command, "@last_update_utc", proposal.LastUpdateUtc, _Provider);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return proposal;
        }

        /// <inheritdoc />
        public async Task<MemoryProposal?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) return null;
            List<MemoryProposal> rows = await SelectAsync("id = @id", command => ProductionFactSql.Add(command, "@id", id.Trim()), 1, token).ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }

        /// <inheritdoc />
        public async Task<MemoryProposal?> ReadBySourceKeyAsync(string sourceKey, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(sourceKey)) return null;
            List<MemoryProposal> rows = await SelectAsync("source_key = @source_key", command => ProductionFactSql.Add(command, "@source_key", sourceKey), 1, token).ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }

        /// <inheritdoc />
        public Task<List<MemoryProposal>> EnumerateAsync(string? tenantId, MemoryProposalStateEnum? state, int limit, CancellationToken token = default)
        {
            List<string> clauses = new List<string>();
            if (tenantId != null) clauses.Add("tenant_id = @tenant_id");
            if (state.HasValue) clauses.Add("state = @state");
            string where = clauses.Count == 0 ? String.Empty : String.Join(" AND ", clauses);
            return SelectAsync(where, command =>
            {
                if (tenantId != null) ProductionFactSql.Add(command, "@tenant_id", tenantId);
                if (state.HasValue) ProductionFactSql.Add(command, "@state", state.Value.ToString());
            }, Math.Max(1, Math.Min(1000, limit)), token);
        }

        /// <inheritdoc />
        public async Task<bool> DismissAsync(string id, string dismissedBy, string reason, DateTime dismissedUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (String.IsNullOrWhiteSpace(dismissedBy)) throw new ArgumentNullException(nameof(dismissedBy));
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));

            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE memory_proposals SET state = @dismissed, dismissed_by = @dismissed_by, dismissed_reason = @dismissed_reason, dismissed_utc = @dismissed_utc, last_update_utc = @last_update_utc WHERE id = @id AND state = @open;";
                    ProductionFactSql.Add(command, "@dismissed", MemoryProposalStateEnum.Dismissed.ToString());
                    ProductionFactSql.Add(command, "@dismissed_by", Bound(dismissedBy, MemoryProposal.MaximumReasonLength));
                    ProductionFactSql.Add(command, "@dismissed_reason", Bound(reason, MemoryProposal.MaximumReasonLength));
                    ProductionFactSql.AddUtc(command, "@dismissed_utc", dismissedUtc, _Provider);
                    ProductionFactSql.AddUtc(command, "@last_update_utc", dismissedUtc, _Provider);
                    ProductionFactSql.Add(command, "@id", id.Trim());
                    ProductionFactSql.Add(command, "@open", MemoryProposalStateEnum.Open.ToString());
                    int changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return changed == 1;
                }
            }
        }

        #endregion

        #region Private-Methods

        private async Task<List<MemoryProposal>> SelectAsync(string where, Action<DbCommand> bind, int limit, CancellationToken token)
        {
            List<MemoryProposal> rows = new List<MemoryProposal>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    string filter = String.IsNullOrEmpty(where) ? String.Empty : " WHERE " + where;
                    string order = " ORDER BY created_utc DESC, id DESC";
                    command.CommandText = _Provider == DatabaseTypeEnum.SqlServer
                        ? "SELECT TOP (@row_limit) " + _Columns + " FROM memory_proposals" + filter + order + ";"
                        : "SELECT " + _Columns + " FROM memory_proposals" + filter + order + " LIMIT @row_limit;";
                    bind(command);
                    ProductionFactSql.Add(command, "@row_limit", limit);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            rows.Add(FromReader(reader));
                    }
                }
            }
            return rows;
        }

        private static MemoryProposal FromReader(DbDataReader reader)
        {
            string state = Convert.ToString(reader["state"], CultureInfo.InvariantCulture)!;
            if (!Enum.TryParse(state, false, out MemoryProposalStateEnum parsedState))
                throw new InvalidOperationException("Stored memory proposal has an unknown state: " + state);

            List<string> related = new List<string>();
            string? relatedJson = ProductionFactSql.ReadText(reader["related_record_ids_json"]);
            if (!String.IsNullOrWhiteSpace(relatedJson))
                related = JsonSerializer.Deserialize<List<string>>(relatedJson) ?? new List<string>();

            return new MemoryProposal
            {
                Id = Convert.ToString(reader["id"], CultureInfo.InvariantCulture)!,
                TenantId = ProductionFactSql.ReadText(reader["tenant_id"]),
                UserId = ProductionFactSql.ReadText(reader["user_id"]),
                Source = Convert.ToString(reader["source"], CultureInfo.InvariantCulture)!,
                SourceKey = Convert.ToString(reader["source_key"], CultureInfo.InvariantCulture)!,
                Title = ProductionFactSql.ReadText(reader["title"]) ?? String.Empty,
                Body = ProductionFactSql.ReadText(reader["body"]) ?? String.Empty,
                TargetHint = ProductionFactSql.ReadText(reader["target_hint"]) ?? String.Empty,
                Confidence = Convert.ToDouble(reader["confidence"], CultureInfo.InvariantCulture),
                RelatedRecordIds = related,
                State = parsedState,
                DismissedBy = ProductionFactSql.ReadText(reader["dismissed_by"]),
                DismissedReason = ProductionFactSql.ReadText(reader["dismissed_reason"]),
                DismissedUtc = ProductionFactSql.ReadNullableUtc(reader["dismissed_utc"]),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"]),
                LastUpdateUtc = ProductionFactSql.ReadUtc(reader["last_update_utc"])
            };
        }

        private static string? Bound(string? value, int maximumLength)
        {
            if (value == null) return null;
            string trimmed = value.Trim();
            return trimmed.Length > maximumLength ? trimmed.Substring(0, maximumLength) : trimmed;
        }

        #endregion
    }
}
