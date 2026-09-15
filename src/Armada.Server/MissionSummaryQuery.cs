namespace Armada.Server
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one caller-scoped read of lightweight mission summaries. Every surface that lists mission
    /// summaries (REST and the WebSocket command hub) reads through it, so each returns the same shape
    /// and the same rows to the same caller: a global administrator sees every tenant, a tenant
    /// administrator sees its tenant, and any other caller sees only its own missions.
    /// </summary>
    public static class MissionSummaryQuery
    {
        #region Public-Methods

        /// <summary>
        /// Enumerate the mission summaries the caller may read.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="query">Pagination and filter query.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The caller-scoped page of mission summaries, with its elapsed time set.</returns>
        /// <exception cref="UnauthorizedAccessException">The caller is not authenticated.</exception>
        public static async Task<EnumerationResult<MissionSummary>> EnumerateForCallerAsync(
            DatabaseDriver database,
            AuthContext caller,
            EnumerationQuery query,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (!caller.IsAuthenticated) throw new UnauthorizedAccessException("Listing mission summaries requires an authenticated caller.");

            Stopwatch sw = Stopwatch.StartNew();
            EnumerationResult<MissionSummary> result = caller.IsAdmin
                ? await database.Missions.EnumerateMissionSummariesAsync(query, token).ConfigureAwait(false)
                : caller.IsTenantAdmin
                    ? await database.Missions.EnumerateMissionSummariesAsync(caller.TenantId!, query, token).ConfigureAwait(false)
                    : await database.Missions.EnumerateMissionSummariesAsync(caller.TenantId!, caller.UserId!, query, token).ConfigureAwait(false);
            result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            return result;
        }

        #endregion
    }
}
