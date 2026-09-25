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
    /// Workflow profile persistence, one implementation for every provider.
    /// </summary>
    internal sealed class WorkflowProfileMethods : IWorkflowProfileMethods
    {
        #region Internal-Members

        /// <summary>
        /// The workflow_profiles table.
        /// </summary>
        internal static readonly StoredTable<WorkflowProfile> Table = new StoredTable<WorkflowProfile>(
            "workflow_profiles",
            new[]
            {
                "id", "tenant_id", "user_id", "name", "description", "scope", "fleet_id", "vessel_id", "is_default", "active", "language_hints_json",
                "lint_command", "build_command", "unit_test_command", "containerless_unit_test_command", "integration_test_command",
                "e2e_test_command", "package_command", "publish_artifact_command", "release_versioning_command", "changelog_generation_command",
                "migration_command", "security_scan_command", "performance_command", "deployment_verification_command",
                "rollback_verification_command", "required_secrets_json", "expected_artifacts_json", "environments_json",
                "environment_variables_json", "created_utc", "last_update_utc"
            },
            new[] { "created_utc" },
            WorkflowProfileColumns.Read,
            WorkflowProfileColumns.Write);

        /// <summary>
        /// Defaults first, then most recently updated, then by name.
        /// </summary>
        internal const string Order = "is_default DESC, last_update_utc DESC, name ASC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<WorkflowProfile> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal WorkflowProfileMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<WorkflowProfile>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<WorkflowProfile> CreateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(profile, token).ConfigureAwait(false);
            return profile;
        }

        /// <inheritdoc />
        public Task<WorkflowProfile?> ReadAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token);
        }

        /// <inheritdoc />
        public async Task<WorkflowProfile> UpdateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            profile.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(profile, token).ConfigureAwait(false);
            return profile;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, WorkflowProfileQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<WorkflowProfile>> EnumerateAsync(WorkflowProfileQuery query, CancellationToken token = default)
        {
            query ??= new WorkflowProfileQuery();
            return _Rows.PageAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, query.PageNumber, query.PageSize, token);
        }

        /// <inheritdoc />
        public Task<List<WorkflowProfile>> EnumerateAllAsync(WorkflowProfileQuery query, CancellationToken token = default)
        {
            return _Rows.ListAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, scope, search, flag and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, WorkflowProfileQuery? query, DatabaseTypeEnum provider)
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
            return filter
                .Bool("active", "@active", query.Active)
                .Time("created_utc", ">=", "@from_utc", query.FromUtc)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc);
        }

        #endregion
    }
}

