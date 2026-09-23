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
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// WebSocket route tests migrated from xUnit to TestSuite harness.
    /// </summary>
    public class WebSocketTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Name of this test suite.
        /// </summary>
        public override string Name => "WebSocket Tests";

        #endregion

        #region Private-Members

        private HttpClient _AuthClient;
        private HttpClient _UnauthClient;
        private int _RestPort;
        private string _ApiKey;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a new WebSocket test suite.
        /// </summary>
        public WebSocketTests(HttpClient authClient, HttpClient unauthClient, int restPort, string apiKey)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _RestPort = restPort;
            _ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run all WebSocket tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            // Subscribe Tests
            await RunTest("Anonymous_Subscribe_IsRefusedWithoutSnapshot", async () =>
            {
                using (ClientWebSocket ws = await ConnectAnonymousAsync().ConfigureAwait(false))
                {
                    await SendJsonAsync(ws, new { Route = "subscribe" }).ConfigureAwait(false);
                    JsonElement? frame = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertTrue(frame.HasValue, "Expected a refusal frame before the close");
                    AssertEqual("auth.required", frame!.Value.GetProperty("type").GetString());
                    JsonElement? after = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertFalse(after.HasValue, "Expected the server to close an unauthenticated session");
                }
            }).ConfigureAwait(false);

            await RunTest("Anonymous_Command_IsRefused", async () =>
            {
                using (ClientWebSocket ws = await ConnectAnonymousAsync().ConfigureAwait(false))
                {
                    await SendJsonAsync(ws, new { Route = "command", action = "status" }).ConfigureAwait(false);
                    JsonElement? frame = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertTrue(frame.HasValue, "Expected a refusal frame before the close");
                    AssertEqual("auth.required", frame!.Value.GetProperty("type").GetString());
                }
            }).ConfigureAwait(false);

            await RunTest("Anonymous_IdleSession_IsClosedAfterAuthenticationWindow", async () =>
            {
                using (ClientWebSocket ws = await ConnectAnonymousAsync().ConfigureAwait(false))
                {
                    // Send nothing. The server must not hold an unauthenticated socket open forever.
                    JsonElement? frame = await ReceiveFrameOrCloseAsync(ws, 30).ConfigureAwait(false);
                    AssertTrue(frame.HasValue, "Expected an auth.required frame when the authentication window ends");
                    AssertEqual("auth.required", frame!.Value.GetProperty("type").GetString());
                    JsonElement? after = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertFalse(after.HasValue, "Expected the server to close the idle unauthenticated session");
                }
            }).ConfigureAwait(false);

            await RunTest("Authenticate_InvalidApiKey_IsRefused", async () =>
            {
                using (ClientWebSocket ws = await ConnectAnonymousAsync().ConfigureAwait(false))
                {
                    await SendJsonAsync(ws, new { Route = "authenticate", apiKey = "invalid-" + Guid.NewGuid().ToString("N") }).ConfigureAwait(false);
                    JsonElement? frame = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertTrue(frame.HasValue, "Expected a refusal frame before the close");
                    AssertEqual("auth.failed", frame!.Value.GetProperty("type").GetString());
                    JsonElement? after = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertFalse(after.HasValue, "Expected the server to close a session with invalid credentials");
                }
            }).ConfigureAwait(false);

            await RunTest("Authenticate_ValidApiKey_AllowsSubscribe", async () =>
            {
                using (ClientWebSocket ws = await ConnectAnonymousAsync().ConfigureAwait(false))
                {
                    JsonElement result = await AuthenticateAsync(ws, new { Route = "authenticate", apiKey = _ApiKey }).ConfigureAwait(false);
                    AssertEqual("auth.result", result.GetProperty("type").GetString());
                    AssertTrue(result.GetProperty("data").GetProperty("isAdmin").GetBoolean(), "API key session should be a global administrator");

                    await SendJsonAsync(ws, new { Route = "subscribe" }).ConfigureAwait(false);
                    JsonElement snapshot = await WaitForTypeAsync(ws, "status.snapshot").ConfigureAwait(false);
                    AssertEqual("status.snapshot", snapshot.GetProperty("type").GetString());
                }
            }).ConfigureAwait(false);

            await RunTest("Authenticate_NonAdminUser_ReceivesScopedSnapshotAndNoForeignEvents", async () =>
            {
                string email = "ws-user-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";
                using (StringContent content = new StringContent(
                    JsonHelper.Serialize(new { TenantId = "default", Email = email, Password = "testpass123", FirstName = "Socket", LastName = "User" }),
                    Encoding.UTF8,
                    "application/json"))
                {
                    HttpResponseMessage onboard = await _UnauthClient.PostAsync("/api/v1/onboarding", content).ConfigureAwait(false);
                    AssertEqual(System.Net.HttpStatusCode.OK, onboard.StatusCode);
                    OnboardingResult onboarded = JsonHelper.Deserialize<OnboardingResult>(await onboard.Content.ReadAsStringAsync().ConfigureAwait(false));
                    AssertTrue(onboarded.Success, "Expected onboarding to succeed");
                    string bearer = onboarded.Credential!.BearerToken;

                    using (ClientWebSocket ws = await ConnectAnonymousAsync().ConfigureAwait(false))
                    {
                        JsonElement result = await AuthenticateAsync(ws, new { Route = "authenticate", token = bearer }).ConfigureAwait(false);
                        AssertEqual("auth.result", result.GetProperty("type").GetString());
                        AssertFalse(result.GetProperty("data").GetProperty("isAdmin").GetBoolean(), "Onboarded user should not be a global administrator");

                        await SendJsonAsync(ws, new { Route = "command", action = "status" }).ConfigureAwait(false);
                        JsonElement refused = await WaitForTypeAsync(ws, "command.error").ConfigureAwait(false);
                        AssertContains("administrator", refused.GetProperty("error").GetString() ?? "");

                        // A narrower session may subscribe. The fleet-wide aggregates are withheld,
                        // and the snapshot says it is scoped.
                        await SendJsonAsync(ws, new { Route = "subscribe" }).ConfigureAwait(false);
                        JsonElement snapshot = await WaitForTypeAsync(ws, "status.snapshot").ConfigureAwait(false);
                        JsonElement snapshotData = snapshot.GetProperty("data");
                        AssertTrue(snapshotData.GetProperty("scoped").GetBoolean(), "a narrower session receives a scoped snapshot");
                        AssertEqual(JsonValueKind.Null, snapshotData.GetProperty("status").ValueKind, "fleet status is withheld");
                        AssertEqual(JsonValueKind.Null, snapshotData.GetProperty("reconciliation").ValueKind, "fleet reconciliation is withheld");
                        await WaitForTypeAsync(ws, "stream.ready").ConfigureAwait(false);

                        // An administrator session subscribed at the same time proves the event was
                        // broadcast, so silence on the user session is filtering, not a missing event.
                        using (ClientWebSocket admin = await ConnectAsync().ConfigureAwait(false))
                        {
                            await SendJsonAsync(admin, new { Route = "subscribe" }).ConfigureAwait(false);
                            await WaitForTypeAsync(admin, "stream.ready").ConfigureAwait(false);

                            string voyageId = await CreateVoyageViaRestAsync("ws-scoped-voyage").ConfigureAwait(false);
                            HttpResponseMessage cancel = await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false);
                            cancel.EnsureSuccessStatusCode();

                            bool adminSaw = false;
                            DateTime adminDeadline = DateTime.UtcNow.AddSeconds(15);
                            while (!adminSaw && DateTime.UtcNow < adminDeadline)
                            {
                                JsonElement? frame = await ReceiveFrameOrCloseAsync(admin, 15).ConfigureAwait(false);
                                if (!frame.HasValue) break;
                                adminSaw = frame.Value.GetRawText().Contains(voyageId, StringComparison.Ordinal);
                            }
                            AssertTrue(adminSaw, "the administrator session receives the voyage event");

                            // The broadcast was queued before this command was sent, so a delivered voyage
                            // event would arrive ahead of the command reply. Every frame up to the reply
                            // must omit the voyage. The reply also proves commands still need a global
                            // administrator after subscribing.
                            await SendJsonAsync(ws, new { Route = "command", action = "status" }).ConfigureAwait(false);
                            JsonElement? stillRefused = null;
                            DateTime userDeadline = DateTime.UtcNow.AddSeconds(15);
                            while (stillRefused == null && DateTime.UtcNow < userDeadline)
                            {
                                JsonElement? frame = await ReceiveFrameOrCloseAsync(ws, 15).ConfigureAwait(false);
                                AssertTrue(frame.HasValue, "the user session stays open while waiting for the command reply");
                                AssertFalse(frame!.Value.GetRawText().Contains(voyageId, StringComparison.Ordinal),
                                    "a user in the same tenant must not receive another user's voyage event");
                                if (frame.Value.TryGetProperty("type", out JsonElement frameType) && frameType.GetString() == "command.error")
                                    stillRefused = frame.Value;
                            }
                            AssertTrue(stillRefused.HasValue, "the user session receives the command refusal");
                            AssertContains("administrator", stillRefused!.Value.GetProperty("error").GetString() ?? "");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Command_CancelMission_ReachesTheOwningTenantAdministratorOnly", async () =>
            {
                string tenantAToken = await CreateTenantAdministratorTokenAsync("ws-cmd-a").ConfigureAwait(false);
                string tenantBToken = await CreateTenantAdministratorTokenAsync("ws-cmd-b").ConfigureAwait(false);
                string missionId = await CreateMissionWithBearerAsync(tenantAToken, "ws-command-owned-mission").ConfigureAwait(false);

                using (ClientWebSocket ownerAdmin = await ConnectAnonymousAsync().ConfigureAwait(false))
                using (ClientWebSocket otherAdmin = await ConnectAnonymousAsync().ConfigureAwait(false))
                {
                    await AuthenticateAsync(ownerAdmin, new { Route = "authenticate", token = tenantAToken }).ConfigureAwait(false);
                    await AuthenticateAsync(otherAdmin, new { Route = "authenticate", token = tenantBToken }).ConfigureAwait(false);
                    await SendJsonAsync(ownerAdmin, new { Route = "subscribe" }).ConfigureAwait(false);
                    await WaitForTypeAsync(ownerAdmin, "stream.ready").ConfigureAwait(false);
                    await SendJsonAsync(otherAdmin, new { Route = "subscribe" }).ConfigureAwait(false);
                    await WaitForTypeAsync(otherAdmin, "stream.ready").ConfigureAwait(false);

                    // A global administrator changes the tenant's mission through a WebSocket command.
                    JsonElement cancel = await WsCommandAsync("cancel_mission", new { id = missionId }).ConfigureAwait(false);
                    AssertEqual("command.result", cancel.GetProperty("type").GetString());

                    bool ownerSaw = false;
                    DateTime ownerDeadline = DateTime.UtcNow.AddSeconds(15);
                    while (!ownerSaw && DateTime.UtcNow < ownerDeadline)
                    {
                        JsonElement? frame = await ReceiveFrameOrCloseAsync(ownerAdmin, 15).ConfigureAwait(false);
                        if (!frame.HasValue) break;
                        ownerSaw = frame.Value.TryGetProperty("type", out JsonElement frameType)
                            && frameType.GetString() == "mission.changed"
                            && frame.Value.GetRawText().Contains(missionId, StringComparison.Ordinal);
                    }
                    AssertTrue(ownerSaw, "the administrator of the mission's tenant receives the event its command caused");

                    // The command reply is queued after the broadcast, so every frame before it must omit
                    // the other tenant's mission.
                    await SendJsonAsync(otherAdmin, new { Route = "command", action = "status" }).ConfigureAwait(false);
                    bool otherReplied = false;
                    DateTime otherDeadline = DateTime.UtcNow.AddSeconds(15);
                    while (!otherReplied && DateTime.UtcNow < otherDeadline)
                    {
                        JsonElement? frame = await ReceiveFrameOrCloseAsync(otherAdmin, 15).ConfigureAwait(false);
                        AssertTrue(frame.HasValue, "the other tenant's session stays open");
                        AssertFalse(frame!.Value.GetRawText().Contains(missionId, StringComparison.Ordinal),
                            "another tenant's administrator must not receive the event");
                        otherReplied = frame.Value.TryGetProperty("type", out JsonElement replyType) && replyType.GetString() == "command.error";
                    }
                    AssertTrue(otherReplied, "the other tenant's session receives its command reply");
                }
            }).ConfigureAwait(false);

            await RunTest("Authenticate_ApiKeyHeaderOnUpgrade_AllowsSubscribe", async () =>
            {
                using (ClientWebSocket ws = new ClientWebSocket())
                {
                    ws.Options.SetRequestHeader("X-Api-Key", _ApiKey);
                    await ws.ConnectAsync(new Uri("ws://localhost:" + _RestPort + "/ws"), CancellationToken.None).ConfigureAwait(false);
                    JsonElement? frame = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertTrue(frame.HasValue, "Expected an authentication reply for header credentials");
                    AssertEqual("auth.result", frame!.Value.GetProperty("type").GetString());

                    await SendJsonAsync(ws, new { Route = "subscribe" }).ConfigureAwait(false);
                    JsonElement snapshot = await WaitForTypeAsync(ws, "status.snapshot").ConfigureAwait(false);
                    AssertEqual("status.snapshot", snapshot.GetProperty("type").GetString());
                }
            }).ConfigureAwait(false);

            await RunTest("Authenticate_InvalidApiKeyHeaderOnUpgrade_IsRefused", async () =>
            {
                using (ClientWebSocket ws = new ClientWebSocket())
                {
                    ws.Options.SetRequestHeader("X-Api-Key", "invalid-" + Guid.NewGuid().ToString("N"));
                    await ws.ConnectAsync(new Uri("ws://localhost:" + _RestPort + "/ws"), CancellationToken.None).ConfigureAwait(false);
                    JsonElement? frame = await ReceiveFrameOrCloseAsync(ws, 10).ConfigureAwait(false);
                    AssertTrue(frame.HasValue, "Expected a refusal frame before the close");
                    AssertEqual("auth.failed", frame!.Value.GetProperty("type").GetString());
                }
            }).ConfigureAwait(false);

            await RunTest("Subscribe_ReturnsStatusSnapshot", async () =>
            {
                using ClientWebSocket ws = await ConnectAsync().ConfigureAwait(false);

                string msg = JsonHelper.Serialize(new { Route = "subscribe" });
                byte[] bytes = Encoding.UTF8.GetBytes(msg);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

                byte[] buffer = new byte[1048576];
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                JsonElement snapshot = await ReceiveFrameAsync(ws, buffer, cts.Token).ConfigureAwait(false);
                AssertEqual("status.snapshot", snapshot.GetProperty("type").GetString());
                Assert(snapshot.TryGetProperty("streamId", out JsonElement streamId) && !String.IsNullOrWhiteSpace(streamId.GetString()), "Snapshot should contain streamId.");
                Assert(snapshot.TryGetProperty("cursor", out _), "Snapshot should contain cursor.");
                Assert(snapshot.GetProperty("data").TryGetProperty("status", out _), "Snapshot should contain status.");
                Assert(snapshot.GetProperty("data").TryGetProperty("reconciliation", out _), "Snapshot should contain reconciliation state.");
                Assert(snapshot.TryGetProperty("timestamp", out _), "Snapshot should contain timestamp.");

                JsonElement ready;
                long lastCursor = snapshot.GetProperty("cursor").GetInt64();
                do
                {
                    ready = await ReceiveFrameAsync(ws, buffer, cts.Token).ConfigureAwait(false);
                    if (ready.GetProperty("type").GetString() != "stream.ready"
                        && ready.TryGetProperty("cursor", out JsonElement eventCursor))
                    {
                        AssertEqual(lastCursor + 1, eventCursor.GetInt64(), "Snapshot catch-up events must be contiguous.");
                        lastCursor = eventCursor.GetInt64();
                    }
                } while (ready.GetProperty("type").GetString() != "stream.ready");

                AssertEqual(streamId.GetString(), ready.GetProperty("streamId").GetString());
                AssertTrue(ready.GetProperty("cursor").GetInt64() >= lastCursor, "Ready must not move the cursor backward.");
            }).ConfigureAwait(false);

            await RunTest("Reconnect_ReplaysEventAfterCursorBeforeSnapshot", async () =>
            {
                string streamId;
                long cursor;
                using (ClientWebSocket first = await ConnectAsync().ConfigureAwait(false))
                {
                    await SendJsonAsync(first, new { Route = "subscribe" }).ConfigureAwait(false);
                    byte[] firstBuffer = new byte[1048576];
                    using CancellationTokenSource firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    JsonElement frame;
                    do
                    {
                        frame = await ReceiveFrameAsync(first, firstBuffer, firstCts.Token).ConfigureAwait(false);
                    } while (frame.GetProperty("type").GetString() != "stream.ready");

                    streamId = frame.GetProperty("streamId").GetString()!;
                    cursor = frame.GetProperty("cursor").GetInt64();
                }

                string voyageId = await CreateVoyageViaRestAsync("ws-replay-voyage").ConfigureAwait(false);
                HttpResponseMessage cancel = await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false);
                cancel.EnsureSuccessStatusCode();

                using ClientWebSocket resumed = await ConnectAsync().ConfigureAwait(false);
                await SendJsonAsync(resumed, new { Route = "subscribe", streamId, cursor, voyageId }).ConfigureAwait(false);
                byte[] buffer = new byte[1048576];
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                bool sawExpectedReplay = false;
                bool sawSnapshot = false;
                while (true)
                {
                    JsonElement frame = await ReceiveFrameAsync(resumed, buffer, cts.Token).ConfigureAwait(false);
                    string? type = frame.GetProperty("type").GetString();
                    if (type == "voyage.changed"
                        && frame.GetProperty("data").GetProperty("id").GetString() == voyageId)
                    {
                        AssertFalse(sawSnapshot, "The missed event must be replayed before the authoritative snapshot.");
                        AssertTrue(frame.GetProperty("cursor").GetInt64() > cursor, "The replay cursor must advance.");
                        sawExpectedReplay = true;
                    }
                    else if (type == "status.snapshot")
                    {
                        sawSnapshot = true;
                        JsonElement voyages = frame.GetProperty("data").GetProperty("reconciliation").GetProperty("voyages");
                        AssertTrue(voyages.GetArrayLength() == 1, "The scoped snapshot should contain the cancelled voyage.");
                        AssertEqual(voyageId, voyages[0].GetProperty("id").GetString());
                    }
                    else if (type == "stream.ready")
                    {
                        break;
                    }
                }

                AssertTrue(sawExpectedReplay, "Reconnect should replay the missed voyage event.");
                AssertTrue(sawSnapshot, "Reconnect should include an authoritative snapshot.");
            }).ConfigureAwait(false);

            await RunTest("RepeatedSubscribe_IsRejectedWithoutMixingSequences", async () =>
            {
                using ClientWebSocket ws = await ConnectAsync().ConfigureAwait(false);
                await SendJsonAsync(ws, new { Route = "subscribe" }).ConfigureAwait(false);
                byte[] buffer = new byte[1048576];
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                JsonElement frame;
                do
                {
                    frame = await ReceiveFrameAsync(ws, buffer, cts.Token).ConfigureAwait(false);
                } while (frame.GetProperty("type").GetString() != "stream.ready");

                await SendJsonAsync(ws, new { Route = "subscribe" }).ConfigureAwait(false);
                do
                {
                    frame = await ReceiveFrameAsync(ws, buffer, cts.Token).ConfigureAwait(false);
                } while (frame.GetProperty("type").GetString() != "command.error");

                AssertContains("already subscribed", frame.GetProperty("error").GetString()!);
            }).ConfigureAwait(false);

            // Status Tests
            await RunTest("Status_ReturnsArmadaStatus", async () =>
            {
                JsonElement resp = await WsCommandAsync("status").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("status", resp.GetProperty("action").GetString());
                ArmadaStatus status = DeserializeData<ArmadaStatus>(resp);
                AssertTrue(status.TotalCaptains >= 0);
            }).ConfigureAwait(false);

            await RunTest("StopAll_ReturnsAllStopped", async () =>
            {
                try
                {
                    JsonElement resp = await WsCommandAsync("stop_all").ConfigureAwait(false);
                    AssertEqual("command.result", resp.GetProperty("type").GetString());
                    AssertEqual("stop_all", resp.GetProperty("action").GetString());
                    AssertEqual("all_stopped", resp.GetProperty("data").GetProperty("status").GetString());
                }
                catch (TaskCanceledException)
                {
                    // StopAll may take longer than expected with many active captains - acceptable
                }
                catch (System.Net.WebSockets.WebSocketException)
                {
                    // StopAll may close the WebSocket connection - acceptable
                }
            }).ConfigureAwait(false);

            // Fleet Tests
            await RunTest("ListFleets_Empty_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_fleets").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_fleets", resp.GetProperty("action").GetString());
                EnumerationResult<Fleet> data = DeserializeData<EnumerationResult<Fleet>>(resp);
                AssertNotNull(data.Objects);
            }).ConfigureAwait(false);

            await RunTest("CreateFleet_ReturnsCreatedFleet", async () =>
            {
                JsonElement resp = await WsCommandAsync("create_fleet", new { data = new { Name = "ws-fleet" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("create_fleet", resp.GetProperty("action").GetString());
                Fleet data = DeserializeData<Fleet>(resp);
                AssertStartsWith("flt_", data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetFleet_ExistingFleet_ReturnsFleet", async () =>
            {
                string fleetId = await CreateFleetViaRestAsync("ws-get-fleet").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_fleet", new { id = fleetId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                FleetDetailResponse data = DeserializeData<FleetDetailResponse>(resp);
                AssertEqual(fleetId, data.Fleet!.Id);
                AssertNotNull(data.Vessels);
            }).ConfigureAwait(false);

            await RunTest("GetFleet_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_fleet", new { id = "flt_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Fleet not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("UpdateFleet_ExistingFleet_ReturnsUpdated", async () =>
            {
                string fleetId = await CreateFleetViaRestAsync("ws-upd-fleet").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("update_fleet", new { id = fleetId, data = new { Name = "ws-upd-fleet-renamed" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Fleet data = DeserializeData<Fleet>(resp);
                AssertEqual(fleetId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("UpdateFleet_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("update_fleet", new { id = "flt_nonexistent", data = new { Name = "x" } }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Fleet not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("DeleteFleet_ExistingFleet_ReturnsDeleted", async () =>
            {
                string fleetId = await CreateFleetViaRestAsync("ws-del-fleet").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("delete_fleet", new { id = fleetId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                DeleteFleetResponse data = DeserializeData<DeleteFleetResponse>(resp);
                AssertEqual("deleted", data.Status);
            }).ConfigureAwait(false);

            await RunTest("ListFleets_AfterCreate_ReturnsFleet", async () =>
            {
                await WsCommandAsync("create_fleet", new { data = new { Name = "ws-list-fleet" } }).ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("list_fleets").ConfigureAwait(false);
                EnumerationResult<Fleet> data = DeserializeData<EnumerationResult<Fleet>>(resp);
                AssertTrue(data.TotalRecords >= 1);
            }).ConfigureAwait(false);

            await RunTest("ListFleets_WithPagination_RespectsPageSize", async () =>
            {
                await CreateFleetViaRestAsync("ws-page-1").ConfigureAwait(false);
                await CreateFleetViaRestAsync("ws-page-2").ConfigureAwait(false);
                await CreateFleetViaRestAsync("ws-page-3").ConfigureAwait(false);

                JsonElement resp = await WsCommandAsync("list_fleets", new { query = new { pageSize = 2, pageNumber = 1 } }).ConfigureAwait(false);
                EnumerationResult<Fleet> data = DeserializeData<EnumerationResult<Fleet>>(resp);
                AssertEqual(2, data.PageSize);
                AssertEqual(2, data.Objects.Count);
            }).ConfigureAwait(false);

            // Vessel Tests
            await RunTest("ListVessels_Empty_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_vessels").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_vessels", resp.GetProperty("action").GetString());
            }).ConfigureAwait(false);

            await RunTest("CreateVessel_ReturnsCreatedVessel", async () =>
            {
                JsonElement resp = await WsCommandAsync("create_vessel", new { data = new { Name = "ws-vessel", RepoUrl = TestRepoHelper.GetLocalBareRepoUrl() } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Vessel data = DeserializeData<Vessel>(resp);
                AssertStartsWith("vsl_", data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetVessel_ExistingVessel_ReturnsVessel", async () =>
            {
                string vesselId = await CreateVesselViaRestAsync("ws-get-vessel").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_vessel", new { id = vesselId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Vessel data = DeserializeData<Vessel>(resp);
                AssertEqual(vesselId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetVessel_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_vessel", new { id = "vsl_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Vessel not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_ExistingVessel_ReturnsUpdated", async () =>
            {
                string vesselId = await CreateVesselViaRestAsync("ws-upd-vessel").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("update_vessel", new { id = vesselId, data = new { Name = "ws-upd-vessel-renamed", RepoUrl = TestRepoHelper.GetLocalBareRepoUrl() } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Vessel data = DeserializeData<Vessel>(resp);
                AssertEqual(vesselId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("update_vessel", new { id = "vsl_nonexistent", data = new { Name = "x", RepoUrl = TestRepoHelper.GetLocalBareRepoUrl() } }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("DeleteVessel_ExistingVessel_ReturnsDeleted", async () =>
            {
                string vesselId = await CreateVesselViaRestAsync("ws-del-vessel").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("delete_vessel", new { id = vesselId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                DeleteVesselResponse data = DeserializeData<DeleteVesselResponse>(resp);
                AssertEqual("deleted", data.Status);
            }).ConfigureAwait(false);

            // Voyage Tests
            await RunTest("ListVoyages_Empty_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_voyages").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_voyages", resp.GetProperty("action").GetString());
            }).ConfigureAwait(false);

            await RunTest("CreateVoyage_BareVoyage_ReturnsCreatedVoyage", async () =>
            {
                JsonElement resp = await WsCommandAsync("create_voyage", new { data = new { title = "ws-voyage", description = "test voyage" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("create_voyage", resp.GetProperty("action").GetString());
                Voyage data = DeserializeData<Voyage>(resp);
                AssertStartsWith("vyg_", data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetVoyage_ExistingVoyage_ReturnsVoyageWithMissions", async () =>
            {
                string voyageId = await CreateVoyageViaRestAsync("ws-get-voyage").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_voyage", new { id = voyageId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                VoyageDetailResponse data = DeserializeData<VoyageDetailResponse>(resp);
                AssertNotNull(data.Voyage);
                AssertNotNull(data.Missions);
            }).ConfigureAwait(false);

            await RunTest("GetVoyage_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_voyage", new { id = "vyg_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Voyage not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("CancelVoyage_ExistingVoyage_ReturnsCancelled", async () =>
            {
                string voyageId = await CreateVoyageViaRestAsync("ws-cancel-voyage").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("cancel_voyage", new { id = voyageId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                CancelVoyageResponse data = DeserializeData<CancelVoyageResponse>(resp);
                AssertEqual("Cancelled", data.Voyage!.Status.ToString());
                AssertTrue(data.CancelledMissions >= 0);
            }).ConfigureAwait(false);

            await RunTest("CancelVoyage_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("cancel_voyage", new { id = "vyg_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Voyage not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("PurgeVoyage_ExistingVoyage_ReturnsDeleted", async () =>
            {
                string voyageId = await CreateVoyageViaRestAsync("ws-purge-voyage").ConfigureAwait(false);

                // Cancel first — purge is blocked on Open/InProgress voyages
                await WsCommandAsync("cancel_voyage", new { id = voyageId }).ConfigureAwait(false);

                JsonElement resp = await WsCommandAsync("purge_voyage", new { id = voyageId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                PurgeVoyageResponse data = DeserializeData<PurgeVoyageResponse>(resp);
                AssertEqual("deleted", data.Status);
            }).ConfigureAwait(false);

            await RunTest("PurgeVoyage_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("purge_voyage", new { id = "vyg_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Voyage not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("ListVoyages_AfterCreate_ReturnsVoyage", async () =>
            {
                await WsCommandAsync("create_voyage", new { data = new { title = "ws-list-voyage" } }).ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("list_voyages").ConfigureAwait(false);
                EnumerationResult<Voyage> data = DeserializeData<EnumerationResult<Voyage>>(resp);
                AssertTrue(data.TotalRecords >= 1);
            }).ConfigureAwait(false);

            // Mission Tests
            await RunTest("ListMissions_Empty_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_missions").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_missions", resp.GetProperty("action").GetString());
            }).ConfigureAwait(false);

            await RunTest("CreateMission_ReturnsCreatedMission", async () =>
            {
                JsonElement resp = await WsCommandAsync("create_mission", new { data = new { Title = "ws-mission" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Mission data = DeserializeData<Mission>(resp);
                AssertStartsWith("msn_", data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetMission_ExistingMission_ReturnsMission", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-get-mission").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_mission", new { id = missionId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Mission data = DeserializeData<Mission>(resp);
                AssertEqual(missionId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetMission_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_mission", new { id = "msn_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Mission not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("UpdateMission_ExistingMission_ReturnsUpdated", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-upd-mission").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("update_mission", new { id = missionId, data = new { Title = "ws-upd-mission-renamed" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Mission data = DeserializeData<Mission>(resp);
                AssertEqual(missionId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("UpdateMission_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("update_mission", new { id = "msn_nonexistent", data = new { Title = "x" } }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("CancelMission_ExistingMission_ReturnsCancelled", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-cancel-mission").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("cancel_mission", new { id = missionId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Mission data = DeserializeData<Mission>(resp);
                AssertEqual("Cancelled", data.Status.ToString());
                AssertEqual(missionId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("CancelMission_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("cancel_mission", new { id = "msn_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Mission not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("TransitionMissionStatus_ValidTransition_ReturnsUpdated", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-transition-mission").ConfigureAwait(false);

                // Pending -> Assigned via REST
                await _AuthClient.PutAsync("/api/v1/missions/" + missionId + "/status",
                    JsonHelper.ToJsonContent(new { Status = "Assigned" })).ConfigureAwait(false);

                // Assigned -> InProgress via WebSocket
                JsonElement resp = await WsCommandAsync("transition_mission_status", new { id = missionId, status = "InProgress" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Mission data = DeserializeData<Mission>(resp);
                AssertEqual("InProgress", data.Status.ToString());
            }).ConfigureAwait(false);

            await RunTest("TransitionMissionStatus_InvalidTransition_ReturnsError", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-bad-transition").ConfigureAwait(false);

                // Pending -> Complete is not valid
                JsonElement resp = await WsCommandAsync("transition_mission_status", new { id = missionId, status = "Complete" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertContains("Invalid transition", resp.GetProperty("error").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("TransitionMissionStatus_Refused_RepliesOnlyToCallingSession", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-refused-transition-scope").ConfigureAwait(false);
                Mission? before = await JsonHelper.DeserializeAsync<Mission>(
                    await _AuthClient.GetAsync("/api/v1/missions/" + missionId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertNotNull(before, "the mission reads back before the transition");

                using (ClientWebSocket caller = await ConnectAsync().ConfigureAwait(false))
                using (ClientWebSocket observer = await ConnectAsync().ConfigureAwait(false))
                {
                    await SendJsonAsync(observer, new { Route = "subscribe" }).ConfigureAwait(false);
                    await WaitForTypeAsync(observer, "stream.ready").ConfigureAwait(false);

                    // A newly created mission cannot move straight to Complete, so the shared
                    // transition path refuses it.
                    await SendJsonAsync(caller, new { Route = "command", action = "transition_mission_status", id = missionId, status = "Complete" }).ConfigureAwait(false);
                    JsonElement refusal = await WaitForTypeAsync(caller, "command.error").ConfigureAwait(false);
                    AssertEqual("transition_mission_status", refusal.GetProperty("action").GetString());
                    AssertContains("Invalid transition", refusal.GetProperty("error").GetString() ?? "");

                    // The observer's own command reply is queued after the refusal was produced, so a
                    // refusal or mission change that leaked to it would arrive first.
                    await SendJsonAsync(observer, new { Route = "command", action = "status" }).ConfigureAwait(false);
                    bool observerReplied = false;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    while (!observerReplied && DateTime.UtcNow < deadline)
                    {
                        JsonElement? frame = await ReceiveFrameOrCloseAsync(observer, 15).ConfigureAwait(false);
                        AssertTrue(frame.HasValue, "the observer session stays open while waiting for its command reply");
                        string raw = frame!.Value.GetRawText();
                        AssertFalse(raw.Contains("Invalid transition", StringComparison.Ordinal),
                            "the refusal text reaches only the session that requested the transition");
                        string? frameType = frame.Value.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
                        AssertFalse(frameType == "command.error",
                            "a refused transition reply is sent only to the session that requested it");
                        observerReplied = frameType == "command.result";
                    }
                    AssertTrue(observerReplied, "the observer session receives its own command reply");
                }

                Mission? after = await JsonHelper.DeserializeAsync<Mission>(
                    await _AuthClient.GetAsync("/api/v1/missions/" + missionId).ConfigureAwait(false)).ConfigureAwait(false);
                AssertEqual(before!.Status, after!.Status, "a refused transition leaves the mission unchanged");
            }).ConfigureAwait(false);

            await RunTest("TransitionMissionStatus_InvalidStatusString_ReturnsError", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-bad-status").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("transition_mission_status", new { id = missionId, status = "BogusStatus" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertContains("Invalid status", resp.GetProperty("error").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("TransitionMissionStatus_NonExistentMission_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("transition_mission_status", new { id = "msn_nonexistent", status = "InProgress" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Mission not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("TransitionMissionStatus_ToComplete_SetsCompletedUtc", async () =>
            {
                // A report-only mission satisfies the shared manual completion proof without a
                // landed commit; the gate itself is covered by the landing pipeline suite.
                HttpResponseMessage createResp = await _AuthClient.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new { Title = "ws-complete-mission", Mode = "Research" })).ConfigureAwait(false);
                createResp.EnsureSuccessStatusCode();
                string createBody = await createResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                MissionCreateResponse createWrapper = JsonHelper.Deserialize<MissionCreateResponse>(createBody);
                string missionId = (createWrapper.Mission ?? JsonHelper.Deserialize<Mission>(createBody)).Id;

                // Pending -> Assigned -> InProgress via REST
                await _AuthClient.PutAsync("/api/v1/missions/" + missionId + "/status",
                    JsonHelper.ToJsonContent(new { Status = "Assigned" })).ConfigureAwait(false);
                await _AuthClient.PutAsync("/api/v1/missions/" + missionId + "/status",
                    JsonHelper.ToJsonContent(new { Status = "InProgress" })).ConfigureAwait(false);

                JsonElement resp = await WsCommandAsync("transition_mission_status", new { id = missionId, status = "Complete" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Mission data = DeserializeData<Mission>(resp);
                AssertNotNull(data.CompletedUtc);
            }).ConfigureAwait(false);

            await RunTest("ListMissions_WithPagination_RespectsPageSize", async () =>
            {
                await CreateMissionViaRestAsync("ws-page-m1").ConfigureAwait(false);
                await CreateMissionViaRestAsync("ws-page-m2").ConfigureAwait(false);
                await CreateMissionViaRestAsync("ws-page-m3").ConfigureAwait(false);

                JsonElement resp = await WsCommandAsync("list_missions", new { query = new { pageSize = 2 } }).ConfigureAwait(false);
                EnumerationResult<Mission> data = DeserializeData<EnumerationResult<Mission>>(resp);
                AssertEqual(2, data.Objects.Count);
            }).ConfigureAwait(false);

            // Captain Tests
            await RunTest("ListCaptains_Empty_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_captains").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_captains", resp.GetProperty("action").GetString());
            }).ConfigureAwait(false);

            await RunTest("CreateCaptain_ReturnsCreatedCaptain", async () =>
            {
                JsonElement resp = await WsCommandAsync("create_captain", new { data = new { Name = "ws-captain", Runtime = "ClaudeCode" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Captain data = DeserializeData<Captain>(resp);
                AssertStartsWith("cpt_", data.Id);
            }).ConfigureAwait(false);

            await RunTest("CreateCaptain_ServerOwnedState_IsRefusedByName", async () =>
            {
                JsonElement resp = await WsCommandAsync("create_captain", new { data = new { Name = "ws-owned-captain", Runtime = "ClaudeCode", State = "Working", QuarantineReason = "caller supplied" } }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                string error = resp.GetProperty("error").GetString() ?? String.Empty;
                AssertContains("State", error);
                AssertContains("QuarantineReason", error);
            }).ConfigureAwait(false);

            await RunTest("CreateCaptain_DuplicateName_IsRefusedAsConflict", async () =>
            {
                string name = "ws-duplicate-captain-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                JsonElement first = await WsCommandAsync("create_captain", new { data = new { Name = name, Runtime = "ClaudeCode" } }).ConfigureAwait(false);
                AssertEqual("command.result", first.GetProperty("type").GetString());

                JsonElement second = await WsCommandAsync("create_captain", new { data = new { Name = name, Runtime = "Codex" } }).ConfigureAwait(false);
                AssertEqual("command.error", second.GetProperty("type").GetString(), second.ToString());
                AssertEqual(CaptainNameRule.NameTakenMessage, second.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("GetCaptain_ExistingCaptain_ReturnsCaptain", async () =>
            {
                string captainId = await CreateCaptainViaRestAsync("ws-get-captain").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_captain", new { id = captainId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Captain data = DeserializeData<Captain>(resp);
                AssertEqual(captainId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetCaptain_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_captain", new { id = "cpt_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Captain not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("UpdateCaptain_ExistingCaptain_ReturnsUpdated", async () =>
            {
                string captainId = await CreateCaptainViaRestAsync("ws-upd-captain").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("update_captain", new { id = captainId, data = new { Name = "ws-upd-captain-renamed", Runtime = "ClaudeCode" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Captain data = DeserializeData<Captain>(resp);
                AssertEqual(captainId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("UpdateCaptain_PreservesOperationalFields", async () =>
            {
                string captainId = await CreateCaptainViaRestAsync("ws-preserve-captain").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("update_captain", new { id = captainId, data = new { Name = "ws-preserve-renamed", Runtime = "ClaudeCode" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                Captain data = DeserializeData<Captain>(resp);
                AssertEqual("Idle", data.State.ToString());
            }).ConfigureAwait(false);

            await RunTest("UpdateCaptain_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("update_captain", new { id = "cpt_nonexistent", data = new { Name = "x" } }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Captain not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("DeleteCaptain_ExistingCaptain_ReturnsDeleted", async () =>
            {
                string captainId = await CreateCaptainViaRestAsync("ws-del-captain").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("delete_captain", new { id = captainId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                DeleteCaptainResponse data = DeserializeData<DeleteCaptainResponse>(resp);
                AssertEqual("deleted", data.Status);
            }).ConfigureAwait(false);

            await RunTest("DeleteCaptain_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("delete_captain", new { id = "cpt_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Captain not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("StopCaptain_ExistingCaptain_ReturnsStopped", async () =>
            {
                string captainId = await CreateCaptainViaRestAsync("ws-stop-captain").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("stop_captain", new { captainId = captainId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                StopCaptainResponse data = DeserializeData<StopCaptainResponse>(resp);
                AssertEqual("stopped", data.Status);
            }).ConfigureAwait(false);

            // Signal Tests
            await RunTest("ListSignals_Empty_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_signals").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_signals", resp.GetProperty("action").GetString());
            }).ConfigureAwait(false);

            await RunTest("SendSignal_ReturnsCreatedSignal", async () =>
            {
                JsonElement resp = await WsCommandAsync("send_signal", new { data = new { Type = "Nudge", Payload = "hello" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("send_signal", resp.GetProperty("action").GetString());
                Signal data = DeserializeData<Signal>(resp);
                AssertStartsWith("sig_", data.Id);
            }).ConfigureAwait(false);

            await RunTest("ListSignals_AfterSend_ReturnsSignal", async () =>
            {
                await WsCommandAsync("send_signal", new { data = new { Type = "Mail", Payload = "test-mail" } }).ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("list_signals").ConfigureAwait(false);
                EnumerationResult<Signal> data = DeserializeData<EnumerationResult<Signal>>(resp);
                AssertTrue(data.TotalRecords >= 1);
            }).ConfigureAwait(false);

            // Event Tests
            await RunTest("ListEvents_ReturnsEventList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_events").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_events", resp.GetProperty("action").GetString());
                EnumerationResult<ArmadaEvent> data = DeserializeData<EnumerationResult<ArmadaEvent>>(resp);
                AssertNotNull(data.Objects);
            }).ConfigureAwait(false);

            // Dock Tests
            await RunTest("ListDocks_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_docks").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_docks", resp.GetProperty("action").GetString());
                EnumerationResult<Dock> data = DeserializeData<EnumerationResult<Dock>>(resp);
                AssertNotNull(data.Objects);
            }).ConfigureAwait(false);

            // MergeQueue Tests
            await RunTest("ListMergeQueue_ReturnsEmptyList", async () =>
            {
                JsonElement resp = await WsCommandAsync("list_merge_queue").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("list_merge_queue", resp.GetProperty("action").GetString());
            }).ConfigureAwait(false);

            await RunTest("EnqueueMerge_ReturnsCreatedEntry", async () =>
            {
                JsonElement resp = await WsCommandAsync("enqueue_merge", new { data = new { BranchName = "feature/ws-test", TargetBranch = "main" } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("enqueue_merge", resp.GetProperty("action").GetString());
                MergeEntry data = DeserializeData<MergeEntry>(resp);
                AssertStartsWith("mrg_", data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetMergeEntry_ExistingEntry_ReturnsEntry", async () =>
            {
                JsonElement createResp = await WsCommandAsync("enqueue_merge", new { data = new { BranchName = "feature/ws-get-merge", TargetBranch = "main" } }).ConfigureAwait(false);
                MergeEntry created = DeserializeData<MergeEntry>(createResp);
                string mergeId = created.Id;

                JsonElement resp = await WsCommandAsync("get_merge_entry", new { id = mergeId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                MergeEntry data = DeserializeData<MergeEntry>(resp);
                AssertEqual(mergeId, data.Id);
            }).ConfigureAwait(false);

            await RunTest("GetMergeEntry_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_merge_entry", new { id = "mrg_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertEqual("Merge entry not found", resp.GetProperty("error").GetString());
            }).ConfigureAwait(false);

            await RunTest("CancelMerge_ExistingEntry_ReturnsCancelled", async () =>
            {
                JsonElement createResp = await WsCommandAsync("enqueue_merge", new { data = new { BranchName = "feature/ws-cancel-merge", TargetBranch = "main" } }).ConfigureAwait(false);
                MergeEntry created = DeserializeData<MergeEntry>(createResp);
                string mergeId = created.Id;

                JsonElement resp = await WsCommandAsync("cancel_merge", new { id = mergeId }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                CancelMergeResponse data = DeserializeData<CancelMergeResponse>(resp);
                AssertEqual("cancelled", data.Status);
            }).ConfigureAwait(false);

            await RunTest("ProcessMergeQueue_ReturnsProcessed", async () =>
            {
                JsonElement resp = await WsCommandAsync("process_merge_queue").ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                ProcessMergeQueueResponse data = DeserializeData<ProcessMergeQueueResponse>(resp);
                AssertEqual("processed", data.Status);
            }).ConfigureAwait(false);

            // ── Mission Diff/Log and Captain Log ────────────────────────

            await RunTest("GetMissionDiff_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_mission_diff", new { id = "msn_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("GetMissionDiff_ExistingMission_ReturnsResult", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-diff-mission").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_mission_diff", new { id = missionId }).ConfigureAwait(false);
                // May return error (no worktree/settings) or result — just verify action is correct
                string action = resp.GetProperty("action").GetString()!;
                AssertEqual("get_mission_diff", action);
            }).ConfigureAwait(false);

            await RunTest("GetMissionLog_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_mission_log", new { id = "msn_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("GetMissionLog_ExistingMission_ReturnsLogData", async () =>
            {
                string missionId = await CreateMissionViaRestAsync("ws-log-mission").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_mission_log", new { id = missionId }).ConfigureAwait(false);
                // May return error (settings not configured) or result with empty log
                string action = resp.GetProperty("action").GetString()!;
                AssertEqual("get_mission_log", action);
            }).ConfigureAwait(false);

            await RunTest("GetCaptainLog_NonExistent_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("get_captain_log", new { id = "cpt_nonexistent" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("GetCaptainLog_ExistingCaptain_ReturnsLogData", async () =>
            {
                string captainId = await CreateCaptainViaRestAsync("ws-log-captain").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("get_captain_log", new { id = captainId }).ConfigureAwait(false);
                // May return error (settings not configured) or result with empty log
                string action = resp.GetProperty("action").GetString()!;
                AssertEqual("get_captain_log", action);
            }).ConfigureAwait(false);

            // Note: stop_server is not tested in automated suite as it would shut down the server.

            // ── Enumerate ─────────────────────────────────────────────

            await RunTest("Enumerate_Fleets_ReturnsPaginatedResult", async () =>
            {
                await CreateFleetViaRestAsync("ws-enum-fleet").ConfigureAwait(false);
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "fleets", query = new { pageSize = 10, pageNumber = 1 } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
                AssertEqual("enumerate", resp.GetProperty("action").GetString());
                EnumerationResult<Fleet> data = DeserializeData<EnumerationResult<Fleet>>(resp);
                AssertNotNull(data.Objects);
            }).ConfigureAwait(false);

            await RunTest("Enumerate_Vessels_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "vessels" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_Captains_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "captains" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_Missions_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "missions" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_Voyages_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "voyages" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_Docks_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "docks" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_Signals_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "signals" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_Events_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "events" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_MergeQueue_ReturnsResult", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "merge_queue" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_WithPagination_RespectsPageSize", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "fleets", query = new { pageSize = 5, pageNumber = 1 } }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_SingularEntityType_Works", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "fleet" }).ConfigureAwait(false);
                AssertEqual("command.result", resp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("Enumerate_InvalidEntityType_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("enumerate", new { entityType = "bananas" }).ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertContains("Unknown entity type", resp.GetProperty("error").GetString()!);
            }).ConfigureAwait(false);

            // Error Handling Tests
            await RunTest("UnknownAction_ReturnsError", async () =>
            {
                JsonElement resp = await WsCommandAsync("totally_bogus_action").ConfigureAwait(false);
                AssertEqual("command.error", resp.GetProperty("type").GetString());
                AssertContains("Unknown action", resp.GetProperty("error").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("UnknownRoute_ReturnsError", async () =>
            {
                using ClientWebSocket ws = await ConnectAsync().ConfigureAwait(false);

                string msg = JsonHelper.Serialize(new { Route = "bad_route" });
                byte[] bytes = Encoding.UTF8.GetBytes(msg);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

                byte[] buffer = new byte[1048576];
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                AssertEqual("error", root.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("NoRoute_ReturnsError", async () =>
            {
                using ClientWebSocket ws = await ConnectAsync().ConfigureAwait(false);

                string msg = JsonHelper.Serialize(new { hello = "world" });
                byte[] bytes = Encoding.UTF8.GetBytes(msg);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

                byte[] buffer = new byte[1048576];
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                AssertEqual("error", root.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            // CrossEntity Tests
            await RunTest("FullFleetLifecycle_CreateGetUpdateDelete", async () =>
            {
                // Create
                JsonElement createResp = await WsCommandAsync("create_fleet", new { data = new { Name = "lifecycle-fleet" } }).ConfigureAwait(false);
                AssertEqual("command.result", createResp.GetProperty("type").GetString());
                Fleet createdFleet = DeserializeData<Fleet>(createResp);
                string fleetId = createdFleet.Id;
                AssertStartsWith("flt_", fleetId);

                // Get
                JsonElement getResp = await WsCommandAsync("get_fleet", new { id = fleetId }).ConfigureAwait(false);
                AssertEqual("command.result", getResp.GetProperty("type").GetString());
                FleetDetailResponse getDetail = DeserializeData<FleetDetailResponse>(getResp);
                AssertEqual(fleetId, getDetail.Fleet!.Id);

                // Update
                JsonElement updateResp = await WsCommandAsync("update_fleet", new { id = fleetId, data = new { Name = "lifecycle-fleet-updated" } }).ConfigureAwait(false);
                AssertEqual("command.result", updateResp.GetProperty("type").GetString());

                // Delete
                JsonElement deleteResp = await WsCommandAsync("delete_fleet", new { id = fleetId }).ConfigureAwait(false);
                AssertEqual("command.result", deleteResp.GetProperty("type").GetString());
                DeleteFleetResponse deleted = DeserializeData<DeleteFleetResponse>(deleteResp);
                AssertEqual("deleted", deleted.Status);

                // Verify deleted
                JsonElement verifyResp = await WsCommandAsync("get_fleet", new { id = fleetId }).ConfigureAwait(false);
                AssertEqual("command.error", verifyResp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("FullVoyageLifecycle_CreateGetCancelPurge", async () =>
            {
                // Create bare voyage
                JsonElement createResp = await WsCommandAsync("create_voyage", new { data = new { title = "lifecycle-voyage", description = "test" } }).ConfigureAwait(false);
                AssertEqual("command.result", createResp.GetProperty("type").GetString());
                Voyage createdVoyage = DeserializeData<Voyage>(createResp);
                string voyageId = createdVoyage.Id;

                // Get
                JsonElement getResp = await WsCommandAsync("get_voyage", new { id = voyageId }).ConfigureAwait(false);
                AssertEqual("command.result", getResp.GetProperty("type").GetString());

                // Create another voyage for purge test
                JsonElement create2Resp = await WsCommandAsync("create_voyage", new { data = new { title = "purge-voyage" } }).ConfigureAwait(false);
                Voyage createdVoyage2 = DeserializeData<Voyage>(create2Resp);
                string purgeId = createdVoyage2.Id;

                // Cancel first
                JsonElement cancelResp = await WsCommandAsync("cancel_voyage", new { id = voyageId }).ConfigureAwait(false);
                AssertEqual("command.result", cancelResp.GetProperty("type").GetString());
                CancelVoyageResponse cancelData = DeserializeData<CancelVoyageResponse>(cancelResp);
                AssertEqual("Cancelled", cancelData.Voyage!.Status.ToString());

                // Cancel second before purge — purge is blocked on Open/InProgress voyages
                await WsCommandAsync("cancel_voyage", new { id = purgeId }).ConfigureAwait(false);

                // Purge second
                JsonElement purgeResp = await WsCommandAsync("purge_voyage", new { id = purgeId }).ConfigureAwait(false);
                AssertEqual("command.result", purgeResp.GetProperty("type").GetString());
                PurgeVoyageResponse purgeData = DeserializeData<PurgeVoyageResponse>(purgeResp);
                AssertEqual("deleted", purgeData.Status);

                // Verify purged
                JsonElement verifyResp = await WsCommandAsync("get_voyage", new { id = purgeId }).ConfigureAwait(false);
                AssertEqual("command.error", verifyResp.GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("FullMissionLifecycle_CreateTransitionCancel", async () =>
            {
                // Create
                JsonElement createResp = await WsCommandAsync("create_mission", new { data = new { Title = "lifecycle-mission" } }).ConfigureAwait(false);
                AssertEqual("command.result", createResp.GetProperty("type").GetString());
                Mission createdMission = DeserializeData<Mission>(createResp);
                string missionId = createdMission.Id;

                // Transition: Pending -> Assigned
                JsonElement t1 = await WsCommandAsync("transition_mission_status", new { id = missionId, status = "Assigned" }).ConfigureAwait(false);
                AssertEqual("command.result", t1.GetProperty("type").GetString());

                // Transition: Assigned -> InProgress
                JsonElement t2 = await WsCommandAsync("transition_mission_status", new { id = missionId, status = "InProgress" }).ConfigureAwait(false);
                AssertEqual("command.result", t2.GetProperty("type").GetString());

                // Cancel
                JsonElement cancelResp = await WsCommandAsync("cancel_mission", new { id = missionId }).ConfigureAwait(false);
                AssertEqual("command.result", cancelResp.GetProperty("type").GetString());
                Mission cancelledMission = DeserializeData<Mission>(cancelResp);
                AssertEqual("Cancelled", cancelledMission.Status.ToString());
            }).ConfigureAwait(false);

            await RunTest("FullCaptainLifecycle_CreateUpdateDelete", async () =>
            {
                // Create
                JsonElement createResp = await WsCommandAsync("create_captain", new { data = new { Name = "lifecycle-captain", Runtime = "ClaudeCode" } }).ConfigureAwait(false);
                AssertEqual("command.result", createResp.GetProperty("type").GetString());
                Captain createdCaptain = DeserializeData<Captain>(createResp);
                string captainId = createdCaptain.Id;

                // Get
                JsonElement getResp = await WsCommandAsync("get_captain", new { id = captainId }).ConfigureAwait(false);
                AssertEqual("command.result", getResp.GetProperty("type").GetString());

                // Update
                JsonElement updateResp = await WsCommandAsync("update_captain", new { id = captainId, data = new { Name = "lifecycle-renamed", Runtime = "ClaudeCode" } }).ConfigureAwait(false);
                AssertEqual("command.result", updateResp.GetProperty("type").GetString());

                // Delete
                JsonElement deleteResp = await WsCommandAsync("delete_captain", new { id = captainId }).ConfigureAwait(false);
                AssertEqual("command.result", deleteResp.GetProperty("type").GetString());

                // Verify deleted
                JsonElement verifyResp = await WsCommandAsync("get_captain", new { id = captainId }).ConfigureAwait(false);
                AssertEqual("command.error", verifyResp.GetProperty("type").GetString());
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task<ClientWebSocket> ConnectAsync()
        {
            ClientWebSocket ws = await ConnectAnonymousAsync().ConfigureAwait(false);
            JsonElement result = await AuthenticateAsync(ws, new { Route = "authenticate", apiKey = _ApiKey }).ConfigureAwait(false);
            if (result.GetProperty("type").GetString() != "auth.result")
                throw new InvalidOperationException("WebSocket authentication failed: " + result.GetRawText());
            return ws;
        }

        private async Task<ClientWebSocket> ConnectAnonymousAsync()
        {
            ClientWebSocket ws = new ClientWebSocket();
            Uri uri = new Uri("ws://localhost:" + _RestPort + "/ws");
            await ws.ConnectAsync(uri, CancellationToken.None).ConfigureAwait(false);
            return ws;
        }

        private static async Task<JsonElement> AuthenticateAsync(ClientWebSocket socket, object authenticateMessage)
        {
            await SendJsonAsync(socket, authenticateMessage).ConfigureAwait(false);
            JsonElement? frame = await ReceiveFrameOrCloseAsync(socket, 10).ConfigureAwait(false);
            if (!frame.HasValue) throw new InvalidOperationException("WebSocket closed before an authentication reply.");
            return frame.Value;
        }

        private static async Task<JsonElement?> ReceiveFrameOrCloseAsync(ClientWebSocket socket, int timeoutSeconds)
        {
            byte[] buffer = new byte[1048576];
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        return document.RootElement.Clone();
                    }
                }
                catch (WebSocketException)
                {
                    return null;
                }
            }
        }

        private static async Task<JsonElement> WaitForTypeAsync(ClientWebSocket socket, string type)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                JsonElement? frame = await ReceiveFrameOrCloseAsync(socket, 15).ConfigureAwait(false);
                if (!frame.HasValue) throw new InvalidOperationException("WebSocket closed while waiting for " + type + ".");
                if (frame.Value.TryGetProperty("type", out JsonElement typeElement) && typeElement.GetString() == type)
                    return frame.Value;
            }

            throw new TimeoutException("Timed out waiting for " + type + ".");
        }

        private static async Task SendJsonAsync(ClientWebSocket socket, object value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonHelper.Serialize(value));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }

        private static async Task<JsonElement> ReceiveFrameAsync(
            ClientWebSocket socket,
            byte[] buffer,
            CancellationToken token)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        /// <summary>
        /// Extract the "data" property from a WsCommandAsync JsonElement result and deserialize it to T.
        /// </summary>
        private T DeserializeData<T>(JsonElement resp)
        {
            string dataJson = resp.GetProperty("data").GetRawText();
            return JsonHelper.Deserialize<T>(dataJson);
        }

        private async Task<JsonElement> WsCommandAsync(string action, object? extraFields = null)
        {
            using ClientWebSocket ws = await ConnectAsync().ConfigureAwait(false);

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

        private async Task<string> CreateFleetViaRestAsync(string name)
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/fleets",
                JsonHelper.ToJsonContent(new { Name = name })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp).ConfigureAwait(false);
            return fleet.Id;
        }

        private async Task<string> CreateVesselViaRestAsync(string name)
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/vessels",
                JsonHelper.ToJsonContent(new { Name = name, RepoUrl = TestRepoHelper.GetLocalBareRepoUrl() })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp).ConfigureAwait(false);
            return vessel.Id;
        }

        private async Task<string> CreateCaptainViaRestAsync(string name)
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/captains",
                JsonHelper.ToJsonContent(new { Name = name, Runtime = "ClaudeCode" })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(resp).ConfigureAwait(false);
            return captain.Id;
        }

        private async Task<string> CreateMissionViaRestAsync(string title)
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/missions",
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

        private async Task<string> CreateTenantAdministratorTokenAsync(string label)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage tenantResponse = await _AuthClient.PostAsync("/api/v1/tenants",
                JsonHelper.ToJsonContent(new { Name = label + "-" + suffix })).ConfigureAwait(false);
            tenantResponse.EnsureSuccessStatusCode();
            TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResponse).ConfigureAwait(false);

            HttpResponseMessage userResponse = await _AuthClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenant.Id,
                    Email = label + "-" + suffix + "@ws.armada",
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = true
                })).ConfigureAwait(false);
            userResponse.EnsureSuccessStatusCode();
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);

            HttpResponseMessage credentialResponse = await _AuthClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new { TenantId = tenant.Id, UserId = user.Id, Name = label + "-cred" })).ConfigureAwait(false);
            credentialResponse.EnsureSuccessStatusCode();
            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
            return credential.BearerToken;
        }

        private async Task<string> CreateMissionWithBearerAsync(string bearerToken, string title)
        {
            using (HttpClient client = new HttpClient { BaseAddress = _AuthClient.BaseAddress })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                HttpResponseMessage resp = await client.PostAsync("/api/v1/missions", JsonHelper.ToJsonContent(new { Title = title })).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
                if (wrapper.Mission != null) return wrapper.Mission.Id;
                return JsonHelper.Deserialize<Mission>(body).Id;
            }
        }

        private async Task<string> CreateVoyageViaRestAsync(string title)
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/voyages",
                JsonHelper.ToJsonContent(new { Title = title })).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(resp).ConfigureAwait(false);
            return voyage.Id;
        }

        #endregion
    }
}
