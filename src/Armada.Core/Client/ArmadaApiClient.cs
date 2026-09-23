namespace Armada.Core.Client
{
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Typed HTTP client for the Armada Admiral REST API.
    /// Auto-generated style client covering all /api/v1/ endpoints.
    /// </summary>
    public class ArmadaApiClient : IDisposable
    {
        #region Private-Members

        private HttpClient _Client;
        private string _BaseUrl;
        private bool _OwnsClient;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with a base URL.
        /// </summary>
        /// <param name="baseUrl">Admiral server URL (e.g., http://localhost:7890).</param>
        /// <param name="apiKey">Optional API key for authentication.</param>
        public ArmadaApiClient(string baseUrl, string? apiKey = null)
        {
            if (String.IsNullOrEmpty(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));
            _BaseUrl = baseUrl.TrimEnd('/');
            _Client = new HttpClient();
            _OwnsClient = true;
            if (!String.IsNullOrEmpty(apiKey))
                _Client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        /// <summary>
        /// Instantiate with an existing HttpClient.
        /// </summary>
        /// <param name="client">Pre-configured HttpClient.</param>
        /// <param name="baseUrl">Admiral server URL.</param>
        public ArmadaApiClient(HttpClient client, string baseUrl)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            if (String.IsNullOrEmpty(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));
            _BaseUrl = baseUrl.TrimEnd('/');
            _OwnsClient = false;
        }

        #endregion

        #region Public-Methods-Status

        /// <summary>
        /// Get aggregate system status.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ArmadaStatus?> GetStatusAsync(CancellationToken token = default)
        {
            HttpResponseMessage response = await _Client.GetAsync(_BaseUrl + "/api/v1/status", token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new HttpRequestException(StatusRequiresGlobalAdministratorMessage, null, System.Net.HttpStatusCode.Forbidden);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ArmadaStatus>(json, _JsonOptions);
        }

        /// <summary>
        /// Message for a status request the Admiral refused. Fleet status aggregates every tenant, so only a global
        /// administrator may read it; every client that calls the status route reports a refusal with this text.
        /// </summary>
        public const string StatusRequiresGlobalAdministratorMessage =
            "Fleet status requires a global administrator. This credential belongs to a tenant user or tenant administrator; use a global administrator credential, or read your own records through the scoped list routes.";

        /// <summary>
        /// Health check (no authentication required).
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<bool> HealthCheckAsync(CancellationToken token = default)
        {
            try
            {
                HttpResponseMessage response = await _Client.GetAsync(_BaseUrl + "/api/v1/status/health", token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Request server shutdown.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task StopServerAsync(CancellationToken token = default)
        {
            await PostAsync("/api/v1/server/stop", token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Fleets

        /// <summary>
        /// List all fleets.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Fleet>?> ListFleetsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Fleet>>("/api/v1/fleets", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate fleets with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Fleet>?> EnumerateFleetsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Fleet>, EnumerationQuery>("/api/v1/fleets/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a fleet by ID.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Fleet?> GetFleetAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Fleet>("/api/v1/fleets/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a fleet.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Fleet?> CreateFleetAsync(Fleet fleet, CancellationToken token = default)
        {
            return await PostAsync<Fleet, Fleet>("/api/v1/fleets", fleet, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a fleet.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Fleet?> UpdateFleetAsync(string id, Fleet fleet, CancellationToken token = default)
        {
            return await PutAsync<Fleet, Fleet>("/api/v1/fleets/" + id, fleet, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a fleet.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteFleetAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/fleets/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Vessels

        /// <summary>
        /// List all vessels, optionally filtered by fleet.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Vessel>?> ListVesselsAsync(string? fleetId = null, CancellationToken token = default)
        {
            string path = "/api/v1/vessels";
            if (!String.IsNullOrEmpty(fleetId)) path += "?fleetId=" + fleetId;
            return await GetAsync<EnumerationResult<Vessel>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate vessels with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Vessel>?> EnumerateVesselsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Vessel>, EnumerationQuery>("/api/v1/vessels/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a vessel by ID.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Vessel?> GetVesselAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Vessel>("/api/v1/vessels/" + id, token).ConfigureAwait(false);
        }

        /// <summary>List local branches and repository head information for a vessel.</summary>
        /// <param name="id">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The read-only branch inspection response.</returns>
        public async Task<BranchListResponse?> ListVesselBranchesAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<BranchListResponse>("/api/v1/vessels/" + EscapePathSegment(id) + "/branches", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a vessel.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Vessel?> CreateVesselAsync(Vessel vessel, CancellationToken token = default)
        {
            return await PostAsync<Vessel, Vessel>("/api/v1/vessels", vessel, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a vessel.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Vessel?> UpdateVesselAsync(string id, Vessel vessel, CancellationToken token = default)
        {
            return await PutAsync<Vessel, Vessel>("/api/v1/vessels/" + id, vessel, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a vessel.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteVesselAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/vessels/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Captains

        /// <summary>
        /// List all captains.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Captain>?> ListCaptainsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Captain>>("/api/v1/captains", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate captains with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Captain>?> EnumerateCaptainsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Captain>, EnumerationQuery>("/api/v1/captains/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a captain by ID.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Captain?> GetCaptainAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Captain>("/api/v1/captains/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a captain. Only the captain's configuration fields are sent; the server assigns
        /// identity, state and timestamps.
        /// </summary>
        public async Task<Captain?> CreateCaptainAsync(Captain captain, CancellationToken token = default)
        {
            return await PostAsync<Captain, Dictionary<string, object?>>("/api/v1/captains", CaptainInputMapping.ToRequestBody(captain), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Replace a captain's configuration fields. Only configuration is sent; server-owned
        /// fields keep their stored values.
        /// </summary>
        public async Task<Captain?> UpdateCaptainAsync(string id, Captain captain, CancellationToken token = default)
        {
            return await PutAsync<Captain, Dictionary<string, object?>>("/api/v1/captains/" + id, CaptainInputMapping.ToRequestBody(captain), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Stop a captain.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task StopCaptainAsync(string id, CancellationToken token = default)
        {
            await PostAsync("/api/v1/captains/" + id + "/stop", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Stop every working captain and every active planning and objective refinement session with the server's
        /// single stop-all operation, and return its stopped and failed counts.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stop-all result, or null when the server returned no body.</returns>
        public async Task<CaptainStopAllResult?> StopAllCaptainsAsync(CancellationToken token = default)
        {
            return await PostAsync<CaptainStopAllResult>("/api/v1/captains/stop-all", new { }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a captain.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteCaptainAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/captains/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Missions

        /// <summary>
        /// List missions with optional filters.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Mission>?> ListMissionsAsync(
            string? status = null,
            string? vesselId = null,
            string? captainId = null,
            string? voyageId = null,
            CancellationToken token = default)
        {
            List<string> queryParts = new List<string>();
            if (!String.IsNullOrEmpty(status)) queryParts.Add("status=" + status);
            if (!String.IsNullOrEmpty(vesselId)) queryParts.Add("vessel=" + vesselId);
            if (!String.IsNullOrEmpty(captainId)) queryParts.Add("captain=" + captainId);
            if (!String.IsNullOrEmpty(voyageId)) queryParts.Add("voyage=" + voyageId);
            string path = "/api/v1/missions";
            if (queryParts.Count > 0) path += "?" + String.Join("&", queryParts);
            return await GetAsync<EnumerationResult<Mission>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate missions with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Mission>?> EnumerateMissionsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Mission>, EnumerationQuery>("/api/v1/missions/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a mission by ID.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Mission?> GetMissionAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Mission>("/api/v1/missions/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a mission.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Mission?> CreateMissionAsync(Mission mission, CancellationToken token = default)
        {
            return await PostAsync<Mission, Mission>("/api/v1/missions", mission, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a mission.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Mission?> UpdateMissionAsync(string id, Mission mission, CancellationToken token = default)
        {
            return await PutAsync<Mission, Mission>("/api/v1/missions/" + id, mission, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete (cancel) a mission.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteMissionAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/missions/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Restart a failed or cancelled mission, resetting it to Pending for re-dispatch.
        /// Optionally update the title and description before restarting.
        /// </summary>
        /// <param name="id">Mission ID.</param>
        /// <param name="title">Optional new title. Pass null to keep the original.</param>
        /// <param name="description">Optional new description. Pass null to keep the original.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The restarted mission, or null on failure.</returns>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Mission?> RestartMissionAsync(string id, string? title = null, string? description = null, CancellationToken token = default)
        {
            object body = new { Title = title, Description = description };
            return await PostAsync<Mission, object>("/api/v1/missions/" + id + "/restart", body, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the unified diff for a mission's changes against the base branch.
        /// Only available while the captain's worktree exists.
        /// </summary>
        /// <param name="id">Mission ID.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Unified diff text, or null if unavailable.</returns>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<string?> GetMissionDiffAsync(string id, CancellationToken token = default)
        {
            try
            {
                HttpResponseMessage response = await _Client.GetAsync(_BaseUrl + "/api/v1/missions/" + id + "/diff", token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Public-Methods-Voyages

        /// <summary>
        /// List voyages with optional status filter.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Voyage>?> ListVoyagesAsync(string? status = null, CancellationToken token = default)
        {
            string path = "/api/v1/voyages";
            if (!String.IsNullOrEmpty(status)) path += "?status=" + status;
            return await GetAsync<EnumerationResult<Voyage>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate voyages with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Voyage>?> EnumerateVoyagesAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Voyage>, EnumerationQuery>("/api/v1/voyages/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a voyage by ID.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Voyage?> GetVoyageAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Voyage>("/api/v1/voyages/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a voyage.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Voyage?> CreateVoyageAsync(Voyage voyage, CancellationToken token = default)
        {
            return await PostAsync<Voyage, Voyage>("/api/v1/voyages", voyage, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Dispatch a voyage with mission descriptions and optional playbooks.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Voyage?> DispatchVoyageAsync(VoyageDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await PostAsync<Voyage, VoyageDispatchRequest>("/api/v1/voyages", request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel a voyage and its pending missions.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task CancelVoyageAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/voyages/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Permanently delete a mission from the database.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task PurgeMissionAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/missions/" + id + "/purge", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Permanently delete a voyage and all its associated missions.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task PurgeVoyageAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/voyages/" + id + "/purge", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete (cancel) a voyage. Kept for backwards compatibility.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteVoyageAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/voyages/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Signals

        /// <summary>
        /// List signals.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Signal>?> ListSignalsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Signal>>("/api/v1/signals", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate signals with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Signal>?> EnumerateSignalsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Signal>, EnumerationQuery>("/api/v1/signals/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a signal.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Signal?> CreateSignalAsync(Signal signal, CancellationToken token = default)
        {
            return await PostAsync<Signal, Signal>("/api/v1/signals", signal, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Events

        /// <summary>
        /// List events with optional filters.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<ArmadaEvent>?> ListEventsAsync(
            string? type = null,
            string? captainId = null,
            string? missionId = null,
            string? vesselId = null,
            string? voyageId = null,
            int limit = 50,
            CancellationToken token = default)
        {
            List<string> queryParts = new List<string>();
            if (!String.IsNullOrEmpty(type)) queryParts.Add("type=" + type);
            if (!String.IsNullOrEmpty(captainId)) queryParts.Add("captainId=" + captainId);
            if (!String.IsNullOrEmpty(missionId)) queryParts.Add("missionId=" + missionId);
            if (!String.IsNullOrEmpty(vesselId)) queryParts.Add("vesselId=" + vesselId);
            if (!String.IsNullOrEmpty(voyageId)) queryParts.Add("voyageId=" + voyageId);
            if (limit != 50) queryParts.Add("limit=" + limit);
            string path = "/api/v1/events";
            if (queryParts.Count > 0) path += "?" + String.Join("&", queryParts);
            return await GetAsync<EnumerationResult<ArmadaEvent>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate events with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<ArmadaEvent>?> EnumerateEventsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<ArmadaEvent>, EnumerationQuery>("/api/v1/events/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-MergeQueue

        /// <summary>
        /// List merge queue entries.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<MergeEntry>?> ListMergeQueueAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<MergeEntry>>("/api/v1/merge-queue", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate merge queue entries with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<MergeEntry>?> EnumerateMergeQueueAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<MergeEntry>, EnumerationQuery>("/api/v1/merge-queue/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a merge queue entry by ID.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<MergeEntry?> GetMergeEntryAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<MergeEntry>("/api/v1/merge-queue/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enqueue a branch for merge.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<MergeEntry?> EnqueueMergeAsync(MergeEntry entry, CancellationToken token = default)
        {
            return await PostAsync<MergeEntry, MergeEntry>("/api/v1/merge-queue", entry, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel a merge queue entry.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task CancelMergeAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/merge-queue/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Trigger processing of the merge queue.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task ProcessMergeQueueAsync(CancellationToken token = default)
        {
            await PostAsync("/api/v1/merge-queue/process", token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Playbooks

        /// <summary>
        /// List playbooks.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Playbook>?> ListPlaybooksAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Playbook>>("/api/v1/playbooks", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate playbooks with pagination and filtering.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Playbook>?> EnumeratePlaybooksAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Playbook>, EnumerationQuery>("/api/v1/playbooks/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a playbook by ID.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Playbook?> GetPlaybookAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Playbook>("/api/v1/playbooks/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a playbook.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Playbook?> CreatePlaybookAsync(Playbook playbook, CancellationToken token = default)
        {
            return await PostAsync<Playbook, Playbook>("/api/v1/playbooks", playbook, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a playbook.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Playbook?> UpdatePlaybookAsync(string id, Playbook playbook, CancellationToken token = default)
        {
            return await PutAsync<Playbook, Playbook>("/api/v1/playbooks/" + id, playbook, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a playbook.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeletePlaybookAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/playbooks/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Backlog

        /// <summary>List objectives through the primary objective route.</summary>
        public async Task<EnumerationResult<Objective>?> ListObjectivesAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Objective>>("/api/v1/objectives", token).ConfigureAwait(false);
        }

        /// <summary>Reorder objectives through the primary objective route.</summary>
        public async Task<List<Objective>?> ReorderObjectivesAsync(ObjectiveReorderRequest request, CancellationToken token = default)
        {
            return await PostAsync<List<Objective>, ObjectiveReorderRequest>("/api/v1/objectives/reorder", request, token).ConfigureAwait(false);
        }



        /// <summary>
        /// Enumerate backlog items using the user-facing backlog alias surface.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<EnumerationResult<Objective>?> ListBacklogAsync(ObjectiveQuery? query = null, CancellationToken token = default)
        {
            if (query == null)
                return await GetAsync<EnumerationResult<Objective>>("/api/v1/backlog", token).ConfigureAwait(false);

            return await PostAsync<EnumerationResult<Objective>, ObjectiveQuery>("/api/v1/backlog/enumerate", query, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Apply one or more explicit backlog rank updates using the backlog alias surface.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<List<Objective>?> ReorderBacklogAsync(ObjectiveReorderRequest request, CancellationToken token = default)
        {
            return await PostAsync<List<Objective>, ObjectiveReorderRequest>("/api/v1/backlog/reorder", request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get one backlog item by identifier.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Objective?> GetBacklogItemAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Objective>("/api/v1/backlog/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create one backlog item.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Objective?> CreateBacklogItemAsync(ObjectiveUpsertRequest request, CancellationToken token = default)
        {
            return await PostAsync<Objective, ObjectiveUpsertRequest>("/api/v1/backlog", request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update one backlog item.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Objective?> UpdateBacklogItemAsync(string id, ObjectiveUpsertRequest request, CancellationToken token = default)
        {
            return await PutAsync<Objective, ObjectiveUpsertRequest>("/api/v1/backlog/" + id, request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete one backlog item.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteBacklogItemAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/backlog/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Workspace

        /// <summary>List check runs.</summary>
        public async Task<EnumerationResult<CheckRun>?> ListCheckRunsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<CheckRun>>("/api/v1/check-runs", token).ConfigureAwait(false);
        }

        /// <summary>Enumerate check runs with pagination and filtering.</summary>
        public async Task<EnumerationResult<CheckRun>?> EnumerateCheckRunsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<CheckRun>, EnumerationQuery>("/api/v1/check-runs/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>List deployments.</summary>
        public async Task<EnumerationResult<Deployment>?> ListDeploymentsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Deployment>>("/api/v1/deployments", token).ConfigureAwait(false);
        }

        /// <summary>Enumerate deployments with pagination and filtering.</summary>
        public async Task<EnumerationResult<Deployment>?> EnumerateDeploymentsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Deployment>, EnumerationQuery>("/api/v1/deployments/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>List environments.</summary>
        public async Task<EnumerationResult<DeploymentEnvironment>?> ListEnvironmentsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<DeploymentEnvironment>>("/api/v1/environments", token).ConfigureAwait(false);
        }

        /// <summary>Enumerate environments with pagination and filtering.</summary>
        public async Task<EnumerationResult<DeploymentEnvironment>?> EnumerateEnvironmentsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<DeploymentEnvironment>, EnumerationQuery>("/api/v1/environments/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>List releases.</summary>
        public async Task<EnumerationResult<Release>?> ListReleasesAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Release>>("/api/v1/releases", token).ConfigureAwait(false);
        }

        /// <summary>Enumerate releases with pagination and filtering.</summary>
        public async Task<EnumerationResult<Release>?> EnumerateReleasesAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Release>, EnumerationQuery>("/api/v1/releases/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }



        /// <summary>
        /// Run a command in a vessel's workspace (the in-browser dock terminal; tenant admins only).
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkspaceExecResult?> ExecWorkspaceCommandAsync(string vesselId, string command, int timeoutSeconds = 60, CancellationToken token = default)
        {
            WorkspaceExecRequest request = new WorkspaceExecRequest { Command = command, TimeoutSeconds = timeoutSeconds };
            return await PostAsync<WorkspaceExecResult, WorkspaceExecRequest>("/api/v1/workspace/vessels/" + vesselId + "/exec", request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a unified working-tree diff for a vessel, optionally scoped to one path.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkspaceDiffResult?> GetWorkspaceDiffAsync(string vesselId, string? path = null, CancellationToken token = default)
        {
            string url = "/api/v1/workspace/vessels/" + vesselId + "/diff";
            if (!String.IsNullOrWhiteSpace(path))
                url += "?path=" + Uri.EscapeDataString(path);
            return await GetAsync<WorkspaceDiffResult>(url, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Inbox

        /// <summary>
        /// Get the needs-you inbox: items across the fleet awaiting operator attention.
        /// </summary>
        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<List<InboxItem>?> GetInboxAsync(CancellationToken token = default)
        {
            return await GetAsync<List<InboxItem>>("/api/v1/inbox", token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-ExtendedContracts

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<VesselReadinessResult?> GetVesselReadinessAsync(
            string id,
            string? workflowProfileId = null,
            CheckRunTypeEnum? checkType = null,
            string? environmentName = null,
            bool includeWorkflowRequirements = true,
            CancellationToken token = default)
        {
            List<string> query = new List<string>();
            if (!String.IsNullOrWhiteSpace(workflowProfileId))
                query.Add("workflowProfileId=" + Uri.EscapeDataString(workflowProfileId));
            if (checkType.HasValue)
                query.Add("checkType=" + Uri.EscapeDataString(checkType.Value.ToString()));
            if (!String.IsNullOrWhiteSpace(environmentName))
                query.Add("environmentName=" + Uri.EscapeDataString(environmentName));
            if (!includeWorkflowRequirements)
                query.Add("includeWorkflowRequirements=false");

            string path = "/api/v1/vessels/" + EscapePathSegment(id) + "/readiness";
            if (query.Count > 0)
                path += "?" + String.Join("&", query);

            return await GetAsync<VesselReadinessResult>(path, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<LandingPreviewResult?> GetVesselLandingPreviewAsync(
            string id,
            string? sourceBranch = null,
            CancellationToken token = default)
        {
            string path = "/api/v1/vessels/" + EscapePathSegment(id) + "/landing-preview";
            if (!String.IsNullOrWhiteSpace(sourceBranch))
                path += "?sourceBranch=" + Uri.EscapeDataString(sourceBranch);
            return await GetAsync<LandingPreviewResult>(path, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<DeploymentEnvironment?> GetEnvironmentAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<DeploymentEnvironment>("/api/v1/environments/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<DeploymentEnvironment?> CreateEnvironmentAsync(
            DeploymentEnvironmentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PostAsync<DeploymentEnvironment, DeploymentEnvironmentUpsertRequest>(
                "/api/v1/environments",
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<DeploymentEnvironment?> UpdateEnvironmentAsync(
            string id,
            DeploymentEnvironmentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PutAsync<DeploymentEnvironment, DeploymentEnvironmentUpsertRequest>(
                "/api/v1/environments/" + EscapePathSegment(id),
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteEnvironmentAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/environments/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Deployment?> GetDeploymentAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Deployment>("/api/v1/deployments/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Deployment?> CreateDeploymentAsync(
            DeploymentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PostAsync<Deployment, DeploymentUpsertRequest>(
                "/api/v1/deployments",
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Deployment?> UpdateDeploymentAsync(
            string id,
            DeploymentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PutAsync<Deployment, DeploymentUpsertRequest>(
                "/api/v1/deployments/" + EscapePathSegment(id),
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Deployment?> ApproveDeploymentAsync(
            string id,
            string? comment = null,
            CancellationToken token = default)
        {
            object body = String.IsNullOrWhiteSpace(comment)
                ? new { }
                : new { Comment = comment };
            return await PostAsync<Deployment>("/api/v1/deployments/" + EscapePathSegment(id) + "/approve", body, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Deployment?> DenyDeploymentAsync(
            string id,
            string? comment = null,
            CancellationToken token = default)
        {
            object body = String.IsNullOrWhiteSpace(comment)
                ? new { }
                : new { Comment = comment };
            return await PostAsync<Deployment>("/api/v1/deployments/" + EscapePathSegment(id) + "/deny", body, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Deployment?> VerifyDeploymentAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<Deployment>("/api/v1/deployments/" + EscapePathSegment(id) + "/verify", new { }, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Deployment?> RollbackDeploymentAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<Deployment>("/api/v1/deployments/" + EscapePathSegment(id) + "/rollback", new { }, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteDeploymentAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/deployments/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<LandingPreviewResult?> GetMissionLandingPreviewAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<LandingPreviewResult>("/api/v1/missions/" + EscapePathSegment(id) + "/landing-preview", token).ConfigureAwait(false);
        }

        /// <summary>Get GitHub pull-request evidence for a release.</summary>
        public async Task<List<GitHubPullRequestDetail>?> GetReleaseGitHubPullRequestsAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<List<GitHubPullRequestDetail>>("/api/v1/releases/" + EscapePathSegment(id) + "/github/pull-requests", token).ConfigureAwait(false);
        }

        /// <summary>Get GitHub pull-request evidence for a mission.</summary>
        public async Task<GitHubPullRequestDetail?> GetMissionGitHubPullRequestAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<GitHubPullRequestDetail>("/api/v1/missions/" + EscapePathSegment(id) + "/github/pull-request", token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<TokenUsageSummaryResult?> GetTokenUsageSummaryAsync(
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            double? bucketMinutes = null,
            string? model = null,
            string? runtime = null,
            string? source = null,
            string? vesselId = null,
            string? captainId = null,
            CancellationToken token = default)
        {
            List<string> queryParts = new List<string>();
            if (fromUtc.HasValue) queryParts.Add("fromUtc=" + Uri.EscapeDataString(fromUtc.Value.ToUniversalTime().ToString("o")));
            if (toUtc.HasValue) queryParts.Add("toUtc=" + Uri.EscapeDataString(toUtc.Value.ToUniversalTime().ToString("o")));
            if (bucketMinutes.HasValue) queryParts.Add("bucketMinutes=" + bucketMinutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!String.IsNullOrEmpty(model)) queryParts.Add("model=" + Uri.EscapeDataString(model));
            if (!String.IsNullOrEmpty(runtime)) queryParts.Add("runtime=" + Uri.EscapeDataString(runtime));
            if (!String.IsNullOrEmpty(source)) queryParts.Add("source=" + Uri.EscapeDataString(source));
            if (!String.IsNullOrEmpty(vesselId)) queryParts.Add("vesselId=" + Uri.EscapeDataString(vesselId));
            if (!String.IsNullOrEmpty(captainId)) queryParts.Add("captainId=" + Uri.EscapeDataString(captainId));
            string path = "/api/v1/token-usage/summary";
            if (queryParts.Count > 0) path += "?" + String.Join("&", queryParts);
            return await GetAsync<TokenUsageSummaryResult>(path, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Release?> GetReleaseAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Release>("/api/v1/releases/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Release?> CreateReleaseAsync(ReleaseUpsertRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await PostAsync<Release, ReleaseUpsertRequest>("/api/v1/releases", request, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Release?> UpdateReleaseAsync(string id, ReleaseUpsertRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await PutAsync<Release, ReleaseUpsertRequest>("/api/v1/releases/" + EscapePathSegment(id), request, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Release?> RefreshReleaseAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<Release>("/api/v1/releases/" + EscapePathSegment(id) + "/refresh", new { }, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteReleaseAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/releases/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<CheckRun?> GetCheckRunAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<CheckRun>("/api/v1/check-runs/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<CheckRun?> RunCheckAsync(CheckRunRequest request, CancellationToken token = default)
        {
            return await PostAsync<CheckRun, CheckRunRequest>("/api/v1/check-runs", request, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<GitHubActionsSyncResult?> SyncGitHubActionsAsync(GitHubActionsSyncRequest request, CancellationToken token = default)
        {
            return await PostAsync<GitHubActionsSyncResult, GitHubActionsSyncRequest>("/api/v1/check-runs/sync/github-actions", request, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<CheckRun?> RetryCheckRunAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<CheckRun>("/api/v1/check-runs/" + EscapePathSegment(id) + "/retry", new { }, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteCheckRunAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/check-runs/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Read one long-running background job. Requires a global administrator.</summary>
        public async Task<LongRunningJob?> GetJobAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<LongRunningJob>("/api/v1/jobs/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-SupportedProfilesAndObjectives

        /// <summary>List workflow profiles.</summary>
        public async Task<EnumerationResult<WorkflowProfile>?> ListWorkflowProfilesAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<WorkflowProfile>>("/api/v1/workflow-profiles", token).ConfigureAwait(false);
        }

        /// <summary>Enumerate workflow profiles with pagination and filtering.</summary>
        public async Task<EnumerationResult<WorkflowProfile>?> EnumerateWorkflowProfilesAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<WorkflowProfile>, EnumerationQuery>("/api/v1/workflow-profiles/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>List project profiles.</summary>
        public async Task<EnumerationResult<ProjectProfile>?> ListProjectProfilesAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<ProjectProfile>>("/api/v1/project-profiles", token).ConfigureAwait(false);
        }

        /// <summary>Enumerate project profiles with pagination and filtering.</summary>
        public async Task<EnumerationResult<ProjectProfile>?> EnumerateProjectProfilesAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<ProjectProfile>, EnumerationQuery>("/api/v1/project-profiles/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>List skills.</summary>
        public async Task<EnumerationResult<Skill>?> ListSkillsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Skill>>("/api/v1/skills", token).ConfigureAwait(false);
        }

        /// <summary>Enumerate skills with pagination and filtering.</summary>
        public async Task<EnumerationResult<Skill>?> EnumerateSkillsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Skill>, EnumerationQuery>("/api/v1/skills/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }



        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkflowProfile?> GetWorkflowProfileAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<WorkflowProfile>("/api/v1/workflow-profiles/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkflowProfileValidationResult?> ValidateWorkflowProfileAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            return await PostAsync<WorkflowProfileValidationResult, WorkflowProfile>("/api/v1/workflow-profiles/validate", profile, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkflowProfileResolutionPreviewResult?> PreviewWorkflowProfileForVesselAsync(string vesselId, string? workflowProfileId = null, CancellationToken token = default)
        {
            string path = "/api/v1/workflow-profiles/preview/vessels/" + EscapePathSegment(vesselId);
            if (!String.IsNullOrWhiteSpace(workflowProfileId))
                path += "?workflowProfileId=" + Uri.EscapeDataString(workflowProfileId);
            return await GetAsync<WorkflowProfileResolutionPreviewResult>(path, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkflowProfile?> ResolveWorkflowProfileAsync(string vesselId, string? workflowProfileId = null, CancellationToken token = default)
        {
            string path = "/api/v1/workflow-profiles/resolve/vessels/" + EscapePathSegment(vesselId);
            if (!String.IsNullOrWhiteSpace(workflowProfileId))
                path += "?workflowProfileId=" + Uri.EscapeDataString(workflowProfileId);
            return await GetAsync<WorkflowProfile>(path, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkflowProfile?> CreateWorkflowProfileAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            return await PostAsync<WorkflowProfile, WorkflowProfile>("/api/v1/workflow-profiles", profile, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<WorkflowProfile?> UpdateWorkflowProfileAsync(string id, WorkflowProfile profile, CancellationToken token = default)
        {
            return await PutAsync<WorkflowProfile, WorkflowProfile>("/api/v1/workflow-profiles/" + EscapePathSegment(id), profile, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteWorkflowProfileAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/workflow-profiles/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ProjectProfile?> GetProjectProfileAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<ProjectProfile>("/api/v1/project-profiles/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ProjectProfileValidationResult?> ValidateProjectProfileAsync(ProjectProfile profile, CancellationToken token = default)
        {
            return await PostAsync<ProjectProfileValidationResult, ProjectProfile>("/api/v1/project-profiles/validate", profile, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ProjectProfileResolutionResult?> ResolveProjectProfileAsync(string vesselId, string? projectProfileId = null, CancellationToken token = default)
        {
            string path = "/api/v1/project-profiles/resolve/vessels/" + EscapePathSegment(vesselId);
            if (!String.IsNullOrWhiteSpace(projectProfileId))
                path += "?projectProfileId=" + Uri.EscapeDataString(projectProfileId);
            return await GetAsync<ProjectProfileResolutionResult>(path, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<PersonaPromptPreview?> PreviewPersonaPromptAsync(string projectProfileId, string persona, CancellationToken token = default)
        {
            string path = "/api/v1/project-profiles/" + EscapePathSegment(projectProfileId) + "/persona-preview/" + Uri.EscapeDataString(persona);
            return await GetAsync<PersonaPromptPreview>(path, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ProjectProfile?> CreateProjectProfileAsync(ProjectProfile profile, CancellationToken token = default)
        {
            return await PostAsync<ProjectProfile, ProjectProfile>("/api/v1/project-profiles", profile, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ProjectProfile?> UpdateProjectProfileAsync(string id, ProjectProfile profile, CancellationToken token = default)
        {
            return await PutAsync<ProjectProfile, ProjectProfile>("/api/v1/project-profiles/" + EscapePathSegment(id), profile, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteProjectProfileAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/project-profiles/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Skill?> GetSkillAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Skill>("/api/v1/skills/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Skill?> CreateSkillAsync(Skill skill, CancellationToken token = default)
        {
            return await PostAsync<Skill, Skill>("/api/v1/skills", skill, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Skill?> UpdateSkillAsync(string id, Skill skill, CancellationToken token = default)
        {
            return await PutAsync<Skill, Skill>("/api/v1/skills/" + EscapePathSegment(id), skill, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteSkillAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/skills/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<AskResponse?> AskAsync(string message, CancellationToken token = default)
        {
            return await PostAsync<AskResponse, AskRequest>("/api/v1/ask", new AskRequest { Message = message }, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Objective?> GetObjectiveAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Objective>("/api/v1/objectives/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Objective?> CreateObjectiveAsync(ObjectiveUpsertRequest request, CancellationToken token = default)
        {
            return await PostAsync<Objective, ObjectiveUpsertRequest>("/api/v1/objectives", request, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Objective?> UpdateObjectiveAsync(string id, ObjectiveUpsertRequest request, CancellationToken token = default)
        {
            return await PutAsync<Objective, ObjectiveUpsertRequest>("/api/v1/objectives/" + EscapePathSegment(id), request, token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteObjectiveAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/objectives/" + EscapePathSegment(id), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<Objective?> ImportObjectiveFromGitHubAsync(GitHubObjectiveImportRequest request, CancellationToken token = default)
        {
            return await PostAsync<Objective, GitHubObjectiveImportRequest>("/api/v1/objectives/import/github", request, token).ConfigureAwait(false);
        }

        /// <summary>List refinement sessions for an objective.</summary>
        public async Task<List<ObjectiveRefinementSession>?> ListObjectiveRefinementSessionsAsync(string objectiveId, CancellationToken token = default)
        {
            return await GetAsync<List<ObjectiveRefinementSession>>("/api/v1/objectives/" + EscapePathSegment(objectiveId) + "/refinement-sessions", token).ConfigureAwait(false);
        }

        /// <summary>List refinement sessions for a backlog item.</summary>
        public async Task<List<ObjectiveRefinementSession>?> ListBacklogRefinementSessionsAsync(string objectiveId, CancellationToken token = default)
        {
            return await GetAsync<List<ObjectiveRefinementSession>>("/api/v1/backlog/" + EscapePathSegment(objectiveId) + "/refinement-sessions", token).ConfigureAwait(false);
        }

        /// <summary>Create a refinement session for an objective.</summary>
        public async Task<ObjectiveRefinementSessionDetail?> CreateObjectiveRefinementSessionAsync(
            string objectiveId,
            ObjectiveRefinementSessionCreateRequest request,
            CancellationToken token = default)
        {
            return await PostAsync<ObjectiveRefinementSessionDetail, ObjectiveRefinementSessionCreateRequest>(
                "/api/v1/objectives/" + EscapePathSegment(objectiveId) + "/refinement-sessions",
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ObjectiveRefinementSessionDetail?> CreateBacklogRefinementSessionAsync(
            string objectiveId,
            ObjectiveRefinementSessionCreateRequest request,
            CancellationToken token = default)
        {
            return await PostAsync<ObjectiveRefinementSessionDetail, ObjectiveRefinementSessionCreateRequest>(
                "/api/v1/backlog/" + EscapePathSegment(objectiveId) + "/refinement-sessions",
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ObjectiveRefinementSessionDetail?> GetObjectiveRefinementSessionAsync(string sessionId, CancellationToken token = default)
        {
            return await GetAsync<ObjectiveRefinementSessionDetail>("/api/v1/objective-refinement-sessions/" + EscapePathSegment(sessionId), token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ObjectiveRefinementSessionDetail?> SendObjectiveRefinementMessageAsync(
            string sessionId,
            ObjectiveRefinementMessageRequest request,
            CancellationToken token = default)
        {
            return await PostAsync<ObjectiveRefinementSessionDetail, ObjectiveRefinementMessageRequest>(
                "/api/v1/objective-refinement-sessions/" + EscapePathSegment(sessionId) + "/messages",
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ObjectiveRefinementSummaryResponse?> SummarizeObjectiveRefinementSessionAsync(
            string sessionId,
            ObjectiveRefinementSummaryRequest? request = null,
            CancellationToken token = default)
        {
            return await PostAsync<ObjectiveRefinementSummaryResponse, ObjectiveRefinementSummaryRequest>(
                "/api/v1/objective-refinement-sessions/" + EscapePathSegment(sessionId) + "/summarize",
                request ?? new ObjectiveRefinementSummaryRequest(),
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ObjectiveRefinementApplyResponse?> ApplyObjectiveRefinementSummaryAsync(
            string sessionId,
            ObjectiveRefinementApplyRequest? request = null,
            CancellationToken token = default)
        {
            return await PostAsync<ObjectiveRefinementApplyResponse, ObjectiveRefinementApplyRequest>(
                "/api/v1/objective-refinement-sessions/" + EscapePathSegment(sessionId) + "/apply",
                request ?? new ObjectiveRefinementApplyRequest(),
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task<ObjectiveRefinementSessionDetail?> StopObjectiveRefinementSessionAsync(string sessionId, CancellationToken token = default)
        {
            return await PostAsync<ObjectiveRefinementSessionDetail>(
                "/api/v1/objective-refinement-sessions/" + EscapePathSegment(sessionId) + "/stop",
                new { },
                token).ConfigureAwait(false);
        }

        /// <summary>Calls the corresponding fork REST API contract.</summary>
        public async Task DeleteObjectiveRefinementSessionAsync(string sessionId, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/objective-refinement-sessions/" + EscapePathSegment(sessionId), token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Background

        /// <summary>List long-running background jobs, newest first, without their results. Requires a global administrator.</summary>
        public async Task<EnumerationResult<LongRunningJob>?> ListJobsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<LongRunningJob>>("/api/v1/jobs", token).ConfigureAwait(false);
        }

        /// <summary>List token usage records with optional filters.</summary>
        public async Task<EnumerationResult<TokenUsageRecord>?> ListTokenUsageAsync(TokenUsageQuery? query = null, CancellationToken token = default)
        {
            List<string> parts = new List<string>();
            if (query != null)
            {
                if (query.PageNumber > 1) parts.Add("pageNumber=" + query.PageNumber);
                if (query.PageSize != 25) parts.Add("pageSize=" + query.PageSize);
                if (!String.IsNullOrWhiteSpace(query.Model)) parts.Add("model=" + Uri.EscapeDataString(query.Model));
                if (!String.IsNullOrWhiteSpace(query.Runtime)) parts.Add("runtime=" + Uri.EscapeDataString(query.Runtime));
                if (!String.IsNullOrWhiteSpace(query.Source)) parts.Add("source=" + Uri.EscapeDataString(query.Source));
                if (!String.IsNullOrWhiteSpace(query.VesselId)) parts.Add("vesselId=" + Uri.EscapeDataString(query.VesselId));
                if (!String.IsNullOrWhiteSpace(query.CaptainId)) parts.Add("captainId=" + Uri.EscapeDataString(query.CaptainId));
                if (query.FromUtc.HasValue) parts.Add("fromUtc=" + Uri.EscapeDataString(query.FromUtc.Value.ToUniversalTime().ToString("o")));
                if (query.ToUtc.HasValue) parts.Add("toUtc=" + Uri.EscapeDataString(query.ToUtc.Value.ToUniversalTime().ToString("o")));
            }
            string path = "/api/v1/token-usage" + (parts.Count == 0 ? String.Empty : "?" + String.Join("&", parts));
            return await GetAsync<EnumerationResult<TokenUsageRecord>>(path, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Dispose

        /// <summary>
        /// Dispose resources.
        /// </summary>
        public void Dispose()
        {
            if (_OwnsClient)
            {
                _Client?.Dispose();
            }
        }

        #endregion

        #region Private-Methods

        private async Task<T?> GetAsync<T>(string path, CancellationToken token) where T : class
        {
            HttpResponseMessage response = await _Client.GetAsync(_BaseUrl + path, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, _JsonOptions);
        }

        private static string EscapePathSegment(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(value));
            return Uri.EscapeDataString(value);
        }

        private async Task<TResponse?> PostAsync<TResponse, TBody>(string path, TBody body, CancellationToken token)
            where TResponse : class
        {
            HttpResponseMessage response = await _Client.PostAsJsonAsync(_BaseUrl + path, body, _JsonOptions, token).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(json.Length > 0 ? json : response.ReasonPhrase, null, response.StatusCode);
            return JsonSerializer.Deserialize<TResponse>(json, _JsonOptions);
        }

        private async Task<T?> PostAsync<T>(string path, object body, CancellationToken token) where T : class
        {
            HttpResponseMessage response = await _Client.PostAsJsonAsync(_BaseUrl + path, body, _JsonOptions, token).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(json.Length > 0 ? json : response.ReasonPhrase, null, response.StatusCode);
            return JsonSerializer.Deserialize<T>(json, _JsonOptions);
        }

        private async Task PostAsync(string path, CancellationToken token)
        {
            HttpResponseMessage response = await _Client.PostAsync(_BaseUrl + path, null, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        private async Task<TResponse?> PutAsync<TResponse, TBody>(string path, TBody body, CancellationToken token)
            where TResponse : class
        {
            HttpResponseMessage response = await _Client.PutAsJsonAsync(_BaseUrl + path, body, _JsonOptions, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TResponse>(json, _JsonOptions);
        }

        private async Task<T?> PutAsync<T>(string path, object body, CancellationToken token) where T : class
        {
            HttpResponseMessage response = await _Client.PutAsJsonAsync(_BaseUrl + path, body, _JsonOptions, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, _JsonOptions);
        }

        private async Task DeleteAsync(string path, CancellationToken token)
        {
            HttpResponseMessage response = await _Client.DeleteAsync(_BaseUrl + path, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        #endregion
    }
}
