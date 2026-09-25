namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Request history persistence, one implementation for every provider. An entry and its optional detail are
    /// written together in one transaction.
    /// </summary>
    internal sealed class RequestHistoryMethods : IRequestHistoryMethods
    {
        #region Internal-Members

        /// <summary>
        /// The request_history table.
        /// </summary>
        internal static readonly StoredTable<RequestHistoryEntry> Entries = new StoredTable<RequestHistoryEntry>(
            "request_history",
            new[]
            {
                "id", "tenant_id", "user_id", "credential_id", "principal_display", "auth_method", "method", "route", "route_template",
                "query_string", "status_code", "duration_ms", "request_size_bytes", "response_size_bytes", "request_content_type",
                "response_content_type", "is_success", "client_ip", "correlation_id", "created_utc"
            },
            Array.Empty<string>(),
            RequestHistoryColumns.ReadEntry,
            RequestHistoryColumns.WriteEntry);

        /// <summary>
        /// The request_history_detail table, keyed by the entry it belongs to.
        /// </summary>
        internal static readonly StoredTable<RequestHistoryDetail> Details = new StoredTable<RequestHistoryDetail>(
            "request_history_detail",
            new[]
            {
                "request_history_id", "path_params_json", "query_params_json", "request_headers_json", "response_headers_json",
                "request_body_text", "response_body_text", "request_body_truncated", "response_body_truncated"
            },
            Array.Empty<string>(),
            RequestHistoryColumns.ReadDetail,
            RequestHistoryColumns.WriteDetail,
            "request_history_id");

        /// <summary>
        /// Newest first.
        /// </summary>
        internal const string Order = "created_utc DESC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<RequestHistoryEntry> _Entries;
        private readonly StoredMethods<RequestHistoryDetail> _Details;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal RequestHistoryMethods(StoredDialect dialect)
        {
            _Entries = new StoredMethods<RequestHistoryEntry>(dialect, Entries);
            _Details = new StoredMethods<RequestHistoryDetail>(dialect, Details);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<RequestHistoryRecord> CreateAsync(RequestHistoryEntry entry, RequestHistoryDetail? detail, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            using (DbConnection connection = await _Entries.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                await _Entries.InsertAsync(entry, connection, transaction, token).ConfigureAwait(false);
                if (detail != null) await _Details.InsertAsync(detail, connection, transaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }

            return new RequestHistoryRecord { Entry = entry, Detail = detail };
        }

        /// <inheritdoc />
        public async Task<RequestHistoryRecord?> ReadAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            RequestHistoryEntry? entry = await _Entries.FirstAsync(Scope(Entries.Filter().Text("id", id), query, _Entries.Dialect.Provider), token).ConfigureAwait(false);
            if (entry == null) return null;
            RequestHistoryDetail? detail = await _Details.FirstAsync(Details.Filter().Text("request_history_id", "@id", id), token).ConfigureAwait(false);
            return new RequestHistoryRecord { Entry = entry, Detail = detail };
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<RequestHistoryEntry>> EnumerateAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();
            EnumerationResult<RequestHistoryEntry> page = await _Entries.PageAsync(
                Scope(Entries.Filter(), query, _Entries.Dialect.Provider), Order, query.PageNumber, query.PageSize, token).ConfigureAwait(false);
            return EnumerationResult<RequestHistoryEntry>.Create(
                new EnumerationQuery { PageNumber = query.PageNumber, PageSize = page.PageSize },
                page.Objects,
                page.TotalRecords);
        }

        /// <inheritdoc />
        public Task<List<RequestHistoryEntry>> EnumerateForSummaryAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();
            return _Entries.ListAsync(Scope(Entries.Filter(), query, _Entries.Dialect.Provider), Order, token);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Entries.DeleteAsync(Scope(Entries.Filter().Text("id", id), query, _Entries.Dialect.Provider), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<int> DeleteByFilterAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();
            return _Entries.DeleteAsync(Scope(Entries.Filter(), query, _Entries.Dialect.Provider), token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, caller, request and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, RequestHistoryQuery? query, DatabaseTypeEnum provider)
        {
            if (query == null) return filter;
            // PostgreSQL matches the caller and route with ILIKE; the other providers use LIKE.
            string like = provider == DatabaseTypeEnum.Postgresql ? " ILIKE " : " LIKE ";

            filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId);
            if (query.ExcludedUserIds != null && query.ExcludedUserIds.Count > 0)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < query.ExcludedUserIds.Count; i++) names.Add("@excluded_user_" + i);
                List<string> excluded = new List<string>(query.ExcludedUserIds);
                filter.Condition("(user_id IS NULL OR user_id NOT IN (" + String.Join(", ", names) + "))", p =>
                {
                    for (int i = 0; i < excluded.Count; i++) p.Text(names[i], "user_id", excluded[i]);
                });
            }
            filter.Text("credential_id", query.CredentialId);
            if (!String.IsNullOrWhiteSpace(query.Principal))
            {
                string principal = "%" + query.Principal + "%";
                filter.Condition("principal_display" + like + "@principal", p => p.Text("@principal", "principal_display", principal));
            }
            if (!String.IsNullOrWhiteSpace(query.Method))
            {
                string method = query.Method.ToUpperInvariant();
                filter.Condition("UPPER(method) = @method", p => p.Text("@method", "method", method));
            }
            if (!String.IsNullOrWhiteSpace(query.Route))
            {
                string route = "%" + query.Route + "%";
                filter.Condition("route" + like + "@route", p => p.Text("@route", "route", route));
            }
            return filter
                .Int("status_code", query.StatusCode)
                .Bool("is_success", "@is_success", query.IsSuccess)
                .Time("created_utc", ">=", "@from_utc", query.FromUtc)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc);
        }

        #endregion
    }
}
