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
    /// Deployment environment persistence, one implementation for every provider.
    /// </summary>
    internal sealed class DeploymentEnvironmentMethods : IDeploymentEnvironmentMethods
    {
        #region Internal-Members

        /// <summary>
        /// The environments table.
        /// </summary>
        internal static readonly StoredTable<DeploymentEnvironment> Table = new StoredTable<DeploymentEnvironment>(
            "environments",
            new[]
            {
                "id", "tenant_id", "user_id", "vessel_id", "name", "description", "kind", "configuration_source", "base_url", "health_endpoint",
                "access_notes", "deployment_rules", "verification_definitions_json", "rollout_monitoring_window_minutes",
                "rollout_monitoring_interval_seconds", "alert_on_regression", "requires_approval", "is_default", "active", "created_utc",
                "last_update_utc"
            },
            new[] { "created_utc" },
            DeploymentEnvironmentColumns.Read,
            DeploymentEnvironmentColumns.Write);

        /// <summary>
        /// Defaults first, then active environments, then by name, newest first among equals.
        /// </summary>
        internal const string Order = "is_default DESC, active DESC, name ASC, created_utc DESC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<DeploymentEnvironment> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal DeploymentEnvironmentMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<DeploymentEnvironment>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<DeploymentEnvironment> CreateAsync(DeploymentEnvironment environment, CancellationToken token = default)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            environment.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(environment, token).ConfigureAwait(false);
            return environment;
        }

        /// <inheritdoc />
        public Task<DeploymentEnvironment?> ReadAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token);
        }

        /// <inheritdoc />
        public async Task<DeploymentEnvironment> UpdateAsync(DeploymentEnvironment environment, CancellationToken token = default)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            environment.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(environment, token).ConfigureAwait(false);
            return environment;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, DeploymentEnvironmentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<DeploymentEnvironment>> EnumerateAsync(DeploymentEnvironmentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentEnvironmentQuery();
            return _Rows.PageAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, query.PageNumber, query.PageSize, token);
        }

        /// <inheritdoc />
        public Task<List<DeploymentEnvironment>> EnumerateAllAsync(DeploymentEnvironmentQuery query, CancellationToken token = default)
        {
            return _Rows.ListAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, scope, flag and search conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, DeploymentEnvironmentQuery? query, DatabaseTypeEnum provider)
        {
            if (query == null) return filter;
            filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Text("vessel_id", query.VesselId)
                .Name("kind", query.Kind)
                .Bool("is_default", "@is_default_filter", query.IsDefault)
                .Bool("active", "@active_filter", query.Active);
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                string search = "%" + query.Search.ToLowerInvariant() + "%";
                filter.Condition("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search OR LOWER(COALESCE(configuration_source, '')) LIKE @search"
                    + " OR LOWER(COALESCE(base_url, '')) LIKE @search OR LOWER(COALESCE(health_endpoint, '')) LIKE @search)", p => p.Text("@search", "name", search));
            }
            return filter;
        }

        #endregion
    }
}

