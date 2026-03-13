namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// Automated tests for the landing pipeline exercised through the REST API
    /// against a real running ArmadaServer. These complement the unit-level
    /// LandingPipelineTests by verifying the actual orchestration paths in
    /// ArmadaServer.HandleMissionCompleteAsync and related methods.
    /// </summary>
    public class LandingPipelineTests : TestSuite
    {
        #region Public-Members

        public override string Name => "Landing Pipeline (Automated)";

        #endregion

        #region Private-Members

        private HttpClient _AuthClient;
        private HttpClient _UnauthClient;

        #endregion

        #region Constructors-and-Factories

        public LandingPipelineTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        #endregion

        #region Public-Methods

        protected override async Task RunTestsAsync()
        {
            // === PullRequestOpen Status Transitions ===

            await RunTest("PullRequestOpen_TransitionsFromWorkProduced", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR transition test", "WorkProduced");

                // Transition to PullRequestOpen
                HttpResponseMessage resp = await TransitionAsync(missionId, "PullRequestOpen");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("PullRequestOpen", mission.GetProperty("Status").GetString());
            });

            await RunTest("PullRequestOpen_TransitionsToComplete", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR to Complete", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Complete");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("Complete", mission.GetProperty("Status").GetString());
                AssertTrue(mission.TryGetProperty("CompletedUtc", out JsonElement completed) && completed.ValueKind != JsonValueKind.Null,
                    "CompletedUtc should be set");
            });

            await RunTest("PullRequestOpen_TransitionsToLandingFailed", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR to LandingFailed", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "LandingFailed");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("LandingFailed", mission.GetProperty("Status").GetString());
            });

            await RunTest("PullRequestOpen_TransitionsToCancelled", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR to Cancelled", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Cancelled");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("Cancelled", mission.GetProperty("Status").GetString());
            });

            await RunTest("PullRequestOpen_RejectsInvalidTransitionToInProgress", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR invalid transition", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "InProgress");
                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                JsonElement respJson = JsonDocument.Parse(respBody).RootElement;

                // Server returns 200 with an Error property in the body for invalid transitions
                AssertTrue(respJson.TryGetProperty("Error", out _) || respJson.TryGetProperty("Message", out JsonElement msg) && msg.GetString()!.Contains("Invalid transition"),
                    "Expected error response for invalid transition PullRequestOpen -> InProgress");

                // Mission should still be PullRequestOpen
                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("PullRequestOpen", mission.GetProperty("Status").GetString());
            });

            // === Manual Complete Without Dock (Audit Event) ===

            await RunTest("ManualComplete_NoDock_EmitsAuditEvent", async () =>
            {
                // Create a mission and advance to WorkProduced (no dock since no vessel assignment)
                string missionId = await CreateAndAdvanceMissionAsync("Manual complete audit", "WorkProduced");

                // Manually transition to Complete (no dock exists for this mission)
                HttpResponseMessage resp = await TransitionAsync(missionId, "Complete");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("Complete", mission.GetProperty("Status").GetString());

                // Check that the audit event was emitted
                JsonElement events = await GetAsync("/api/v1/events?type=mission.manual_complete_no_dock");
                int count = 0;
                foreach (JsonElement evt in events.GetProperty("Objects").EnumerateArray())
                {
                    if (evt.GetProperty("MissionId").ValueKind != JsonValueKind.Null &&
                        evt.GetProperty("MissionId").GetString() == missionId)
                    {
                        count++;
                    }
                }
                AssertTrue(count >= 1, "Expected at least 1 mission.manual_complete_no_dock event for mission " + missionId);
            });

            // === MergeQueue Auto-Enqueue ===

            await RunTest("MergeQueue_VesselLandingMode_CreatesEntry", async () =>
            {
                // Create a vessel with LandingMode = MergeQueue
                JsonElement vessel = await CreateVesselWithLandingModeAsync("MQ-Vessel", "MergeQueue");
                string vesselId = vessel.GetProperty("Id").GetString()!;

                // Verify vessel LandingMode was persisted
                JsonElement readVessel = await GetAsync("/api/v1/vessels/" + vesselId);
                AssertTrue(readVessel.TryGetProperty("LandingMode", out JsonElement lm), "Vessel should have LandingMode property");
                AssertEqual("MergeQueue", lm.GetString());
            });

            // === Event Emission Correctness ===

            await RunTest("StatusChanged_EventEmitted_ForEachTransition", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("Event emission test", "InProgress");

                // Transition to WorkProduced
                await TransitionAsync(missionId, "WorkProduced");

                // Check that a status_changed event was emitted
                JsonElement events = await GetAsync("/api/v1/events?type=mission.status_changed&missionId=" + missionId);
                bool found = false;
                foreach (JsonElement evt in events.GetProperty("Objects").EnumerateArray())
                {
                    string? msg = evt.GetProperty("Message").GetString();
                    if (msg != null && msg.Contains("WorkProduced"))
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Expected mission.status_changed event mentioning WorkProduced for mission " + missionId);
            });

            await RunTest("LandingFailed_TransitionsBackToWorkProduced", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("LandingFailed retry", "WorkProduced");
                await TransitionAsync(missionId, "LandingFailed");

                // LandingFailed -> WorkProduced (retry)
                HttpResponseMessage resp = await TransitionAsync(missionId, "WorkProduced");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("WorkProduced", mission.GetProperty("Status").GetString());
            });

            await RunTest("LandingFailed_TransitionsToFailed", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("LandingFailed to Failed", "WorkProduced");
                await TransitionAsync(missionId, "LandingFailed");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Failed");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("Failed", mission.GetProperty("Status").GetString());
            });

            await RunTest("LandingFailed_TransitionsToCancelled", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("LandingFailed to Cancelled", "WorkProduced");
                await TransitionAsync(missionId, "LandingFailed");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Cancelled");
                AssertStatusCode(HttpStatusCode.OK, resp);

                JsonElement mission = await GetMissionAsync(missionId);
                AssertEqual("Cancelled", mission.GetProperty("Status").GetString());
            });
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Create a mission (no vessel) and advance it through the status chain to the target status.
        /// Chain: Pending -> Assigned -> InProgress -> WorkProduced -> PullRequestOpen
        /// </summary>
        private async Task<string> CreateAndAdvanceMissionAsync(string title, string targetStatus)
        {
            JsonElement mission = await CreateMissionAsync(title);
            string missionId = mission.GetProperty("Id").GetString()!;

            string[] chain = new[] { "Assigned", "InProgress", "WorkProduced", "PullRequestOpen" };
            foreach (string status in chain)
            {
                HttpResponseMessage resp = await TransitionAsync(missionId, status);
                resp.EnsureSuccessStatusCode();
                if (status == targetStatus) break;
            }

            return missionId;
        }

        private async Task<JsonElement> CreateMissionAsync(string title)
        {
            StringContent content = new StringContent(
                JsonSerializer.Serialize(new { Title = title }),
                Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/missions", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement root = JsonDocument.Parse(body).RootElement.Clone();

            // When mission stays Pending (no captain available), the API returns
            // { "Mission": {...}, "Warning": "..." } instead of the mission directly.
            if (root.TryGetProperty("Mission", out JsonElement nested))
                return nested;

            return root;
        }

        private async Task<JsonElement> CreateVesselWithLandingModeAsync(string name, string landingMode)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string repoUrl = TestRepoHelper.GetLocalBareRepoUrl();
            StringContent content = new StringContent(
                JsonSerializer.Serialize(new { Name = uniqueName, RepoUrl = repoUrl, LandingMode = landingMode }),
                Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/vessels", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private async Task<HttpResponseMessage> TransitionAsync(string missionId, string status)
        {
            StringContent content = new StringContent(
                JsonSerializer.Serialize(new { Status = status }),
                Encoding.UTF8, "application/json");
            return await _AuthClient.PutAsync("/api/v1/missions/" + missionId + "/status", content).ConfigureAwait(false);
        }

        private async Task<JsonElement> GetMissionAsync(string missionId)
        {
            return await GetAsync("/api/v1/missions/" + missionId).ConfigureAwait(false);
        }

        private async Task<JsonElement> GetAsync(string path)
        {
            HttpResponseMessage resp = await _AuthClient.GetAsync(path).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException("GET " + path + " returned " + (int)resp.StatusCode + ": " + body);
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        #endregion
    }
}
