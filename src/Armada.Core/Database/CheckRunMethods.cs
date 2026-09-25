namespace Armada.Core.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Structured check-run persistence, one implementation for every provider.
    /// </summary>
    internal sealed class CheckRunMethods : ICheckRunMethods
    {
        #region Internal-Members

        /// <summary>
        /// The check_runs table.
        /// </summary>
        internal static readonly StoredTable<CheckRun> Table = new StoredTable<CheckRun>(
            "check_runs",
            new[]
            {
                "id", "tenant_id", "user_id", "workflow_profile_id", "vessel_id", "mission_id", "voyage_id", "deployment_id", "label", "check_type", "status",
                "source", "provider_name", "external_id", "external_url", "environment_name", "command", "working_directory", "branch_name", "commit_hash", "exit_code", "output", "summary",
                "test_summary_json", "coverage_summary_json", "artifacts_json", "duration_ms", "started_utc", "completed_utc", "created_utc", "last_update_utc",
                "regression_purpose", "regression_objective_id", "regression_landed_commit", "slot_requested_utc"
            },
            new[] { "created_utc" },
            CheckRunColumns.Read,
            CheckRunColumns.Write);

        /// <summary>
        /// Newest first.
        /// </summary>
        internal const string Order = "created_utc DESC, id DESC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<CheckRun> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal CheckRunMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<CheckRun>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<CheckRun> CreateAsync(CheckRun checkRun, CancellationToken token = default)
        {
            if (checkRun == null) throw new ArgumentNullException(nameof(checkRun));
            checkRun.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.InsertAsync(checkRun, token).ConfigureAwait(false);
            return checkRun;
        }

        /// <inheritdoc />
        public Task<CheckRun?> ReadAsync(string id, CheckRunQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _Rows.FirstAsync(Scope(Table.Filter().Text("id", id), query), token);
        }

        /// <inheritdoc />
        public async Task<CheckRun> UpdateAsync(CheckRun checkRun, CancellationToken token = default)
        {
            if (checkRun == null) throw new ArgumentNullException(nameof(checkRun));
            checkRun.LastUpdateUtc = DateTime.UtcNow;
            await _Rows.UpdateAsync(checkRun, token).ConfigureAwait(false);
            return checkRun;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CheckRunQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await _Rows.DeleteAsync(Scope(Table.Filter().Text("id", id), query), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<CheckRun>> EnumerateAsync(CheckRunQuery query, CancellationToken token = default)
        {
            query ??= new CheckRunQuery();
            return _Rows.PageAsync(Scope(Table.Filter(), query), Order, query.PageNumber, query.PageSize, token);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, user, scope and creation-time conditions of a query.
        /// </summary>
        internal static StoredFilter Scope(StoredFilter filter, CheckRunQuery? query)
        {
            if (query == null) return filter;
            return filter
                .Text("tenant_id", query.TenantId)
                .Text("user_id", query.UserId)
                .Text("workflow_profile_id", query.WorkflowProfileId)
                .Text("vessel_id", query.VesselId)
                .Text("mission_id", query.MissionId)
                .Text("voyage_id", query.VoyageId)
                .Text("deployment_id", query.DeploymentId)
                .Name("check_type", query.Type)
                .Name("status", query.Status)
                .Name("source", query.Source)
                .Text("provider_name", query.ProviderName)
                .Text("external_id", query.ExternalId)
                .Text("environment_name", query.EnvironmentName)
                .Time("created_utc", ">=", "@from_utc", query.FromUtc)
                .Time("created_utc", "<=", "@to_utc", query.ToUtc);
        }

        #endregion
    }
}
