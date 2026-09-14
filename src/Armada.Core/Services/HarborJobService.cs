namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>
    /// Operator view of Harbor jobs: list, inspect and stop. The REST routes and the MCP tools both call this
    /// service, and it applies the shared runner authorization rule to every job. A job the caller may not see
    /// reads as unknown.
    /// </summary>
    public sealed class HarborJobService
    {
        #region Private-Members

        private readonly HarborJobCoordinator _Coordinator;
        private readonly IHarborJobMethods _Store;
        private readonly IHarborRunnerAuthority _Authority;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="coordinator">Job coordinator that holds live jobs.</param>
        /// <param name="store">Durable job records.</param>
        /// <param name="authority">Shared runner authority rule.</param>
        public HarborJobService(HarborJobCoordinator coordinator, IHarborJobMethods store, IHarborRunnerAuthority authority)
        {
            _Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _Store = store ?? throw new ArgumentNullException(nameof(store));
            _Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        }

        #endregion

        #region Public-Methods

        /// <summary>List jobs the caller may see, newest first.</summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="runnerId">Optional runner filter.</param>
        /// <param name="activeOnly">Only jobs that are not terminal.</param>
        /// <param name="limit">Maximum jobs read, 1 to 1000.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Visible jobs.</returns>
        public async Task<List<HarborJobRecord>> ListAsync(AuthContext caller, string? runnerId, bool activeOnly, int limit, CancellationToken token = default)
        {
            List<HarborJobRecord> visible = new List<HarborJobRecord>();
            if (caller == null || !caller.IsAuthenticated || String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId))
                return visible;

            HarborJobQuery query = new HarborJobQuery
            {
                RunnerId = String.IsNullOrWhiteSpace(runnerId) ? null : runnerId.Trim(),
                ActiveOnly = activeOnly,
                Limit = Math.Clamp(limit, 1, 1000)
            };
            if (!caller.IsAdmin) query.TenantId = caller.TenantId;
            if (!caller.IsAdmin && !caller.IsTenantAdmin) query.UserId = caller.UserId;

            foreach (HarborJobRecord stored in await _Store.EnumerateAsync(query, token).ConfigureAwait(false))
            {
                HarborJobRecord current = Current(stored);
                if (await CanSeeAsync(caller, current, token).ConfigureAwait(false)) visible.Add(current);
            }
            return visible;
        }

        /// <summary>Read one job the caller may see.</summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The job, or null when it is unknown or not visible to the caller.</returns>
        public async Task<HarborJobRecord?> GetAsync(AuthContext caller, string jobId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(jobId)) return null;
            HarborJobRecord? record = _Coordinator.TryGetRecord(jobId.Trim(), out HarborJobRecord? live) && live != null
                ? live
                : await _Store.ReadAsync(jobId.Trim(), token).ConfigureAwait(false);
            if (record == null) return null;
            return await CanSeeAsync(caller, record, token).ConfigureAwait(false) ? record : null;
        }

        /// <summary>Stop a job the caller may see.</summary>
        /// <param name="caller">Verified caller.</param>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Stop result with a stable reason.</returns>
        public async Task<HarborCommandResult> StopAsync(AuthContext caller, string jobId, CancellationToken token = default)
        {
            HarborJobRecord? record = await GetAsync(caller, jobId, token).ConfigureAwait(false);
            if (record == null) return HarborCommandResult.Reject("harbor_job_unknown");
            if (HarborJobRecord.IsTerminal(record.State)) return HarborCommandResult.Reject("harbor_job_not_running");
            if (!_Coordinator.TryGetRecord(record.JobId, out _)) return HarborCommandResult.Reject("harbor_job_not_held");
            return await _Coordinator.StopAsync(caller, record.JobId, 10000, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private HarborJobRecord Current(HarborJobRecord stored)
        {
            return _Coordinator.TryGetRecord(stored.JobId, out HarborJobRecord? live) && live != null ? live : stored;
        }

        private Task<bool> CanSeeAsync(AuthContext caller, HarborJobRecord record, CancellationToken token)
        {
            return HarborRunnerAuthorization.IsOwnerOrHasAuthorityAsync(_Authority, caller, record.TenantId, record.UserId, token);
        }

        #endregion
    }
}
