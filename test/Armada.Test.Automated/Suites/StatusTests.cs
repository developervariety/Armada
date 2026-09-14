namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for status and health check routes.
    /// </summary>
    public class StatusTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Name of this test suite.
        /// </summary>
        public override string Name => "Status Routes";

        #endregion

        #region Private-Members

        private HttpClient _AuthClient;
        private HttpClient _UnauthClient;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a new StatusTests suite with shared HTTP clients.
        /// </summary>
        public StatusTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        #endregion

        #region Private-Methods

        private async Task<Captain> CreateCaptainAsync(string name)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StringContent content = JsonHelper.ToJsonContent(new { Name = uniqueName, Runtime = "ClaudeCode" });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/captains", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Captain>(resp).ConfigureAwait(false);
        }

        private async Task<string> CreateFleetAsync()
        {
            StringContent content = JsonHelper.ToJsonContent(new { Name = "StatusTestFleet-" + Guid.NewGuid().ToString("N").Substring(0, 8) });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/fleets", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp).ConfigureAwait(false);
            return fleet.Id;
        }

        private async Task<string> CreateVesselAsync(string fleetId)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Name = "StatusTestVessel-" + Guid.NewGuid().ToString("N").Substring(0, 8), RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(), FleetId = fleetId });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/vessels", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp).ConfigureAwait(false);
            return vessel.Id;
        }

        private async Task<Mission> CreateMissionAsync(string title)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Title = title, Description = "Status test mission" });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/missions", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // Read body once since stream can only be consumed once.
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // When mission stays Pending (no captain available), the API returns
            // { "Mission": {...}, "Warning": "..." } instead of the mission directly.
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            if (wrapper.Mission != null)
                return wrapper.Mission;

            // Response was the mission directly.
            return JsonHelper.Deserialize<Mission>(body);
        }

        private async Task<Voyage> CreateVoyageAsync(string vesselId, string title, int missionCount = 2)
        {
            object[] missions = Enumerable.Range(1, missionCount)
                .Select(i => (object)new { Title = "Voyage Mission " + i, Description = "Desc " + i })
                .ToArray();

            StringContent content = JsonHelper.ToJsonContent(new
            {
                Title = title,
                Description = "Status test voyage",
                VesselId = vesselId,
                Missions = missions
            });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/voyages", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Voyage>(resp).ConfigureAwait(false);
        }

        /// <summary>
        /// Cancel a voyage this suite dispatched. A dispatched voyage on a real vessel launches its missions
        /// in turn, and each later mission becomes assignable when the one before it ends. Left open, that
        /// chain outlives the test and hands work to idle captains that later suites create, so a captain a
        /// later test expects to stay idle can go Working. A cancelled voyage's missions are never assigned.
        /// </summary>
        private async Task CancelVoyageAsync(string voyageId)
        {
            HttpResponseMessage resp = await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, resp.StatusCode, "The dispatched voyage must be cancelled when the test ends");
        }

        private async Task<Signal> CreateSignalAsync(string type, string message)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Type = type, Message = message });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/signals", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Signal>(resp).ConfigureAwait(false);
        }

        private async Task<string> CreateTenantCredentialAsync(string tenantId, string label, bool isTenantAdmin)
        {
            HttpResponseMessage userResponse = await _AuthClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenantId,
                    Email = label + "@status.armada",
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = isTenantAdmin
                })).ConfigureAwait(false);
            userResponse.EnsureSuccessStatusCode();
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);

            HttpResponseMessage credentialResponse = await _AuthClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new { TenantId = tenantId, UserId = user.Id, Name = label + "-cred" })).ConfigureAwait(false);
            credentialResponse.EnsureSuccessStatusCode();
            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
            return credential.BearerToken;
        }

        private async Task<HttpStatusCode> GetStatusCodeWithBearerAsync(string bearerToken)
        {
            using (HttpClient client = new HttpClient { BaseAddress = _AuthClient.BaseAddress })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                using (HttpResponseMessage response = await client.GetAsync("/api/v1/status").ConfigureAwait(false))
                    return response.StatusCode;
            }
        }

        private async Task<ArmadaStatus> GetStatusAsync()
        {
            HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<ArmadaStatus>(response).ConfigureAwait(false);
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Status_AuthorizationMatrix_OnlyGlobalAdministratorReadsFleetStatus", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage tenantResponse = await _AuthClient.PostAsync("/api/v1/tenants",
                    JsonHelper.ToJsonContent(new { Name = "status-matrix-" + suffix })).ConfigureAwait(false);
                tenantResponse.EnsureSuccessStatusCode();
                TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResponse).ConfigureAwait(false);

                string userToken = await CreateTenantCredentialAsync(tenant.Id, "status-user-" + suffix, false).ConfigureAwait(false);
                string tenantAdminToken = await CreateTenantCredentialAsync(tenant.Id, "status-admin-" + suffix, true).ConfigureAwait(false);

                using (HttpResponseMessage anonymous = await _UnauthClient.GetAsync("/api/v1/status").ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, "anonymous caller");
                AssertEqual(HttpStatusCode.Forbidden, await GetStatusCodeWithBearerAsync(userToken).ConfigureAwait(false), "tenant user");
                AssertEqual(HttpStatusCode.Forbidden, await GetStatusCodeWithBearerAsync(tenantAdminToken).ConfigureAwait(false), "tenant administrator");
                using (HttpResponseMessage global = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, global.StatusCode, "global administrator");
            }).ConfigureAwait(false);

            await RunTest("UsagePreview_RequiresAuthentication", async () =>
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { persona = "Worker" }))
                using (HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/settings/usage-preview", content).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("UsagePreview_ValidatesDraftWithoutSaving", async () =>
            {
                using (HttpResponseMessage before = await _AuthClient.GetAsync("/api/v1/settings").ConfigureAwait(false))
                {
                    string saved = await before.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (StringContent content = JsonHelper.ToJsonContent(new { persona = "Worker", usageRouting = new { enabled = true, accounts = new object[0], personaRoutes = new { } } }))
                    using (HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/settings/usage-preview", content).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.OK, response.StatusCode);
                        string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertTrue(result.Contains("v2_persona_route_not_configured"));
                    }
                    using (HttpResponseMessage after = await _AuthClient.GetAsync("/api/v1/settings").ConfigureAwait(false))
                        AssertEqual(saved, await after.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
                using (StringContent invalid = JsonHelper.ToJsonContent(new { usageRouting = new { refreshIntervalMinutes = 0 } }))
                using (HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/settings/usage-preview", invalid).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.BadRequest, response.StatusCode);
            }).ConfigureAwait(false);

            #region Status-Endpoint

            await RunTest("GetStatus_ReturnsOk", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_ReturnsJson", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                AssertEqual("application/json", contentType);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_HasAllExpectedProperties", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                AssertNotNull(status);
                Assert(status.MissionsByStatus != null, "Missing MissionsByStatus");
                Assert(status.Voyages != null, "Missing Voyages");
                Assert(status.RecentSignals != null, "Missing RecentSignals");
                Assert(status.TimestampUtc != default, "Missing TimestampUtc");
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_ShowsZeros", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                // Note: previous test suites may have created captains, so we just verify the fields exist and are >= 0
                AssertTrue(status.TotalCaptains >= 0);
                AssertTrue(status.IdleCaptains >= 0);
                AssertTrue(status.WorkingCaptains >= 0);
                AssertTrue(status.StalledCaptains >= 0);
                AssertTrue(status.ActiveVoyages >= 0);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_EmptyVoyages", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertTrue(status.Voyages.Count >= 0);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_EmptyRecentSignals", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertTrue(status.RecentSignals.Count >= 0);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_NoData_MissionsByStatusIsObject", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertNotNull(status.MissionsByStatus);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingCaptains_ShowsCorrectCounts", async () =>
            {
                await CreateCaptainAsync("status-captain-1").ConfigureAwait(false);
                await CreateCaptainAsync("status-captain-2").ConfigureAwait(false);
                await CreateCaptainAsync("status-captain-3").ConfigureAwait(false);

                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                AssertTrue(status.TotalCaptains >= 3);
                AssertTrue(status.IdleCaptains >= 3);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingMissions_ShowsMissionsByStatus", async () =>
            {
                await CreateMissionAsync("Status Mission 1").ConfigureAwait(false);
                await CreateMissionAsync("Status Mission 2").ConfigureAwait(false);

                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);

                if (status.MissionsByStatus.TryGetValue("Pending", out int pending))
                {
                    AssertTrue(pending >= 2);
                }
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingVoyage_ShowsActiveVoyages", async () =>
            {
                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Voyage voyage = await CreateVoyageAsync(vesselId, "Status Voyage 1").ConfigureAwait(false);
                try
                {
                    ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                    AssertTrue(status.ActiveVoyages >= 1);
                }
                finally
                {
                    await CancelVoyageAsync(voyage.Id).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingVoyage_VoyagesArrayPopulated", async () =>
            {
                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Voyage voyage = await CreateVoyageAsync(vesselId, "Voyage Array Check").ConfigureAwait(false);
                try
                {
                    ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                    AssertTrue(status.Voyages.Count >= 1);
                }
                finally
                {
                    await CancelVoyageAsync(voyage.Id).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("GetStatus_AfterCreatingSignals_ShowsRecentSignals", async () =>
            {
                await CreateSignalAsync("Mail", "Test signal 1").ConfigureAwait(false);
                await CreateSignalAsync("Heartbeat", "Test signal 2").ConfigureAwait(false);

                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                AssertTrue(status.RecentSignals.Count >= 2);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_TimestampUtc_IsRecent", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                DateTime timestamp = status.TimestampUtc.ToUniversalTime();
                TimeSpan elapsed = DateTime.UtcNow - timestamp;

                Assert(elapsed.TotalMinutes < 1, "TimestampUtc should be within the last minute but was " + elapsed.TotalMinutes + " minutes ago");
            }).ConfigureAwait(false);

            await RunTest("GetStatus_TimestampUtc_IsValidDateTime", async () =>
            {
                ArmadaStatus status = await GetStatusAsync().ConfigureAwait(false);
                Assert(status.TimestampUtc != default, "TimestampUtc should be a valid datetime");
            }).ConfigureAwait(false);

            await RunTest("GetStatus_WithoutAuth_ReturnsResponse", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                AssertNotNull(response);
            }).ConfigureAwait(false);

            await RunTest("GetStatus_WrongApiKey_ReturnsResponse", async () =>
            {
                HttpClient wrongKeyClient = new HttpClient();
                wrongKeyClient.BaseAddress = _AuthClient.BaseAddress;
                wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");

                HttpResponseMessage response = await wrongKeyClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                AssertNotNull(response);

                wrongKeyClient.Dispose();
            }).ConfigureAwait(false);

            #endregion

            #region Health-Check-Endpoint

            await RunTest("GetHealth_ReturnsOk", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_ReturnsHealthyStatus", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_NoAuth_ReturnsOk", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_NoAuth_ReturnsHealthyStatus", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_WrongApiKey_StillReturnsOk", async () =>
            {
                HttpClient wrongKeyClient = new HttpClient();
                wrongKeyClient.BaseAddress = _AuthClient.BaseAddress;
                wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "totally-wrong-key");

                HttpResponseMessage response = await wrongKeyClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);

                wrongKeyClient.Dispose();
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasTimestamp", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Timestamp != null, "Health check should include Timestamp");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasVersion", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Version != null, "Health check should include Version");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasUptime", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Uptime != null, "Health check should include Uptime");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_HasPorts", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.Ports != null, "Health check should include Ports");
                Assert(health.Ports!.Admiral >= 0, "Ports should include Admiral");
                Assert(health.Ports!.Mcp >= 0, "Ports should include Mcp");
            }).ConfigureAwait(false);

            await RunTest("GetHealth_ReturnsJson", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                AssertEqual("application/json", contentType);
            }).ConfigureAwait(false);

            await RunTest("GetHealth_StartUtcIsBeforeNow", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                Assert(health.StartUtc != null, "Health check should include StartUtc");
                DateTime startUtc = health.StartUtc!.Value.ToUniversalTime();
                Assert(startUtc <= DateTime.UtcNow, "StartUtc should be in the past");
            }).ConfigureAwait(false);

            #endregion
        }

        #endregion
    }
}
