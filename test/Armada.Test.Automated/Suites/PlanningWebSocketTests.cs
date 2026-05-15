namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class PlanningWebSocketTests : TestSuite
    {
        public override string Name => "Planning WebSocket Tests";

        private readonly HttpClient _AuthClient;
        private readonly int _RestPort;
        private readonly List<string> _CreatedCaptainIds = new List<string>();
        private readonly List<string> _CreatedVesselIds = new List<string>();
        private readonly List<string> _CreatedFleetIds = new List<string>();
        private readonly List<string> _CreatedPlanningSessionIds = new List<string>();
        private readonly List<string> _CreatedVoyageIds = new List<string>();

        public PlanningWebSocketTests(HttpClient authClient, HttpClient unauthClient, int restPort, string apiKey)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _ = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _ = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _RestPort = restPort;
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("CreatePlanningSession_BroadcastsChangedPayload", async () =>
            {
                using ClientWebSocket ws = await ConnectAsync().ConfigureAwait(false);
                await SubscribeAsync(ws).ConfigureAwait(false);

                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Captain captain = await CreateCaptainAsync("planning-ws-create").ConfigureAwait(false);

                PlanningSessionDetailResponse created = await CreatePlanningSessionAsync(captain.Id, vesselId, "WebSocket planning session").ConfigureAwait(false);
                _CreatedPlanningSessionIds.Add(created.Session!.Id);

                JsonElement evt = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "planning-session.changed"
                        && root.GetProperty("data").GetProperty("session").GetProperty("id").GetString() == created.Session.Id;
                }).ConfigureAwait(false);

                AssertEqual("planning-session.changed", evt.GetProperty("type").GetString());
                AssertTrue(evt.TryGetProperty("timestamp", out _), "Planning session changed payload should contain timestamp");
                JsonElement session = evt.GetProperty("data").GetProperty("session");
                AssertEqual(created.Session.Id, session.GetProperty("id").GetString());
                AssertEqual("WebSocket planning session", session.GetProperty("title").GetString());
            }).ConfigureAwait(false);

            await RunTest("PlanningSessionLifecycle_BroadcastsTranscriptSummaryDispatchAndDeleteEvents", async () =>
            {
                using ClientWebSocket ws = await ConnectAsync().ConfigureAwait(false);
                await SubscribeAsync(ws).ConfigureAwait(false);

                string fleetId = await CreateFleetAsync().ConfigureAwait(false);
                string vesselId = await CreateVesselAsync(fleetId).ConfigureAwait(false);
                Captain captain = await CreateCaptainAsync("planning-ws-lifecycle").ConfigureAwait(false);

                PlanningSessionDetailResponse created = await CreatePlanningSessionAsync(captain.Id, vesselId, "Planning WebSocket lifecycle").ConfigureAwait(false);
                _CreatedPlanningSessionIds.Add(created.Session!.Id);

                await UpdateCaptainRuntimeAsync(captain.Id, "Custom").ConfigureAwait(false);

                await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions/" + created.Session.Id + "/messages",
                    JsonHelper.ToJsonContent(new
                    {
                        Content = "Lay out a migration plan and convert it into a dispatch draft."
                    })).ConfigureAwait(false);

                JsonElement createdUser = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "planning-session.message.created"
                        && root.GetProperty("data").GetProperty("sessionId").GetString() == created.Session.Id
                        && root.GetProperty("data").GetProperty("message").GetProperty("role").GetString() == "User";
                }).ConfigureAwait(false);
                AssertEqual(created.Session.Id, createdUser.GetProperty("data").GetProperty("sessionId").GetString());

                JsonElement createdAssistant = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "planning-session.message.created"
                        && root.GetProperty("data").GetProperty("sessionId").GetString() == created.Session.Id
                        && root.GetProperty("data").GetProperty("message").GetProperty("role").GetString() == "Assistant";
                }).ConfigureAwait(false);
                string assistantMessageId = createdAssistant.GetProperty("data").GetProperty("message").GetProperty("id").GetString()
                    ?? throw new Exception("Assistant planning message id not found in WebSocket payload");

                JsonElement updatedAssistant = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "planning-session.message.updated"
                        && root.GetProperty("data").GetProperty("sessionId").GetString() == created.Session.Id
                        && root.GetProperty("data").GetProperty("message").GetProperty("id").GetString() == assistantMessageId
                        && !String.IsNullOrWhiteSpace(root.GetProperty("data").GetProperty("message").GetProperty("content").GetString());
                }).ConfigureAwait(false);
                AssertTrue(updatedAssistant.TryGetProperty("timestamp", out _), "Planning session message update should contain timestamp");

                HttpResponseMessage summarizeResp = await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions/" + created.Session.Id + "/summarize",
                    JsonHelper.ToJsonContent(new
                    {
                        MessageId = assistantMessageId,
                        Title = "WebSocket summary draft"
                    })).ConfigureAwait(false);
                summarizeResp.EnsureSuccessStatusCode();

                JsonElement summaryEvent = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "planning-session.summary.created"
                        && root.GetProperty("data").GetProperty("sessionId").GetString() == created.Session.Id;
                }).ConfigureAwait(false);
                JsonElement draft = summaryEvent.GetProperty("data").GetProperty("draft");
                AssertEqual("assistant-fallback", draft.GetProperty("method").GetString());
                AssertTrue(!String.IsNullOrWhiteSpace(draft.GetProperty("title").GetString()), "Summary event should contain a title");
                AssertTrue(!String.IsNullOrWhiteSpace(draft.GetProperty("description").GetString()), "Summary event should contain a description");

                HttpResponseMessage dispatchResp = await _AuthClient.PostAsync(
                    "/api/v1/planning-sessions/" + created.Session.Id + "/dispatch",
                    JsonHelper.ToJsonContent(new
                    {
                        MessageId = assistantMessageId,
                        Description = draft.GetProperty("description").GetString()
                    })).ConfigureAwait(false);
                dispatchResp.EnsureSuccessStatusCode();
                Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(dispatchResp).ConfigureAwait(false);
                _CreatedVoyageIds.Add(voyage.Id);

                JsonElement dispatchEvent = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "planning-session.dispatch.created"
                        && root.GetProperty("data").GetProperty("sessionId").GetString() == created.Session.Id;
                }).ConfigureAwait(false);
                AssertEqual(voyage.Id, dispatchEvent.GetProperty("data").GetProperty("voyageId").GetString());
                AssertEqual(assistantMessageId, dispatchEvent.GetProperty("data").GetProperty("messageId").GetString());

                HttpResponseMessage deleteResp = await _AuthClient.DeleteAsync("/api/v1/planning-sessions/" + created.Session.Id).ConfigureAwait(false);
                AssertEqual(System.Net.HttpStatusCode.NoContent, deleteResp.StatusCode);
                _CreatedPlanningSessionIds.Remove(created.Session.Id);

                JsonElement deletedEvent = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "planning-session.deleted"
                        && root.GetProperty("data").GetProperty("sessionId").GetString() == created.Session.Id;
                }).ConfigureAwait(false);
                AssertEqual(created.Session.Id, deletedEvent.GetProperty("data").GetProperty("sessionId").GetString());
            }).ConfigureAwait(false);

            await CleanupAsync().ConfigureAwait(false);
        }

        private async Task<ClientWebSocket> ConnectAsync()
        {
            ClientWebSocket ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + _RestPort + "/ws"), CancellationToken.None).ConfigureAwait(false);
            return ws;
        }

        private async Task SubscribeAsync(ClientWebSocket ws)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonHelper.Serialize(new { Route = "subscribe" }));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            await WaitForEventAsync(ws, root => root.GetProperty("type").GetString() == "status.snapshot").ConfigureAwait(false);
        }

        private async Task<JsonElement> WaitForEventAsync(ClientWebSocket ws, Func<JsonElement, bool> predicate, int timeoutMs = 15000)
        {
            byte[] buffer = new byte[1048576];
            using CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);

            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement.Clone();
                if (predicate(root))
                    return root;
            }
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

        private async Task<string> CreateFleetAsync()
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync(
                "/api/v1/fleets",
                JsonHelper.ToJsonContent(new
                {
                    Name = "PlanningWsFleet-" + Guid.NewGuid().ToString("N").Substring(0, 8)
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
                    Name = "PlanningWsVessel-" + Guid.NewGuid().ToString("N").Substring(0, 8),
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
