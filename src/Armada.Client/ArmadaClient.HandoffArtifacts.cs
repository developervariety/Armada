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
    /// Handoff artifact calls (<c>/api/v1/missions/{id}/handoff-artifacts</c>, <c>/api/v1/handoff-artifacts/{id}</c>).
    /// </summary>
    public partial class ArmadaClient
    {
        #region Public-Methods

        /// <summary>
        /// GET /api/v1/missions/{id}/handoff-artifacts: one page of a mission's handoff artifacts, newest first, without
        /// output and diff text.
        /// </summary>
        /// <param name="missionId">Mission id (msn_ prefix).</param>
        /// <param name="pageNumber">Page number, or null for the first page.</param>
        /// <param name="pageSize">Page size (1-1000), or null for the server default (25).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One page.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<EnumerationResult<HandoffArtifact>?> ListMissionHandoffArtifactsAsync(string missionId, int? pageNumber = null, int? pageSize = null, CancellationToken token = default)
        {
            List<KeyValuePair<string, string?>> pairs = new List<KeyValuePair<string, string?>>();
            if (pageNumber.HasValue) pairs.Add(new KeyValuePair<string, string?>("pageNumber", pageNumber.Value.ToString(CultureInfo.InvariantCulture)));
            if (pageSize.HasValue) pairs.Add(new KeyValuePair<string, string?>("pageSize", pageSize.Value.ToString(CultureInfo.InvariantCulture)));
            return GetAsync<EnumerationResult<HandoffArtifact>>($"/api/v1/missions/{E(missionId)}/handoff-artifacts{ArmadaQueryString.FromPairs(pairs)}", null, token);
        }

        /// <summary>
        /// POST /api/v1/missions/{id}/handoff-artifacts/enumerate: one page of a mission's handoff artifacts, newest first.
        /// </summary>
        /// <param name="missionId">Mission id (msn_ prefix).</param>
        /// <param name="query">Paging, or null for the first page of 10.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One page.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<EnumerationResult<HandoffArtifact>?> EnumerateMissionHandoffArtifactsAsync(string missionId, EnumerationQuery? query = null, CancellationToken token = default)
        {
            return PostAsync<EnumerationResult<HandoffArtifact>>($"/api/v1/missions/{E(missionId)}/handoff-artifacts/enumerate", query ?? new EnumerationQuery(), null, token);
        }

        /// <summary>
        /// GET /api/v1/handoff-artifacts/{id}: the complete artifact, with output and diff.
        /// </summary>
        /// <param name="id">Artifact id (hoa_ prefix).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The artifact.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<HandoffArtifact?> GetHandoffArtifactAsync(string id, CancellationToken token = default)
        {
            return GetAsync<HandoffArtifact>($"/api/v1/handoff-artifacts/{E(id)}", null, token);
        }

        #endregion
    }
}
