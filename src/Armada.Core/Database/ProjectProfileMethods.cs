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
    /// Project profile persistence, one implementation for every provider.
    /// </summary>
    internal sealed class ProjectProfileMethods : IProjectProfileMethods
    {
        #region Internal-Members

        /// <summary>
        /// The project_profiles table.
        /// </summary>
        internal static readonly StoredTable<ProjectProfile> Table = new StoredTable<ProjectProfile>(
            "project_profiles",
            new[]
            {
                "id", "tenant_id", "user_id", "name", "description", "scope", "fleet_id", "vessel_id", "is_default", "active", "default_pipeline_id",
                "workflow_profile_id", "persona_overrides_json", "skills_json", "authorization_policy", "created_utc", "last_update_utc"
            },
            new[] { "created_utc" },
            ProjectProfileColumns.Read,
            ProjectProfileColumns.Write);

        /// <summary>
        /// Defaults first, then most recently updated, then by name.
        /// </summary>
        internal const string Order = "is_default DESC, last_update_utc DESC, name ASC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<ProjectProfile> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal ProjectProfileMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<ProjectProfile>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<ProjectProfile> CreateAsync(ProjectProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(profile, token).ConfigureAwait(false);
            return profile;
        }

        /// <inheritdoc />
        public Task<ProjectProfile?> ReadAsync(string id, ProjectProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token);
        }

        /// <inheritdoc />
        public async Task<ProjectProfile> UpdateAsync(ProjectProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(profile, token).ConfigureAwait(false);
            return profile;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, ProjectProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<ProjectProfile>> EnumerateAsync(ProjectProfileQuery query, CancellationToken token = default)
        {
            query ??= new ProjectProfileQuery();
            return _Rows.PageAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, query.PageNumber, query.PageSize, token);
        }

        /// <inheritdoc />
        public Task<List<ProjectProfile>> EnumerateAllAsync(ProjectProfileQuery query, CancellationToken token = default)
        {
            return _Rows.ListAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, scope, search, flag and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, ProjectProfileQuery? query, DatabaseTypeEnum provider)
        {
            if (query == null) return filter;
            filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Name("scope", query.Scope)
                .Text("fleet_id", query.FleetId)
                .Text("vessel_id", query.VesselId);
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                // PostgreSQL matches case-insensitively with ILIKE; the other providers lower both sides.
                if (provider == DatabaseTypeEnum.Postgresql)
                {
                    string pattern = "%" + query.Search + "%";
                    filter.Condition("(name ILIKE @search OR COALESCE(description, '') ILIKE @search)", p => p.Text("@search", "name", pattern));
                }
                else
                {
                    string search = "%" + query.Search.ToLowerInvariant() + "%";
                    filter.Condition("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search)", p => p.Text("@search", "name", search));
                }
            }
            // PostgreSQL stores this creation time as text and compares it as a zoned timestamp.
            string? created = provider == DatabaseTypeEnum.Postgresql ? "created_utc::timestamptz" : null;
            return filter
                .Bool("active", "@active", query.Active)
                .Time("created_utc", ">=", "@from_utc", query.FromUtc, created)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc, created);
        }

        #endregion
    }
}

