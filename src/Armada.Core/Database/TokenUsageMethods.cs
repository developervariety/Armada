namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Token usage persistence, one implementation for every provider. Records are written once and never updated.
    /// </summary>
    internal sealed class TokenUsageMethods : ITokenUsageMethods
    {
        #region Internal-Members

        /// <summary>
        /// The token_usage table.
        /// </summary>
        internal static readonly StoredTable<TokenUsageRecord> Table = new StoredTable<TokenUsageRecord>(
            "token_usage",
            new[]
            {
                "id", "tenant_id", "user_id", "model", "runtime", "source", "source_id", "vessel_id", "captain_id",
                "input_tokens", "output_tokens", "cached_tokens", "total_tokens", "estimated", "created_utc",
                "uncached_input_tokens", "cache_read_input_tokens", "cache_write_input_tokens", "usage_rule"
            },
            Array.Empty<string>(),
            TokenUsageColumns.Read,
            TokenUsageColumns.Write);

        /// <summary>
        /// The events table, read for the token-usage events the summary folds in.
        /// </summary>
        internal static readonly StoredTable<ArmadaEvent> EventTable = new StoredTable<ArmadaEvent>(
            "events",
            new[]
            {
                "id", "tenant_id", "user_id", "event_type", "entity_type", "entity_id", "captain_id", "mission_id",
                "vessel_id", "voyage_id", "message", "payload", "created_utc"
            },
            Array.Empty<string>(),
            EventColumns.Read,
            EventColumns.Write);

        /// <summary>
        /// Event type of the token usage a mission reports on the event stream.
        /// </summary>
        internal const string MissionTokenUsageEventType = "mission.token_usage";

        /// <summary>
        /// Most source identifiers one statement binds. Every provider accepts this many parameters in one command,
        /// with room for the scope conditions.
        /// </summary>
        internal const int SourceIdBatchSize = 400;

        /// <summary>
        /// Newest first.
        /// </summary>
        internal const string Order = "created_utc DESC, id DESC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<TokenUsageRecord> _Rows;
        private readonly StoredMethods<ArmadaEvent> _Events;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal TokenUsageMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<TokenUsageRecord>(dialect, Table);
            _Events = new StoredMethods<ArmadaEvent>(dialect, EventTable);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<TokenUsageRecord> CreateAsync(TokenUsageRecord record, CancellationToken token = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            await _Rows.InsertAsync(record, token).ConfigureAwait(false);
            return record;
        }

        /// <inheritdoc />
        public Task<TokenUsageRecord?> ReadAsync(string id, TokenUsageQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query), token);
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<TokenUsageRecord>> EnumerateAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();
            EnumerationResult<TokenUsageRecord> page = await _Rows.PageAsync(Scope(Table.Filter(), query), Order, query.PageNumber, query.PageSize, token).ConfigureAwait(false);
            return EnumerationResult<TokenUsageRecord>.Create(
                new EnumerationQuery { PageNumber = query.PageNumber, PageSize = page.PageSize },
                page.Objects,
                page.TotalRecords);
        }

        /// <inheritdoc />
        public Task<List<TokenUsageRecord>> EnumerateForSummaryAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();
            return _Rows.ListAsync(Scope(Table.Filter(), query), Order, token);
        }

        /// <inheritdoc />
        public async Task<List<TokenUsageRecord>> EnumerateBySourceIdsAsync(TokenUsageQuery scope, string source, IReadOnlyCollection<string> sourceIds, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(source)) throw new ArgumentNullException(nameof(source));
            if (sourceIds == null) throw new ArgumentNullException(nameof(sourceIds));

            List<string> distinct = sourceIds
                .Where(id => !String.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            List<TokenUsageRecord> records = new List<TokenUsageRecord>();
            for (int start = 0; start < distinct.Count; start += SourceIdBatchSize)
            {
                List<string> batch = distinct.GetRange(start, Math.Min(SourceIdBatchSize, distinct.Count - start));
                List<string> names = new List<string>(batch.Count);
                for (int i = 0; i < batch.Count; i++) names.Add("@source_id_" + i);

                StoredFilter filter = Table.Filter()
                    .Text("tenant_id", scope?.TenantId)
                    .Text("user_id", scope?.UserId)
                    .Key("source", source)
                    .Condition("source_id IN (" + String.Join(", ", names) + ")", p =>
                    {
                        for (int i = 0; i < batch.Count; i++) p.Text(names[i], "source_id", batch[i]);
                    });
                records.AddRange(await _Rows.ListAsync(filter, Order, token).ConfigureAwait(false));
            }

            return records;
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateLegacyEventsAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            StoredFilter filter = EventTable.Filter()
                .Key("event_type", MissionTokenUsageEventType)
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Time("created_utc", ">=", "@from_utc", query.FromUtc)
                .Time("created_utc", "<", "@to_utc", query.ToUtc);
            return _Events.ListAsync(filter, Order, token);
        }

        /// <inheritdoc />
        public Task<int> DeleteByFilterAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();
            return _Rows.DeleteAsync(Scope(Table.Filter(), query), token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, attribution and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, TokenUsageQuery? query)
        {
            if (query == null) return filter;
            return filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Text("model", query.Model)
                .Text("runtime", query.Runtime)
                .Text("source", query.Source)
                .Text("vessel_id", query.VesselId)
                .Text("captain_id", query.CaptainId)
                .Text("source_id", query.SourceId)
                .Time("created_utc", ">=", "@from_utc", query.FromUtc)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc);
        }

        #endregion
    }
}
