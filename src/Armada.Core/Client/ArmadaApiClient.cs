namespace Armada.Core.Client
{
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

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
        public async Task<ArmadaStatus?> GetStatusAsync(CancellationToken token = default)
        {
            return await GetAsync<ArmadaStatus>("/api/v1/status", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Health check (no authentication required).
        /// </summary>
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
        public async Task StopServerAsync(CancellationToken token = default)
        {
            await PostAsync("/api/v1/server/stop", token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Fleets

        /// <summary>
        /// List all fleets.
        /// </summary>
        public async Task<EnumerationResult<Fleet>?> ListFleetsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Fleet>>("/api/v1/fleets", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate fleets with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<Fleet>?> EnumerateFleetsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Fleet>, EnumerationQuery>("/api/v1/fleets/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a fleet by ID.
        /// </summary>
        public async Task<Fleet?> GetFleetAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Fleet>("/api/v1/fleets/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a fleet.
        /// </summary>
        public async Task<Fleet?> CreateFleetAsync(Fleet fleet, CancellationToken token = default)
        {
            return await PostAsync<Fleet, Fleet>("/api/v1/fleets", fleet, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a fleet.
        /// </summary>
        public async Task<Fleet?> UpdateFleetAsync(string id, Fleet fleet, CancellationToken token = default)
        {
            return await PutAsync<Fleet, Fleet>("/api/v1/fleets/" + id, fleet, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a fleet.
        /// </summary>
        public async Task DeleteFleetAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/fleets/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Vessels

        /// <summary>
        /// List all vessels, optionally filtered by fleet.
        /// </summary>
        public async Task<EnumerationResult<Vessel>?> ListVesselsAsync(string? fleetId = null, CancellationToken token = default)
        {
            string path = "/api/v1/vessels";
            if (!String.IsNullOrEmpty(fleetId)) path += "?fleetId=" + fleetId;
            return await GetAsync<EnumerationResult<Vessel>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate vessels with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<Vessel>?> EnumerateVesselsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Vessel>, EnumerationQuery>("/api/v1/vessels/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a vessel by ID.
        /// </summary>
        public async Task<Vessel?> GetVesselAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Vessel>("/api/v1/vessels/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a vessel.
        /// </summary>
        public async Task<Vessel?> CreateVesselAsync(Vessel vessel, CancellationToken token = default)
        {
            return await PostAsync<Vessel, Vessel>("/api/v1/vessels", vessel, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a vessel.
        /// </summary>
        public async Task<Vessel?> UpdateVesselAsync(string id, Vessel vessel, CancellationToken token = default)
        {
            return await PutAsync<Vessel, Vessel>("/api/v1/vessels/" + id, vessel, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a vessel.
        /// </summary>
        public async Task DeleteVesselAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/vessels/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get vessel readiness, optionally scoped to a workflow profile or check type.
        /// </summary>
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

            string path = "/api/v1/vessels/" + id + "/readiness";
            if (query.Count > 0)
                path += "?" + String.Join("&", query);

            return await GetAsync<VesselReadinessResult>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Preview landing readiness for a vessel and optional source branch.
        /// </summary>
        public async Task<LandingPreviewResult?> GetVesselLandingPreviewAsync(
            string id,
            string? sourceBranch = null,
            CancellationToken token = default)
        {
            string path = "/api/v1/vessels/" + id + "/landing-preview";
            if (!String.IsNullOrWhiteSpace(sourceBranch))
                path += "?sourceBranch=" + Uri.EscapeDataString(sourceBranch);
            return await GetAsync<LandingPreviewResult>(path, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Environments

        /// <summary>
        /// List environments with optional filtering.
        /// </summary>
        public async Task<EnumerationResult<DeploymentEnvironment>?> ListEnvironmentsAsync(
            string? vesselId = null,
            EnvironmentKindEnum? kind = null,
            bool? active = null,
            CancellationToken token = default)
        {
            List<string> queryParts = new List<string>();
            if (!String.IsNullOrWhiteSpace(vesselId)) queryParts.Add("vesselId=" + Uri.EscapeDataString(vesselId));
            if (kind.HasValue) queryParts.Add("kind=" + Uri.EscapeDataString(kind.Value.ToString()));
            if (active.HasValue) queryParts.Add("active=" + (active.Value ? "true" : "false"));

            string path = "/api/v1/environments";
            if (queryParts.Count > 0) path += "?" + String.Join("&", queryParts);
            return await GetAsync<EnumerationResult<DeploymentEnvironment>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate environments with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<DeploymentEnvironment>?> EnumerateEnvironmentsAsync(
            DeploymentEnvironmentQuery? query = null,
            CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<DeploymentEnvironment>, DeploymentEnvironmentQuery>(
                "/api/v1/environments/enumerate",
                query ?? new DeploymentEnvironmentQuery(),
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get an environment by ID.
        /// </summary>
        public async Task<DeploymentEnvironment?> GetEnvironmentAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<DeploymentEnvironment>("/api/v1/environments/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create an environment.
        /// </summary>
        public async Task<DeploymentEnvironment?> CreateEnvironmentAsync(
            DeploymentEnvironmentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PostAsync<DeploymentEnvironment, DeploymentEnvironmentUpsertRequest>(
                "/api/v1/environments",
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update an environment.
        /// </summary>
        public async Task<DeploymentEnvironment?> UpdateEnvironmentAsync(
            string id,
            DeploymentEnvironmentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PutAsync<DeploymentEnvironment, DeploymentEnvironmentUpsertRequest>(
                "/api/v1/environments/" + id,
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete an environment.
        /// </summary>
        public async Task DeleteEnvironmentAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/environments/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Deployments

        /// <summary>
        /// List deployments with optional filtering.
        /// </summary>
        public async Task<EnumerationResult<Deployment>?> ListDeploymentsAsync(
            string? vesselId = null,
            string? environmentId = null,
            string? releaseId = null,
            DeploymentStatusEnum? status = null,
            DeploymentVerificationStatusEnum? verificationStatus = null,
            CancellationToken token = default)
        {
            List<string> queryParts = new List<string>();
            if (!String.IsNullOrWhiteSpace(vesselId)) queryParts.Add("vesselId=" + Uri.EscapeDataString(vesselId));
            if (!String.IsNullOrWhiteSpace(environmentId)) queryParts.Add("environmentId=" + Uri.EscapeDataString(environmentId));
            if (!String.IsNullOrWhiteSpace(releaseId)) queryParts.Add("releaseId=" + Uri.EscapeDataString(releaseId));
            if (status.HasValue) queryParts.Add("status=" + Uri.EscapeDataString(status.Value.ToString()));
            if (verificationStatus.HasValue) queryParts.Add("verificationStatus=" + Uri.EscapeDataString(verificationStatus.Value.ToString()));

            string path = "/api/v1/deployments";
            if (queryParts.Count > 0) path += "?" + String.Join("&", queryParts);
            return await GetAsync<EnumerationResult<Deployment>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate deployments with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<Deployment>?> EnumerateDeploymentsAsync(
            DeploymentQuery? query = null,
            CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Deployment>, DeploymentQuery>(
                "/api/v1/deployments/enumerate",
                query ?? new DeploymentQuery(),
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a deployment by ID.
        /// </summary>
        public async Task<Deployment?> GetDeploymentAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Deployment>("/api/v1/deployments/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a deployment.
        /// </summary>
        public async Task<Deployment?> CreateDeploymentAsync(
            DeploymentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PostAsync<Deployment, DeploymentUpsertRequest>(
                "/api/v1/deployments",
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a deployment.
        /// </summary>
        public async Task<Deployment?> UpdateDeploymentAsync(
            string id,
            DeploymentUpsertRequest request,
            CancellationToken token = default)
        {
            return await PutAsync<Deployment, DeploymentUpsertRequest>(
                "/api/v1/deployments/" + id,
                request,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Approve a deployment.
        /// </summary>
        public async Task<Deployment?> ApproveDeploymentAsync(
            string id,
            string? comment = null,
            CancellationToken token = default)
        {
            object body = String.IsNullOrWhiteSpace(comment)
                ? new { }
                : new { Comment = comment };
            return await PostAsync<Deployment>("/api/v1/deployments/" + id + "/approve", body, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Deny a deployment.
        /// </summary>
        public async Task<Deployment?> DenyDeploymentAsync(
            string id,
            string? comment = null,
            CancellationToken token = default)
        {
            object body = String.IsNullOrWhiteSpace(comment)
                ? new { }
                : new { Comment = comment };
            return await PostAsync<Deployment>("/api/v1/deployments/" + id + "/deny", body, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Re-run post-deploy verification for a deployment.
        /// </summary>
        public async Task<Deployment?> VerifyDeploymentAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<Deployment>("/api/v1/deployments/" + id + "/verify", new { }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Roll back a deployment.
        /// </summary>
        public async Task<Deployment?> RollbackDeploymentAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<Deployment>("/api/v1/deployments/" + id + "/rollback", new { }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a deployment.
        /// </summary>
        public async Task DeleteDeploymentAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/deployments/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Captains

        /// <summary>
        /// List all captains.
        /// </summary>
        public async Task<EnumerationResult<Captain>?> ListCaptainsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Captain>>("/api/v1/captains", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate captains with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<Captain>?> EnumerateCaptainsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Captain>, EnumerationQuery>("/api/v1/captains/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a captain by ID.
        /// </summary>
        public async Task<Captain?> GetCaptainAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Captain>("/api/v1/captains/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a captain.
        /// </summary>
        public async Task<Captain?> CreateCaptainAsync(Captain captain, CancellationToken token = default)
        {
            return await PostAsync<Captain, Captain>("/api/v1/captains", captain, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a captain.
        /// </summary>
        public async Task<Captain?> UpdateCaptainAsync(string id, Captain captain, CancellationToken token = default)
        {
            return await PutAsync<Captain, Captain>("/api/v1/captains/" + id, captain, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Stop a captain.
        /// </summary>
        public async Task StopCaptainAsync(string id, CancellationToken token = default)
        {
            await PostAsync("/api/v1/captains/" + id + "/stop", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a captain.
        /// </summary>
        public async Task DeleteCaptainAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/captains/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Missions

        /// <summary>
        /// List missions with optional filters.
        /// </summary>
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
        public async Task<EnumerationResult<Mission>?> EnumerateMissionsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Mission>, EnumerationQuery>("/api/v1/missions/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a mission by ID.
        /// </summary>
        public async Task<Mission?> GetMissionAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Mission>("/api/v1/missions/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Preview landing readiness for a mission.
        /// </summary>
        public async Task<LandingPreviewResult?> GetMissionLandingPreviewAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<LandingPreviewResult>("/api/v1/missions/" + id + "/landing-preview", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a mission.
        /// </summary>
        public async Task<Mission?> CreateMissionAsync(Mission mission, CancellationToken token = default)
        {
            return await PostAsync<Mission, Mission>("/api/v1/missions", mission, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a mission.
        /// </summary>
        public async Task<Mission?> UpdateMissionAsync(string id, Mission mission, CancellationToken token = default)
        {
            return await PutAsync<Mission, Mission>("/api/v1/missions/" + id, mission, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete (cancel) a mission.
        /// </summary>
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
        public async Task<EnumerationResult<Voyage>?> ListVoyagesAsync(string? status = null, CancellationToken token = default)
        {
            string path = "/api/v1/voyages";
            if (!String.IsNullOrEmpty(status)) path += "?status=" + status;
            return await GetAsync<EnumerationResult<Voyage>>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate voyages with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<Voyage>?> EnumerateVoyagesAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Voyage>, EnumerationQuery>("/api/v1/voyages/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a voyage by ID.
        /// </summary>
        public async Task<Voyage?> GetVoyageAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Voyage>("/api/v1/voyages/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a voyage.
        /// </summary>
        public async Task<Voyage?> CreateVoyageAsync(Voyage voyage, CancellationToken token = default)
        {
            return await PostAsync<Voyage, Voyage>("/api/v1/voyages", voyage, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Dispatch a voyage with mission descriptions and optional playbooks.
        /// </summary>
        public async Task<Voyage?> DispatchVoyageAsync(VoyageDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await PostAsync<Voyage, VoyageDispatchRequest>("/api/v1/voyages", request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel a voyage and its pending missions.
        /// </summary>
        public async Task CancelVoyageAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/voyages/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Permanently delete a mission from the database.
        /// </summary>
        public async Task PurgeMissionAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/missions/" + id + "/purge", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Permanently delete a voyage and all its associated missions.
        /// </summary>
        public async Task PurgeVoyageAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/voyages/" + id + "/purge", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete (cancel) a voyage. Kept for backwards compatibility.
        /// </summary>
        public async Task DeleteVoyageAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/voyages/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Signals

        /// <summary>
        /// List signals.
        /// </summary>
        public async Task<EnumerationResult<Signal>?> ListSignalsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Signal>>("/api/v1/signals", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate signals with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<Signal>?> EnumerateSignalsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Signal>, EnumerationQuery>("/api/v1/signals/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a signal.
        /// </summary>
        public async Task<Signal?> CreateSignalAsync(Signal signal, CancellationToken token = default)
        {
            return await PostAsync<Signal, Signal>("/api/v1/signals", signal, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Events

        /// <summary>
        /// List events with optional filters.
        /// </summary>
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
        public async Task<EnumerationResult<ArmadaEvent>?> EnumerateEventsAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<ArmadaEvent>, EnumerationQuery>("/api/v1/events/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-MergeQueue

        /// <summary>
        /// List merge queue entries.
        /// </summary>
        public async Task<EnumerationResult<MergeEntry>?> ListMergeQueueAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<MergeEntry>>("/api/v1/merge-queue", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate merge queue entries with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<MergeEntry>?> EnumerateMergeQueueAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<MergeEntry>, EnumerationQuery>("/api/v1/merge-queue/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a merge queue entry by ID.
        /// </summary>
        public async Task<MergeEntry?> GetMergeEntryAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<MergeEntry>("/api/v1/merge-queue/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enqueue a branch for merge.
        /// </summary>
        public async Task<MergeEntry?> EnqueueMergeAsync(MergeEntry entry, CancellationToken token = default)
        {
            return await PostAsync<MergeEntry, MergeEntry>("/api/v1/merge-queue", entry, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel a merge queue entry.
        /// </summary>
        public async Task CancelMergeAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/merge-queue/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Trigger processing of the merge queue.
        /// </summary>
        public async Task ProcessMergeQueueAsync(CancellationToken token = default)
        {
            await PostAsync("/api/v1/merge-queue/process", token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Playbooks

        /// <summary>
        /// List playbooks.
        /// </summary>
        public async Task<EnumerationResult<Playbook>?> ListPlaybooksAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Playbook>>("/api/v1/playbooks", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate playbooks with pagination and filtering.
        /// </summary>
        public async Task<EnumerationResult<Playbook>?> EnumeratePlaybooksAsync(EnumerationQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Playbook>, EnumerationQuery>("/api/v1/playbooks/enumerate", query ?? new EnumerationQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a playbook by ID.
        /// </summary>
        public async Task<Playbook?> GetPlaybookAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Playbook>("/api/v1/playbooks/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a playbook.
        /// </summary>
        public async Task<Playbook?> CreatePlaybookAsync(Playbook playbook, CancellationToken token = default)
        {
            return await PostAsync<Playbook, Playbook>("/api/v1/playbooks", playbook, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a playbook.
        /// </summary>
        public async Task<Playbook?> UpdatePlaybookAsync(string id, Playbook playbook, CancellationToken token = default)
        {
            return await PutAsync<Playbook, Playbook>("/api/v1/playbooks/" + id, playbook, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a playbook.
        /// </summary>
        public async Task DeletePlaybookAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/playbooks/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-WorkflowProfiles

        /// <summary>
        /// List workflow profiles.
        /// </summary>
        public async Task<EnumerationResult<WorkflowProfile>?> ListWorkflowProfilesAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<WorkflowProfile>>("/api/v1/workflow-profiles", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate workflow profiles with paging and filtering.
        /// </summary>
        public async Task<EnumerationResult<WorkflowProfile>?> EnumerateWorkflowProfilesAsync(WorkflowProfileQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<WorkflowProfile>, WorkflowProfileQuery>("/api/v1/workflow-profiles/enumerate", query ?? new WorkflowProfileQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a workflow profile by ID.
        /// </summary>
        public async Task<WorkflowProfile?> GetWorkflowProfileAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<WorkflowProfile>("/api/v1/workflow-profiles/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Validate a workflow profile definition.
        /// </summary>
        public async Task<WorkflowProfileValidationResult?> ValidateWorkflowProfileAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            return await PostAsync<WorkflowProfileValidationResult, WorkflowProfile>("/api/v1/workflow-profiles/validate", profile, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Preview the fully resolved workflow profile and commands for a vessel.
        /// </summary>
        public async Task<WorkflowProfileResolutionPreviewResult?> PreviewWorkflowProfileForVesselAsync(string vesselId, string? workflowProfileId = null, CancellationToken token = default)
        {
            string path = "/api/v1/workflow-profiles/preview/vessels/" + vesselId;
            if (!String.IsNullOrWhiteSpace(workflowProfileId))
                path += "?workflowProfileId=" + Uri.EscapeDataString(workflowProfileId);
            return await GetAsync<WorkflowProfileResolutionPreviewResult>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve the active workflow profile for a vessel.
        /// </summary>
        public async Task<WorkflowProfile?> ResolveWorkflowProfileAsync(string vesselId, string? workflowProfileId = null, CancellationToken token = default)
        {
            string path = "/api/v1/workflow-profiles/resolve/vessels/" + vesselId;
            if (!String.IsNullOrWhiteSpace(workflowProfileId))
                path += "?workflowProfileId=" + Uri.EscapeDataString(workflowProfileId);
            return await GetAsync<WorkflowProfile>(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a workflow profile.
        /// </summary>
        public async Task<WorkflowProfile?> CreateWorkflowProfileAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            return await PostAsync<WorkflowProfile, WorkflowProfile>("/api/v1/workflow-profiles", profile, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a workflow profile.
        /// </summary>
        public async Task<WorkflowProfile?> UpdateWorkflowProfileAsync(string id, WorkflowProfile profile, CancellationToken token = default)
        {
            return await PutAsync<WorkflowProfile, WorkflowProfile>("/api/v1/workflow-profiles/" + id, profile, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a workflow profile.
        /// </summary>
        public async Task DeleteWorkflowProfileAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/workflow-profiles/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-Releases

        /// <summary>
        /// List releases.
        /// </summary>
        public async Task<EnumerationResult<Release>?> ListReleasesAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<Release>>("/api/v1/releases", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate releases with paging and filtering.
        /// </summary>
        public async Task<EnumerationResult<Release>?> EnumerateReleasesAsync(ReleaseQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<Release>, ReleaseQuery>("/api/v1/releases/enumerate", query ?? new ReleaseQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a release by ID.
        /// </summary>
        public async Task<Release?> GetReleaseAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<Release>("/api/v1/releases/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a release.
        /// </summary>
        public async Task<Release?> CreateReleaseAsync(ReleaseUpsertRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await PostAsync<Release, ReleaseUpsertRequest>("/api/v1/releases", request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a release.
        /// </summary>
        public async Task<Release?> UpdateReleaseAsync(string id, ReleaseUpsertRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await PutAsync<Release, ReleaseUpsertRequest>("/api/v1/releases/" + id, request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Refresh derived release fields.
        /// </summary>
        public async Task<Release?> RefreshReleaseAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<Release>("/api/v1/releases/" + id + "/refresh", new { }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a release.
        /// </summary>
        public async Task DeleteReleaseAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/releases/" + id, token).ConfigureAwait(false);
        }

        #endregion

        #region Public-Methods-CheckRuns

        /// <summary>
        /// List structured check runs.
        /// </summary>
        public async Task<EnumerationResult<CheckRun>?> ListCheckRunsAsync(CancellationToken token = default)
        {
            return await GetAsync<EnumerationResult<CheckRun>>("/api/v1/check-runs", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate structured check runs with paging and filtering.
        /// </summary>
        public async Task<EnumerationResult<CheckRun>?> EnumerateCheckRunsAsync(CheckRunQuery? query = null, CancellationToken token = default)
        {
            return await PostAsync<EnumerationResult<CheckRun>, CheckRunQuery>("/api/v1/check-runs/enumerate", query ?? new CheckRunQuery(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a structured check run by ID.
        /// </summary>
        public async Task<CheckRun?> GetCheckRunAsync(string id, CancellationToken token = default)
        {
            return await GetAsync<CheckRun>("/api/v1/check-runs/" + id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Execute a structured check run.
        /// </summary>
        public async Task<CheckRun?> RunCheckAsync(CheckRunRequest request, CancellationToken token = default)
        {
            return await PostAsync<CheckRun, CheckRunRequest>("/api/v1/check-runs", request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Retry a structured check run.
        /// </summary>
        public async Task<CheckRun?> RetryCheckRunAsync(string id, CancellationToken token = default)
        {
            return await PostAsync<CheckRun>("/api/v1/check-runs/" + id + "/retry", new { }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a structured check run.
        /// </summary>
        public async Task DeleteCheckRunAsync(string id, CancellationToken token = default)
        {
            await DeleteAsync("/api/v1/check-runs/" + id, token).ConfigureAwait(false);
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
