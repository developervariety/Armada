namespace Armada.Client
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Client.Http;
    using Armada.Core.Models;

    /// <summary>
    /// Fixture battery calls: list the packs, run packs against a captain and read a run
    /// (<c>/api/v1/battery</c>).
    /// </summary>
    public partial class ArmadaClient
    {
        #region Public-Methods

        /// <summary>
        /// GET /api/v1/battery/packs: one page of battery packs, with validation errors for malformed ones.
        /// </summary>
        /// <param name="pageNumber">Page number, or null for 1.</param>
        /// <param name="pageSize">Page size, or null for 100.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One page of packs.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<EnumerationResult<BatteryPackInfo>?> ListBatteryPacksAsync(int? pageNumber = null, int? pageSize = null, CancellationToken token = default)
        {
            List<string> parts = new List<string>();
            if (pageNumber.HasValue) parts.Add("pageNumber=" + pageNumber.Value.ToString(CultureInfo.InvariantCulture));
            if (pageSize.HasValue) parts.Add("pageSize=" + pageSize.Value.ToString(CultureInfo.InvariantCulture));
            string query = parts.Count == 0 ? String.Empty : "?" + String.Join("&", parts);
            return GetAsync<EnumerationResult<BatteryPackInfo>>($"/api/v1/battery/packs{query}", null, token);
        }

        /// <summary>
        /// POST /api/v1/battery/packs/enumerate: one page of the fixture battery packs, with the total.
        /// </summary>
        /// <param name="query">Paging and filters, or null for the first page.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One page.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<EnumerationResult<BatteryPackInfo>?> EnumerateBatteryPacksAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return PostAsync<EnumerationResult<BatteryPackInfo>>("/api/v1/battery/packs/enumerate", query ?? new EnumerationQuery(), null, token);
        }

        /// <summary>
        /// POST /api/v1/battery/runs: run packs against a captain. Returns the queued run at once. Global admin or tenant
        /// admin of the captain's tenant; 400 for no packs, an unknown or malformed pack, or a missing fixture vessel
        /// without CreateFixtureVessel.
        /// </summary>
        /// <param name="request">Packs, captain, run count, seed and whether to create fixture vessels.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The queued run.</returns>
        /// <exception cref="ArgumentNullException">Thrown when request is null.</exception>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<BatteryRun?> RunBatteryAsync(BatteryRunRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return PostAsync<BatteryRun>($"/api/v1/battery/runs", request, null, token);
        }

        /// <summary>
        /// GET /api/v1/battery/runs/{id}: one run with its per-task results.
        /// </summary>
        /// <param name="id">Run id (bat_ prefix).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The run.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<BatteryRun?> GetBatteryRunAsync(string id, CancellationToken token = default)
        {
            return GetAsync<BatteryRun>($"/api/v1/battery/runs/{E(id)}", null, token);
        }

        #endregion
    }
}
