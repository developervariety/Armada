namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Release persistence, one implementation for every provider.
    /// </summary>
    internal sealed class ReleaseMethods : IReleaseMethods
    {
        #region Internal-Members

        /// <summary>
        /// The releases table.
        /// </summary>
        internal static readonly StoredTable<Release> Table = new StoredTable<Release>(
            "releases",
            new[]
            {
                "id", "tenant_id", "user_id", "vessel_id", "workflow_profile_id", "title", "version", "tag_name", "summary", "notes", "status",
                "voyage_ids_json", "mission_ids_json", "check_run_ids_json", "artifacts_json", "created_utc", "last_update_utc", "published_utc"
            },
            new[] { "created_utc" },
            ReleaseColumns.Read,
            ReleaseColumns.Write);

        /// <summary>
        /// Most recently published or updated first, newest first among equals.
        /// </summary>
        internal const string Order = "COALESCE(published_utc, last_update_utc) DESC, created_utc DESC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<Release> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal ReleaseMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<Release>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Release> CreateAsync(Release release, CancellationToken token = default)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));
            release.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(release, token).ConfigureAwait(false);
            return release;
        }

        /// <inheritdoc />
        public Task<Release?> ReadAsync(string id, ReleaseQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token);
        }

        /// <inheritdoc />
        public async Task<Release> UpdateAsync(Release release, CancellationToken token = default)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));
            release.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(release, token).ConfigureAwait(false);
            return release;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, ReleaseQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Release>> EnumerateAsync(ReleaseQuery query, CancellationToken token = default)
        {
            query ??= new ReleaseQuery();
            return _Rows.PageAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, query.PageNumber, query.PageSize, token);
        }

        /// <inheritdoc />
        public Task<List<Release>> EnumerateAllAsync(ReleaseQuery query, CancellationToken token = default)
        {
            return _Rows.ListAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, scope, search and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, ReleaseQuery? query, DatabaseTypeEnum provider)
        {
            if (query == null) return filter;
            filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Text("vessel_id", query.VesselId)
                .Text("workflow_profile_id", query.WorkflowProfileId);
            if (!String.IsNullOrWhiteSpace(query.VoyageId))
            {
                string voyageLike = "%\"" + query.VoyageId + "\"%";
                filter.Condition("voyage_ids_json LIKE @voyage_like", p => p.Text("@voyage_like", "voyage_ids_json", voyageLike));
            }
            if (!String.IsNullOrWhiteSpace(query.MissionId))
            {
                string missionLike = "%\"" + query.MissionId + "\"%";
                filter.Condition("mission_ids_json LIKE @mission_like", p => p.Text("@mission_like", "mission_ids_json", missionLike));
            }
            if (!String.IsNullOrWhiteSpace(query.CheckRunId))
            {
                string checkRunLike = "%\"" + query.CheckRunId + "\"%";
                filter.Condition("check_run_ids_json LIKE @check_run_like", p => p.Text("@check_run_like", "check_run_ids_json", checkRunLike));
            }
            filter.Name("status", query.Status);
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                string search = "%" + query.Search.ToLowerInvariant() + "%";
                filter.Condition("(LOWER(title) LIKE @search OR LOWER(COALESCE(version, '')) LIKE @search OR LOWER(COALESCE(tag_name, '')) LIKE @search"
                    + " OR LOWER(COALESCE(summary, '')) LIKE @search OR LOWER(COALESCE(notes, '')) LIKE @search)", p => p.Text("@search", "title", search));
            }
            return filter
                .Time("created_utc", ">=", "@from_utc", query.FromUtc)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc);
        }

        #endregion
    }
}

