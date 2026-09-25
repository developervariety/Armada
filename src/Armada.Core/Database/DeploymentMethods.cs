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
    /// Deployment persistence, one implementation for every provider.
    /// </summary>
    internal sealed class DeploymentMethods : IDeploymentMethods
    {
        #region Internal-Members

        /// <summary>
        /// The deployments table.
        /// </summary>
        internal static readonly StoredTable<Deployment> Table = new StoredTable<Deployment>(
            "deployments",
            new[]
            {
                "id", "tenant_id", "user_id", "vessel_id", "workflow_profile_id", "environment_id", "environment_name", "release_id", "mission_id",
                "voyage_id", "title", "source_ref", "summary", "notes", "status", "verification_status", "approval_required", "approved_by_user_id",
                "approved_utc", "approval_comment", "deploy_check_run_id", "smoke_test_check_run_id", "health_check_run_id",
                "deployment_verification_check_run_id", "rollback_check_run_id", "rollback_verification_check_run_id", "check_run_ids_json",
                "request_history_summary_json", "created_utc", "started_utc", "completed_utc", "verified_utc", "rolled_back_utc",
                "monitoring_window_ends_utc", "last_monitored_utc", "last_regression_alert_utc", "latest_monitoring_summary",
                "monitoring_failure_count", "last_update_utc"
            },
            new[] { "created_utc" },
            DeploymentColumns.Read,
            DeploymentColumns.Write);

        /// <summary>
        /// Most recently active first, newest first among equals.
        /// </summary>
        internal const string Order = "COALESCE(completed_utc, started_utc, last_update_utc) DESC, created_utc DESC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<Deployment> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal DeploymentMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<Deployment>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Deployment> CreateAsync(Deployment deployment, CancellationToken token = default)
        {
            if (deployment == null) throw new ArgumentNullException(nameof(deployment));
            deployment.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(deployment, token).ConfigureAwait(false);
            return deployment;
        }

        /// <inheritdoc />
        public Task<Deployment?> ReadAsync(string id, DeploymentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token);
        }

        /// <inheritdoc />
        public async Task<Deployment> UpdateAsync(Deployment deployment, CancellationToken token = default)
        {
            if (deployment == null) throw new ArgumentNullException(nameof(deployment));
            deployment.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(deployment, token).ConfigureAwait(false);
            return deployment;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, DeploymentQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Scope(Table.Filter().Text("id", id), query, _Rows.Dialect.Provider), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Deployment>> EnumerateAsync(DeploymentQuery query, CancellationToken token = default)
        {
            query ??= new DeploymentQuery();
            return _Rows.PageAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, query.PageNumber, query.PageSize, token);
        }

        /// <inheritdoc />
        public Task<List<Deployment>> EnumerateAllAsync(DeploymentQuery query, CancellationToken token = default)
        {
            return _Rows.ListAsync(Scope(Table.Filter(), query, _Rows.Dialect.Provider), Order, token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, scope, search and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, DeploymentQuery? query, DatabaseTypeEnum provider)
        {
            if (query == null) return filter;
            filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Text("vessel_id", query.VesselId)
                .Text("workflow_profile_id", query.WorkflowProfileId)
                .Text("environment_id", query.EnvironmentId)
                .Text("environment_name", query.EnvironmentName)
                .Text("release_id", query.ReleaseId)
                .Text("mission_id", query.MissionId)
                .Text("voyage_id", query.VoyageId);
            if (!String.IsNullOrWhiteSpace(query.CheckRunId))
            {
                string checkRunLike = "%\"" + query.CheckRunId + "\"%";
                filter.Condition("check_run_ids_json LIKE @check_run_like", p => p.Text("@check_run_like", "check_run_ids_json", checkRunLike));
            }
            filter
                .Name("status", query.Status)
                .Name("verification_status", query.VerificationStatus);
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                string search = "%" + query.Search.ToLowerInvariant() + "%";
                filter.Condition("(LOWER(title) LIKE @search OR LOWER(COALESCE(source_ref, '')) LIKE @search OR LOWER(COALESCE(summary, '')) LIKE @search"
                    + " OR LOWER(COALESCE(notes, '')) LIKE @search OR LOWER(COALESCE(environment_name, '')) LIKE @search)", p => p.Text("@search", "title", search));
            }
            return filter
                .Time("created_utc", ">=", "@from_utc", query.FromUtc)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc);
        }

        #endregion
    }
}

