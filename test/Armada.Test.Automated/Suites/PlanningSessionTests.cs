namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class PlanningSessionTests : TestSuite
    {
        public override string Name => "Planning Session API Tests";

        private readonly HttpClient _AuthClient;
        private readonly List<string> _CreatedCaptainIds = new List<string>();
        private readonly List<string> _CreatedVesselIds = new List<string>();
        private readonly List<string> _CreatedFleetIds = new List<string>();
        private readonly List<string> _CreatedVoyageIds = new List<string>();
        private readonly List<string> _CreatedPlanningSessionIds = new List<string>();

        public PlanningSessionTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _ = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateGetAndListPlanningSession_ReturnsDetail", async () =>
            {
                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Captain captain = await CreateCaptainAsync("planning-api-create").ConfigureAwait(false);

                HttpResponseMessage createResp = await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = "API planning session",
                        CaptainId = captain.Id,
                        VesselId = vesselId
                    })).ConfigureAwait(false);

                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                PlanningSessionDetailResponse created = await JsonHelper.DeserializeAsync<PlanningSessionDetailResponse>(createResp).ConfigureAwait(false);
                AssertNotNull(created.Session);
                AssertEqual("API planning session", created.Session!.Title);
                AssertEqual(captain.Id, created.Session.CaptainId);
                AssertEqual(vesselId, created.Session.VesselId);
                AssertNotNull(created.Captain);
                AssertNotNull(created.Vessel);
                AssertTrue(created.Messages.Count == 0);
                _CreatedPlanningSessionIds.Add(created.Session.Id);

                HttpResponseMessage getResp = await _AuthClient.GetAsync("/api/v1/planning-sessions/" + created.Session.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, getResp.StatusCode);
                PlanningSessionDetailResponse fetched = await JsonHelper.DeserializeAsync<PlanningSessionDetailResponse>(getResp).ConfigureAwait(false);
                AssertNotNull(fetched.Session);
                AssertEqual(created.Session.Id, fetched.Session!.Id);

                HttpResponseMessage listResp = await _AuthClient.GetAsync("/api/v1/planning-sessions").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, listResp.StatusCode);
                List<PlanningSession> sessions = await JsonHelper.DeserializeAsync<List<PlanningSession>>(listResp).ConfigureAwait(false);
                AssertTrue(sessions.Any(session => session.Id == created.Session.Id), "Created planning session should appear in the list");
            }).ConfigureAwait(false);

            await RunTest("PlanningSessionLifecycle_SendSummarizeDispatchStopDelete_Works", async () =>
            {
                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Captain captain = await CreateCaptainAsync("planning-api-lifecycle").ConfigureAwait(false);

                PlanningSessionDetailResponse created = await CreatePlanningSessionAsync(captain.Id, vesselId, "Lifecycle session").ConfigureAwait(false);
                _CreatedPlanningSessionIds.Add(created.Session!.Id);

                // Force a deterministic assistant failure path so this test does not depend on any external CLI.
                await UpdateCaptainRuntimeAsync(captain.Id, "Custom").ConfigureAwait(false);

                HttpResponseMessage sendResp = await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions/" + created.Session.Id + "/messages",
                    JsonHelper.ToJsonContent(new
                    {
                        Content = "Produce a release plan for the local dashboard."
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, sendResp.StatusCode);

                PlanningSessionDetailResponse withAssistant = await WaitForAssistantOutputAsync(created.Session.Id).ConfigureAwait(false);
                PlanningSessionMessage? assistant = withAssistant.Messages
                    .Where(message => String.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(message => message.Sequence)
                    .FirstOrDefault(message => !String.IsNullOrWhiteSpace(message.Content));

                AssertNotNull(assistant);
                AssertTrue(!String.IsNullOrWhiteSpace(assistant!.Content), "Assistant output should be available for summarize and dispatch");

                HttpResponseMessage summarizeResp = await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions/" + created.Session.Id + "/summarize",
                    JsonHelper.ToJsonContent(new
                    {
                        MessageId = assistant.Id,
                        Title = "Lifecycle dispatch draft"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, summarizeResp.StatusCode);
                PlanningSessionSummaryResponse summary = await JsonHelper.DeserializeAsync<PlanningSessionSummaryResponse>(summarizeResp).ConfigureAwait(false);
                AssertEqual(created.Session.Id, summary.SessionId);
                AssertEqual(assistant.Id, summary.MessageId);
                AssertEqual("assistant-fallback", summary.Method);
                AssertTrue(!String.IsNullOrWhiteSpace(summary.Title), "Summary title should not be empty");
                AssertTrue(!String.IsNullOrWhiteSpace(summary.Description), "Summary description should not be empty");

                HttpResponseMessage dispatchResp = await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions/" + created.Session.Id + "/dispatch",
                    JsonHelper.ToJsonContent(new
                    {
                        MessageId = assistant.Id,
                        Title = summary.Title,
                        Description = summary.Description
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, dispatchResp.StatusCode);
                Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(dispatchResp).ConfigureAwait(false);
                _CreatedVoyageIds.Add(voyage.Id);
                AssertEqual(created.Session.Id, voyage.SourcePlanningSessionId);
                AssertEqual(assistant.Id, voyage.SourcePlanningMessageId);

                HttpResponseMessage stopResp = await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions/" + created.Session.Id + "/stop",
                    JsonHelper.ToJsonContent(new { })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, stopResp.StatusCode);
                PlanningSessionDetailResponse stopping = await JsonHelper.DeserializeAsync<PlanningSessionDetailResponse>(stopResp).ConfigureAwait(false);
                AssertNotNull(stopping.Session);
                AssertTrue(
                    stopping.Session!.Status.ToString() == "Stopping" || stopping.Session.Status.ToString() == "Stopped",
                    "Stop should return a stopping or stopped session state");

                PlanningSessionDetailResponse stopped = await WaitForSessionStatusAsync(created.Session.Id, "Stopped").ConfigureAwait(false);
                AssertEqual("Stopped", stopped.Session!.Status.ToString());

                HttpResponseMessage deleteResp = await _AuthClient.DeleteAsync("/api/v1/planning-sessions/" + created.Session.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, deleteResp.StatusCode);
                _CreatedPlanningSessionIds.Remove(created.Session.Id);

                HttpResponseMessage getDeletedResp = await _AuthClient.GetAsync("/api/v1/planning-sessions/" + created.Session.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, getDeletedResp.StatusCode);
            }).ConfigureAwait(false);

            await CleanupAsync().ConfigureAwait(false);
        }

        private async Task<PlanningSessionDetailResponse> CreatePlanningSessionAsync(string captainId, string vesselId, string title)
        {
            HttpResponseMessage createResp = await _AuthClient.PostAsync(
                "/api/v1/planning-sessions",
                JsonHelper.ToJsonContent(new
                {
                    Title = title,
                    CaptainId = captainId,
                    VesselId = vesselId
                })).ConfigureAwait(false);
            createResp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<PlanningSessionDetailResponse>(createResp).ConfigureAwait(false);
        }

        private async Task<PlanningSessionDetailResponse> WaitForAssistantOutputAsync(string sessionId, int timeoutMs = 15000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                HttpResponseMessage resp = await _AuthClient.GetAsync("/api/v1/planning-sessions/" + sessionId).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                PlanningSessionDetailResponse detail = await JsonHelper.DeserializeAsync<PlanningSessionDetailResponse>(resp).ConfigureAwait(false);
                if (detail.Messages.Any(message =>
                    String.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase) &&
                    !String.IsNullOrWhiteSpace(message.Content)))
                {
                    return detail;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            throw new Exception("Timed out waiting for assistant planning output for session " + sessionId);
        }

        private async Task<PlanningSessionDetailResponse> WaitForSessionStatusAsync(string sessionId, string expectedStatus, int timeoutMs = 15000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                HttpResponseMessage resp = await _AuthClient.GetAsync("/api/v1/planning-sessions/" + sessionId).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                PlanningSessionDetailResponse detail = await JsonHelper.DeserializeAsync<PlanningSessionDetailResponse>(resp).ConfigureAwait(false);
                if (String.Equals(detail.Session?.Status.ToString(), expectedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    return detail;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            throw new Exception("Timed out waiting for planning session " + sessionId + " to reach status " + expectedStatus);
        }

        private async Task<string> CreateFleetAsync()
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync(
                "/api/v1/fleets",
                JsonHelper.ToJsonContent(new
                {
                    Name = "PlanningFleet-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp).ConfigureAwait(false);
            _CreatedFleetIds.Add(fleet.Id);
            return fleet.Id;
        }

        private async Task<string> CreateVesselAsync(string fleetId)
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync(
                "/api/v1/vessels",
                JsonHelper.ToJsonContent(new
                {
                    Name = "PlanningVessel-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                    FleetId = fleetId
                })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp).ConfigureAwait(false);
            _CreatedVesselIds.Add(vessel.Id);
            return vessel.Id;
        }

        private async Task<Captain> CreateCaptainAsync(string prefix, string runtime = "ClaudeCode")
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync(
                "/api/v1/captains",
                JsonHelper.ToJsonContent(new
                {
                    Name = prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Runtime = runtime
                })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(resp).ConfigureAwait(false);
            _CreatedCaptainIds.Add(captain.Id);
            return captain;
        }

        private async Task UpdateCaptainRuntimeAsync(string captainId, string runtime)
        {
            HttpResponseMessage getResp = await _AuthClient.GetAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
            getResp.EnsureSuccessStatusCode();
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(getResp).ConfigureAwait(false);

            HttpResponseMessage updateResp = await _AuthClient.PutAsync(
                "/api/v1/captains/" + captainId,
                JsonHelper.ToJsonContent(new
                {
                    Name = captain.Name,
                    Runtime = runtime
                })).ConfigureAwait(false);
            updateResp.EnsureSuccessStatusCode();
        }

        private async Task CleanupAsync()
        {
            foreach (string planningSessionId in _CreatedPlanningSessionIds.ToArray())
            {
                try
                {
                    await _AuthClient.PostAsync("/api/v1/planning-sessions/" + planningSessionId + "/stop", JsonHelper.ToJsonContent(new { })).ConfigureAwait(false);
                }
                catch
                {
                }

                try
                {
                    await _AuthClient.DeleteAsync("/api/v1/planning-sessions/" + planningSessionId).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            foreach (string voyageId in _CreatedVoyageIds)
            {
                try
                {
                    await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId + "/purge").ConfigureAwait(false);
                }
                catch
                {
                }
            }

            foreach (string captainId in _CreatedCaptainIds)
            {
                try
                {
                    await _AuthClient.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            foreach (string vesselId in _CreatedVesselIds)
            {
                try
                {
                    await _AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            foreach (string fleetId in _CreatedFleetIds)
            {
                try
                {
                    await _AuthClient.DeleteAsync("/api/v1/fleets/" + fleetId).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private sealed class PlanningSessionDetailResponse
        {
            public PlanningSession? Session { get; set; }
            public List<PlanningSessionMessage> Messages { get; set; } = new List<PlanningSessionMessage>();
            public Captain? Captain { get; set; }
            public Vessel? Vessel { get; set; }
        }
    }
}
