namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end coverage for the Admiral WebSocket surface: the status snapshot handshake,
    /// broadcast events triggered through the REST API, and the command channel that mirrors the
    /// REST fleet/vessel/voyage/mission/captain/signal/merge-queue/enumerate actions. Ported from the
    /// retired automated <c>WebSocketTests</c> suite; each case opens a raw WebSocket to the shared
    /// server obtained through <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class WebSocketSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the end-to-end WebSocket suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("subscribe_check_run_import_broadcasts_check_run_changed", "Subscribe_CheckRunImport_BroadcastsCheckRunChanged", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                int restPort = fx.RestPort;

                using ClientWebSocket ws = await ConnectAsync(fx).ConfigureAwait(false);
                await SubscribeAsync(ws).ConfigureAwait(false);

                string vesselId = await CreateVesselViaRestAsync(authClient, "ws-checkrun-broadcast").ConfigureAwait(false);
                HttpResponseMessage response = await authClient.PostAsync("/api/v1/check-runs/import",
                    JsonHelper.ToJsonContent(new
                    {
                        VesselId = vesselId,
                        Type = "Build",
                        Status = "Passed",
                        Label = "WebSocket Imported Check",
                        Command = "echo imported",
                        Output = "Build succeeded.",
                        Summary = "Imported check passed."
                    })).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                CheckRun created = await JsonHelper.DeserializeAsync<CheckRun>(response).ConfigureAwait(false);

                JsonElement evt = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "check-run.changed"
                        && root.GetProperty("data").GetProperty("id").GetString() == created.Id;
                }).ConfigureAwait(false);

                AssertEqual("check-run.changed", evt.GetProperty("type").GetString());
                AssertEqual(created.Id, evt.GetProperty("data").GetProperty("id").GetString());
                AssertEqual("Passed", evt.GetProperty("data").GetProperty("status").GetString());
            }));

            cases.Add(CaseAsync("subscribe_mission_review_transition_broadcasts_approval_needed", "Subscribe_MissionReviewTransition_BroadcastsApprovalNeeded", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                int restPort = fx.RestPort;

                using ClientWebSocket ws = await ConnectAsync(fx).ConfigureAwait(false);
                await SubscribeAsync(ws).ConfigureAwait(false);

                string missionId = await CreateMissionViaRestAsync(authClient, "ws-approval-needed").ConfigureAwait(false);

                HttpResponseMessage assignedResponse = await authClient.PutAsync(
                    "/api/v1/missions/" + missionId + "/status",
                    JsonHelper.ToJsonContent(new { Status = "Assigned" })).ConfigureAwait(false);
                assignedResponse.EnsureSuccessStatusCode();

                HttpResponseMessage inProgressResponse = await authClient.PutAsync(
                    "/api/v1/missions/" + missionId + "/status",
                    JsonHelper.ToJsonContent(new { Status = "InProgress" })).ConfigureAwait(false);
                inProgressResponse.EnsureSuccessStatusCode();

                HttpResponseMessage reviewResponse = await authClient.PutAsync(
                    "/api/v1/missions/" + missionId + "/status",
                    JsonHelper.ToJsonContent(new { Status = "Review" })).ConfigureAwait(false);
                reviewResponse.EnsureSuccessStatusCode();

                JsonElement evt = await WaitForEventAsync(ws, root =>
                {
                    return root.GetProperty("type").GetString() == "approval-needed"
                        && root.GetProperty("data").GetProperty("missionId").GetString() == missionId;
                }).ConfigureAwait(false);

                AssertEqual("approval-needed", evt.GetProperty("type").GetString());
                AssertEqual(missionId, evt.GetProperty("data").GetProperty("missionId").GetString());
                AssertEqual("mission", evt.GetProperty("data").GetProperty("entityType").GetString());
                AssertEqual("Review", evt.GetProperty("data").GetProperty("status").GetString());
            }));

            cases.Add(CaseAsync("subscribe_backlog_create_broadcasts_objective_changed", "Subscribe_BacklogCreate_BroadcastsObjectiveChanged", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                int restPort = fx.RestPort;

                string objectiveId = String.Empty;

                try
                {
                    using ClientWebSocket ws = await ConnectAsync(fx).ConfigureAwait(false);
                    await SubscribeAsync(ws).ConfigureAwait(false);

                    HttpResponseMessage createResponse = await authClient.PostAsync(
                        "/api/v1/backlog",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "WebSocket backlog coverage",
                            Description = "Broadcast objective.changed for backlog routes."
                        })).ConfigureAwait(false);
                    createResponse.EnsureSuccessStatusCode();
                    Objective created = await JsonHelper.DeserializeAsync<Objective>(createResponse).ConfigureAwait(false);
                    objectiveId = created.Id;

                    JsonElement evt = await WaitForEventAsync(ws, root =>
                    {
                        return root.GetProperty("type").GetString() == "objective.changed"
                            && root.GetProperty("data").GetProperty("id").GetString() == objectiveId;
                    }).ConfigureAwait(false);

                    AssertEqual("objective.changed", evt.GetProperty("type").GetString());
                    AssertEqual(objectiveId, evt.GetProperty("data").GetProperty("id").GetString());
                    AssertEqual("Inbox", evt.GetProperty("data").GetProperty("backlogState").GetString());
                }
                finally
                {
                    if (!String.IsNullOrWhiteSpace(objectiveId))
                        await authClient.DeleteAsync("/api/v1/backlog/" + objectiveId).ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("subscribe_backlog_refinement_lifecycle_broadcasts_refinement_events", "Subscribe_BacklogRefinementLifecycle_BroadcastsRefinementEvents", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                int restPort = fx.RestPort;

                string objectiveId = String.Empty;
                string captainId = String.Empty;
                string sessionId = String.Empty;

                try
                {
                    using ClientWebSocket ws = await ConnectAsync(fx).ConfigureAwait(false);
                    await SubscribeAsync(ws).ConfigureAwait(false);

                    Objective objective = await CreateObjectiveViaRestAsync(authClient, "WebSocket refinement objective").ConfigureAwait(false);
                    objectiveId = objective.Id;

                    Captain captain = await CreateCaptainRecordViaRestAsync(authClient, "ws-refinement-captain").ConfigureAwait(false);
                    captainId = captain.Id;

                    HttpResponseMessage createSessionResponse = await authClient.PostAsync(
                        "/api/v1/backlog/" + objectiveId + "/refinement-sessions",
                        JsonHelper.ToJsonContent(new
                        {
                            CaptainId = captainId,
                            Title = "WebSocket refinement lifecycle"
                        })).ConfigureAwait(false);
                    createSessionResponse.EnsureSuccessStatusCode();
                    ObjectiveRefinementSessionDetail createdSession = await JsonHelper.DeserializeAsync<ObjectiveRefinementSessionDetail>(createSessionResponse).ConfigureAwait(false);
                    sessionId = createdSession.Session.Id;

                    JsonElement createdEvent = await WaitForEventAsync(ws, root =>
                    {
                        return root.GetProperty("type").GetString() == "objective-refinement-session.changed"
                            && root.GetProperty("data").GetProperty("session").GetProperty("id").GetString() == sessionId
                            && root.GetProperty("data").GetProperty("session").GetProperty("status").GetString() == "Active";
                    }).ConfigureAwait(false);
                    AssertEqual(sessionId, createdEvent.GetProperty("data").GetProperty("session").GetProperty("id").GetString());

                    await UpdateCaptainRuntimeAsync(authClient, captainId, "Custom").ConfigureAwait(false);

                    HttpResponseMessage sendResponse = await authClient.PostAsync(
                        "/api/v1/objective-refinement-sessions/" + sessionId + "/messages",
                        JsonHelper.ToJsonContent(new
                        {
                            Content = "Clarify acceptance criteria for the backlog item."
                        })).ConfigureAwait(false);
                    sendResponse.EnsureSuccessStatusCode();

                    JsonElement userMessageEvent = await WaitForEventAsync(ws, root =>
                    {
                        return root.GetProperty("type").GetString() == "objective-refinement-session.message.created"
                            && root.GetProperty("data").GetProperty("sessionId").GetString() == sessionId
                            && root.GetProperty("data").GetProperty("message").GetProperty("role").GetString() == "User";
                    }).ConfigureAwait(false);
                    AssertEqual(sessionId, userMessageEvent.GetProperty("data").GetProperty("sessionId").GetString());

                    JsonElement assistantCreatedEvent = await WaitForEventAsync(ws, root =>
                    {
                        return root.GetProperty("type").GetString() == "objective-refinement-session.message.created"
                            && root.GetProperty("data").GetProperty("sessionId").GetString() == sessionId
                            && root.GetProperty("data").GetProperty("message").GetProperty("role").GetString() == "Assistant";
                    }).ConfigureAwait(false);
                    string assistantMessageId = assistantCreatedEvent.GetProperty("data").GetProperty("message").GetProperty("id").GetString()
                        ?? throw new Exception("Assistant refinement message id not found in WebSocket payload");

                    JsonElement assistantUpdatedEvent = await WaitForEventAsync(ws, root =>
                    {
                        return root.GetProperty("type").GetString() == "objective-refinement-session.message.updated"
                            && root.GetProperty("data").GetProperty("sessionId").GetString() == sessionId
                            && root.GetProperty("data").GetProperty("message").GetProperty("id").GetString() == assistantMessageId
                            && !String.IsNullOrWhiteSpace(root.GetProperty("data").GetProperty("message").GetProperty("content").GetString());
                    }).ConfigureAwait(false);
                    AssertContains("Refinement response failed", assistantUpdatedEvent.GetProperty("data").GetProperty("message").GetProperty("content").GetString() ?? String.Empty);

                    JsonElement activeEvent = await WaitForEventAsync(ws, root =>
                    {
                        return root.GetProperty("type").GetString() == "objective-refinement-session.changed"
                            && root.GetProperty("data").GetProperty("session").GetProperty("id").GetString() == sessionId
                            && root.GetProperty("data").GetProperty("session").GetProperty("status").GetString() == "Active";
                    }).ConfigureAwait(false);
                    AssertEqual(sessionId, activeEvent.GetProperty("data").GetProperty("session").GetProperty("id").GetString());

                    HttpResponseMessage deleteSessionResponse = await authClient.DeleteAsync("/api/v1/objective-refinement-sessions/" + sessionId).ConfigureAwait(false);
                    AssertEqual(System.Net.HttpStatusCode.NoContent, deleteSessionResponse.StatusCode);

                    JsonElement deletedEvent = await WaitForEventAsync(ws, root =>
                    {
                        return root.GetProperty("type").GetString() == "objective-refinement-session.deleted"
                            && root.GetProperty("data").GetProperty("sessionId").GetString() == sessionId;
                    }).ConfigureAwait(false);
                    AssertEqual(sessionId, deletedEvent.GetProperty("data").GetProperty("sessionId").GetString());
                    sessionId = String.Empty;
                }
                finally
                {
                    if (!String.IsNullOrWhiteSpace(sessionId))
                        await authClient.DeleteAsync("/api/v1/objective-refinement-sessions/" + sessionId).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(objectiveId))
                        await authClient.DeleteAsync("/api/v1/backlog/" + objectiveId).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(captainId))
                        await authClient.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("create_vessel_git_hub_token_override_does_not_leak", "CreateVessel_GitHubTokenOverrideDoesNotLeak", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                int restPort = fx.RestPort;

                string token = "ghp_ws_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                JsonElement resp = await WsCommandAsync(fx, "create_vessel", new
                {
                    data = new
                    {
                        Name = "ws-vessel-github-override",
                        RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                        GitHubTokenOverride = token
                    }
                }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                string raw = resp.GetRawText();
                AssertFalse(raw.Contains(token, StringComparison.Ordinal));
                AssertFalse(raw.Contains("\"gitHubTokenOverride\"", StringComparison.Ordinal));
                Vessel data = DeserializeData<Vessel>(resp);
                AssertTrue(data.HasGitHubTokenOverride);
            }));

            cases.Add(CaseAsync("update_vessel_empty_git_hub_token_override_clears_override", "UpdateVessel_EmptyGitHubTokenOverrideClearsOverride", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                int restPort = fx.RestPort;

                JsonElement createResponse = await WsCommandAsync(fx, "create_vessel", new
                {
                    data = new
                    {
                        Name = "ws-clear-github-override",
                        RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                        GitHubTokenOverride = "ghp_ws_clear"
                    }
                }).ConfigureAwait(false);
                Vessel created = DeserializeData<Vessel>(createResponse);
                AssertTrue(created.HasGitHubTokenOverride);

                JsonElement updateResponse = await WsCommandAsync(fx, "update_vessel", new
                {
                    id = created.Id,
                    data = new
                    {
                        Name = "ws-clear-github-override",
                        RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                        GitHubTokenOverride = ""
                    }
                }).ConfigureAwait(false);
                Vessel updated = DeserializeData<Vessel>(updateResponse);
                AssertFalse(updated.HasGitHubTokenOverride);
            }));

            cases.Add(CaseAsync("list_mission_summaries_empty_returns_empty_list", "ListMissionSummaries_Empty_ReturnsEmptyList", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                int restPort = fx.RestPort;

                JsonElement resp = await WsCommandAsync(fx, "list_missions_summary").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_missions_summary", resp.GetProperty("action").GetString());
                EnumerationResult<MissionSummary> data = DeserializeData<EnumerationResult<MissionSummary>>(resp);
                AssertNotNull(data);
            }));

            cases.Add(CaseAsync("list_mission_summaries_with_pagination_respects_page_size", "ListMissionSummaries_WithPagination_RespectsPageSize", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                int restPort = fx.RestPort;

                await CreateMissionViaRestAsync(authClient, "ws-page-summary-m1").ConfigureAwait(false);
                await CreateMissionViaRestAsync(authClient, "ws-page-summary-m2").ConfigureAwait(false);
                await CreateMissionViaRestAsync(authClient, "ws-page-summary-m3").ConfigureAwait(false);

                JsonElement resp = await WsCommandAsync(fx, "list_missions_summary", new { query = new { pageSize = 2 } }).ConfigureAwait(false);
                EnumerationResult<MissionSummary> data = DeserializeData<EnumerationResult<MissionSummary>>(resp);
                AssertEqual(2, data.Objects.Count);
            }));

            // ── Mission Diff/Log and Captain Log ────────────────────────

            // Note: stop_server is not tested in automated suite as it would shut down the server.

            // ── Enumerate ─────────────────────────────────────────────

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "WebSocket Tests",
                cases: cases);
        }

        #endregion

        #region Private-Members

        private const string SuiteId = "E2E.WebSocket";

        #endregion

        #region Private-Methods

        private static async Task<ClientWebSocket> ConnectAsync(E2EServerFixture fx)
        {
            ClientWebSocket ws = new ClientWebSocket();
            Uri uri = new Uri("ws://127.0.0.1:" + fx.RestPort + "/ws");
            await ws.ConnectAsync(uri, CancellationToken.None).ConfigureAwait(false);
            await AuthenticateAsync(ws, fx.ApiKey).ConfigureAwait(false);
            return ws;
        }

        /// <summary>
        /// Authenticate a hub session with the fixture API key. The hub refuses every other route on
        /// an unauthenticated session, so every connection authenticates before it subscribes or sends
        /// a command.
        /// </summary>
        private static async Task AuthenticateAsync(ClientWebSocket ws, string apiKey)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonHelper.Serialize(new { Route = "authenticate", apiKey = apiKey }));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            JsonElement reply = await WaitForEventAsync(ws, root => root.TryGetProperty("type", out JsonElement type)
                && (type.GetString() == "auth.result" || type.GetString() == "auth.error" || type.GetString() == "error")).ConfigureAwait(false);
            if (reply.GetProperty("type").GetString() != "auth.result")
                throw new InvalidOperationException("WebSocket authentication failed: " + reply.GetRawText());
        }

        private static async Task SubscribeAsync(ClientWebSocket ws)
        {
            string payload = JsonHelper.Serialize(new { Route = "subscribe" });
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            await WaitForEventAsync(ws, root => root.GetProperty("type").GetString() == "status.snapshot").ConfigureAwait(false);
        }

        private static async Task<JsonElement> WaitForEventAsync(ClientWebSocket ws, Func<JsonElement, bool> predicate, int timeoutMs = 15000)
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

        /// <summary>
        /// Extract the "data" property from a WsCommandAsync JsonElement result and deserialize it to T.
        /// </summary>
        private static T DeserializeData<T>(JsonElement resp)
        {
            string dataJson = resp.GetProperty("data").GetRawText();
            return JsonHelper.Deserialize<T>(dataJson);
        }

        private static async Task<JsonElement> WsCommandAsync(E2EServerFixture fx, string action, object? extraFields = null)
        {
            using ClientWebSocket ws = await ConnectAsync(fx).ConfigureAwait(false);

            Dictionary<string, object?> msg = new Dictionary<string, object?>
            {
                ["Route"] = "command",
                ["action"] = action
            };

            if (extraFields != null)
            {
                string extraJson = JsonHelper.Serialize(extraFields);
                using JsonDocument extraDoc = JsonDocument.Parse(extraJson);
                foreach (JsonProperty prop in extraDoc.RootElement.EnumerateObject())
                {
                    msg[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                }
            }

            string payload = JsonHelper.Serialize(msg);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

            byte[] buffer = new byte[1048576];
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Loop past broadcast messages (mission.changed, voyage.changed, etc.)
            // until we receive the actual command.result or command.error response.
            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement.Clone();

                if (root.TryGetProperty("type", out JsonElement typeElem))
                {
                    string? type = typeElem.GetString();
                    if (type == "command.result" || type == "command.error")
                        return root;
                }

                // Not a command response — skip and read next message
            }
        }

        private static async Task<string> CreateVesselViaRestAsync(HttpClient authClient, string name)
        {
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/vessels",
                JsonHelper.ToJsonContent(new { Name = name, RepoUrl = TestRepoHelper.GetLocalBareRepoUrl() })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp).ConfigureAwait(false);
            return vessel.Id;
        }

        private static async Task<Captain> CreateCaptainRecordViaRestAsync(HttpClient authClient, string name)
        {
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/captains",
                JsonHelper.ToJsonContent(new { Name = name, Runtime = "ClaudeCode" })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Captain>(resp).ConfigureAwait(false);
        }

        private static async Task<Objective> CreateObjectiveViaRestAsync(HttpClient authClient, string title)
        {
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/backlog",
                JsonHelper.ToJsonContent(new { Title = title, Description = "WebSocket objective coverage" })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Objective>(resp).ConfigureAwait(false);
        }

        private static async Task UpdateCaptainRuntimeAsync(HttpClient authClient, string captainId, string runtime)
        {
            HttpResponseMessage getResp = await authClient.GetAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
            getResp.EnsureSuccessStatusCode();
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(getResp).ConfigureAwait(false);

            HttpResponseMessage updateResp = await authClient.PutAsync(
                "/api/v1/captains/" + captainId,
                JsonHelper.ToJsonContent(new
                {
                    Name = captain.Name,
                    Runtime = runtime
                })).ConfigureAwait(false);
            updateResp.EnsureSuccessStatusCode();
        }

        private static async Task<string> CreateMissionViaRestAsync(HttpClient authClient, string title)
        {
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/missions",
                JsonHelper.ToJsonContent(new { Title = title })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // When mission stays Pending (no captain available), the API returns
            // { "Mission": {...}, "Warning": "..." } instead of the mission directly.
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            if (wrapper.Mission != null)
                return wrapper.Mission.Id;

            // Fallback: response is the mission directly
            Mission mission = JsonHelper.Deserialize<Mission>(body);
            return mission.Id;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
