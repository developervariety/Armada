namespace Armada.Test.Unit.Suites.Services
{
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Server.Mcp;
    using Armada.Test.Common;

    /// <summary>
    /// Protocol compatibility tests for Armada's official MCP SDK HTTP adapter.
    /// </summary>
    public class ArmadaMcpHttpServerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Armada MCP HTTP Server";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ModernDiscoveryAndToolCalls_Use2026Protocol", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_echo",
                    "Echo a value",
                    new
                    {
                        type = "object",
                        properties = new { value = new { type = "string" } },
                        required = new[] { "value" }
                    },
                    args => Task.FromResult((object)new
                    {
                        Echo = args!.Value.GetProperty("value").GetString()
                    }));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                JsonElement discovery = await PostModernAsync(
                    client,
                    "/mcp",
                    1,
                    "server/discover",
                    new { }).ConfigureAwait(false);
                JsonElement discoveryResult = discovery.GetProperty("result");
                AssertEqual("complete", discoveryResult.GetProperty("resultType").GetString());
                AssertTrue(
                    discoveryResult.GetProperty("supportedVersions")
                        .EnumerateArray()
                        .Any(version => version.GetString() == "2026-07-28"),
                    "server/discover should advertise MCP 2026-07-28");

                JsonElement list = await PostModernAsync(
                    client,
                    "/mcp",
                    2,
                    "tools/list",
                    new { }).ConfigureAwait(false);
                JsonElement listResult = list.GetProperty("result");
                AssertEqual("armada_echo", listResult.GetProperty("tools")[0].GetProperty("name").GetString());
                AssertEqual("public", listResult.GetProperty("cacheScope").GetString());
                AssertTrue(listResult.GetProperty("ttlMs").GetInt64() > 0, "tools/list should be cacheable");

                JsonElement call = await PostModernAsync(
                    client,
                    "/mcp",
                    3,
                    "tools/call",
                    new
                    {
                        name = "armada_echo",
                        arguments = new { value = "modern" }
                    },
                    requestName: "armada_echo").ConfigureAwait(false);
                string text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
                AssertContains("\"Echo\":\"modern\"", text);
            }).ConfigureAwait(false);

            await RunTest("LegacyInitializeAndRpcRoute_RemainCompatible", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_legacy",
                    "Legacy compatibility tool",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Status = "ok" }));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                JsonElement initialize = await PostLegacyAsync(
                    client,
                    "/rpc",
                    10,
                    "initialize",
                    new
                    {
                        protocolVersion = "2025-11-25",
                        capabilities = new { },
                        clientInfo = new { name = "legacy-test", version = "1" }
                    }).ConfigureAwait(false);
                AssertEqual(
                    "2025-11-25",
                    initialize.GetProperty("result").GetProperty("protocolVersion").GetString());

                JsonElement list = await PostLegacyAsync(
                    client,
                    "/rpc",
                    11,
                    "tools/list",
                    new { }).ConfigureAwait(false);
                AssertEqual(
                    "armada_legacy",
                    list.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

                JsonElement call = await PostLegacyAsync(
                    client,
                    "/rpc",
                    12,
                    "tools/call",
                    new { name = "armada_legacy", arguments = new { } }).ConfigureAwait(false);
                string text = call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
                AssertContains("\"Status\":\"ok\"", text);
            }).ConfigureAwait(false);

            await RunTest("ToolsList_WithoutParams_RemainsCompatible", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_parameterless_list",
                    "Verify parameterless discovery",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Status = "ok" }));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                JsonElement list = await SendAsync(
                    client,
                    CreateRequestWithoutParams("/mcp", 12, "tools/list")).ConfigureAwait(false);
                AssertEqual(
                    "armada_parameterless_list",
                    list.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            }).ConfigureAwait(false);

            await RunTest("ToolExceptions_RemainJsonRpcErrors", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.RegisterTool(
                    "armada_failure",
                    "Throw an expected test failure",
                    new { type = "object" },
                    args => Task.FromException<object>(
                        new InvalidOperationException("expected failure")));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };
                JsonElement response = await PostLegacyAsync(
                    client,
                    "/rpc",
                    13,
                    "tools/call",
                    new { name = "armada_failure", arguments = new { } }).ConfigureAwait(false);
                AssertTrue(response.TryGetProperty("error", out _), "tool exception should be a JSON-RPC error");
            }).ConfigureAwait(false);

            await RunTest("ToolsList_PaginatesDeterministically", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                for (int index = 0; index < 501; index++)
                {
                    string name = "armada_tool_" + index.ToString("D3");
                    server.RegisterTool(
                        name,
                        "Tool " + index,
                        new { type = "object" },
                        args => Task.FromResult((object)new { Status = "ok" }));
                }
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };
                JsonElement first = await PostModernAsync(
                    client,
                    "/mcp",
                    20,
                    "tools/list",
                    new { }).ConfigureAwait(false);
                JsonElement firstResult = first.GetProperty("result");
                AssertEqual(500, firstResult.GetProperty("tools").GetArrayLength());
                AssertEqual("500", firstResult.GetProperty("nextCursor").GetString());

                JsonElement second = await PostModernAsync(
                    client,
                    "/mcp",
                    21,
                    "tools/list",
                    new { cursor = "500" }).ConfigureAwait(false);
                JsonElement secondResult = second.GetProperty("result");
                AssertEqual(1, secondResult.GetProperty("tools").GetArrayLength());
                AssertFalse(secondResult.TryGetProperty("nextCursor", out _), "last page should not have a cursor");
                AssertEqual(
                    "armada_tool_500",
                    secondResult.GetProperty("tools")[0].GetProperty("name").GetString());
            }).ConfigureAwait(false);

            await RunTest("PendingWake_RidesBackOnAnyToolResult", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.PendingWakeProvider = (participantKey, token) =>
                    Task.FromResult((IReadOnlyList<string>)new List<string>
                    {
                        "[to=" + participantKey + "] [from=lead] take vessel review"
                    });
                RegisterStatusTool(server);
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                // A monitoring session calls a status tool, not a coordination tool. Before
                // the wake rode on the tool result, this is exactly where mail went unseen.
                JsonElement withoutHeader = await CallStatusToolAsync(client, 1, null).ConfigureAwait(false);
                AssertFalse(
                    ResultText(withoutHeader).Contains("[ARMADA WAKE]", StringComparison.Ordinal),
                    "an unidentified caller must not receive another session's mail");

                JsonElement withHeader = await CallStatusToolAsync(client, 2, "lead-session").ConfigureAwait(false);
                string text = ResultText(withHeader);
                AssertContains("armada_status ok", text);
                AssertContains("[ARMADA WAKE]", text);
                AssertContains("take vessel review", text);
                AssertContains("armada_mark_signal_read", text);
            }).ConfigureAwait(false);

            await RunTest("PendingWake_RepeatsUntilAcknowledged", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                int providerCalls = 0;
                server.PendingWakeProvider = (participantKey, token) =>
                {
                    providerCalls++;
                    return Task.FromResult((IReadOnlyList<string>)new List<string> { "still waiting" });
                };
                RegisterStatusTool(server);
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                // Delivery is not acknowledgement. A wake that stopped being shown before
                // the session read it would be a lost wake.
                JsonElement first = await CallStatusToolAsync(client, 1, "lead-session").ConfigureAwait(false);
                JsonElement second = await CallStatusToolAsync(client, 2, "lead-session").ConfigureAwait(false);
                AssertContains("[ARMADA WAKE]", ResultText(first));
                AssertContains("[ARMADA WAKE]", ResultText(second));
                AssertEqual(2, providerCalls, "each tool call should re-check for pending wakes");
            }).ConfigureAwait(false);

            await RunTest("PendingWake_SkipsToolsThatCarryTheirOwnWakes", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.WakeBannerExcludedTools.Add("armada_coordination_read");
                server.PendingWakeProvider = (participantKey, token) =>
                    Task.FromResult((IReadOnlyList<string>)new List<string> { "directed work" });
                server.RegisterTool(
                    "armada_coordination_read",
                    "Read the board",
                    new { type = "object" },
                    args => Task.FromResult((object)new { UnreadWakes = new[] { "directed work" } }));
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                JsonElement call = await CallToolAsync(
                    client, 1, "armada_coordination_read", "lead-session").ConfigureAwait(false);
                string text = ResultText(call);
                AssertContains("UnreadWakes", text);
                AssertFalse(
                    text.Contains("[ARMADA WAKE]", StringComparison.Ordinal),
                    "a tool that already returns its wakes must not be given a second copy");
            }).ConfigureAwait(false);

            await RunTest("PendingWake_LookupFailureLeavesTheToolResultIntact", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.PendingWakeProvider = (participantKey, token) =>
                    throw new InvalidOperationException("board unavailable");
                RegisterStatusTool(server);
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                // Losing the caller's real result to a board lookup is the worse outcome.
                JsonElement call = await CallStatusToolAsync(client, 1, "lead-session").ConfigureAwait(false);
                AssertContains("armada_status ok", ResultText(call));
            }).ConfigureAwait(false);

            await RunTest("PendingWake_RejectsAMalformedParticipantHeader", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                string? observedKey = null;
                server.PendingWakeProvider = (participantKey, token) =>
                {
                    observedKey = participantKey;
                    return Task.FromResult((IReadOnlyList<string>)new List<string> { "mail" });
                };
                RegisterStatusTool(server);
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                // The key is echoed into text a model reads, so a key that is not a plain
                // identifier is dropped rather than passed on.
                JsonElement call = await CallStatusToolAsync(client, 1, "lead session <b>").ConfigureAwait(false);
                AssertNull(observedKey, "a malformed participant key must not reach the board lookup");
                AssertFalse(
                    ResultText(call).Contains("[ARMADA WAKE]", StringComparison.Ordinal),
                    "a malformed participant key must not deliver a wake");
            }).ConfigureAwait(false);

            await RunTest("BearerAuthenticationFailsClosed", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.BearerToken = "test-token";
                RegisterStatusTool(server);
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };
                using HttpRequestMessage missingRequest = CreateRequest(
                    "/mcp", 1, "tools/list", new { });
                using HttpResponseMessage missingResponse = await client.SendAsync(missingRequest).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, missingResponse.StatusCode);
                AssertEqual(
                    "Bearer realm=\"Armada MCP\"",
                    missingResponse.Headers.WwwAuthenticate.Single().ToString());

                using HttpRequestMessage validRequest = CreateRequest(
                    "/mcp", 2, "tools/list", new { });
                validRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", "test-token");
                using HttpResponseMessage validResponse = await client.SendAsync(validRequest).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, validResponse.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("OAuthDiscoveryAndPkceGrant_AuthorizeMcpRequest", async () =>
            {
                int port = GetAvailablePort();
                const string publicBase = "https://armada-poc.example";
                const string redirectUri = "http://127.0.0.1:43119/callback";
                const string ownerSecret = "owner-test";
                string verifier = new string('v', 64);
                string challenge = Convert.ToBase64String(
                    SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');

                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.BearerToken = "backup-test";
                server.OAuthBroker = new GrokMcpOAuthBroker(publicBase, ownerSecret);
                RegisterStatusTool(server);
                await server.StartAsync().ConfigureAwait(false);

                CookieContainer cookies = new CookieContainer();
                using HttpClient client = new HttpClient(new HttpClientHandler
                {
                    CookieContainer = cookies,
                    AllowAutoRedirect = false
                })
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };

                using HttpResponseMessage unauthorized = await client.SendAsync(
                    CreateRequest("/mcp", 1, "tools/list", new { })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                AssertContains("oauth-protected-resource/mcp", unauthorized.Headers.WwwAuthenticate.Single().ToString());

                JsonElement metadata = JsonSerializer.Deserialize<JsonElement>(
                    await client.GetStringAsync("/.well-known/oauth-protected-resource/mcp").ConfigureAwait(false));
                AssertEqual(publicBase + "/mcp", metadata.GetProperty("resource").GetString());

                using HttpResponseMessage registrationResponse = await client.PostAsync(
                    "/oauth/register",
                    new StringContent(JsonSerializer.Serialize(new
                    {
                        redirect_uris = new[] { redirectUri },
                        client_name = "Grok test",
                        token_endpoint_auth_method = "none"
                    }), Encoding.UTF8, "application/json")).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, registrationResponse.StatusCode);
                JsonElement registration = JsonSerializer.Deserialize<JsonElement>(
                    await registrationResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                string clientId = registration.GetProperty("client_id").GetString()!;

                string authorizePath = "/oauth/authorize?response_type=code&client_id=" + Uri.EscapeDataString(clientId)
                    + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                    + "&state=test-state&scope=armada%3Aread&resource=" + Uri.EscapeDataString(publicBase + "/mcp")
                    + "&code_challenge=" + Uri.EscapeDataString(challenge) + "&code_challenge_method=S256";
                using HttpResponseMessage authorizationResponse = await client.GetAsync(authorizePath).ConfigureAwait(false);
                string approvalPage = await authorizationResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                string csrf = ExtractHiddenValue(approvalPage, "csrf");
                using HttpRequestMessage approvalRequest = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["redirect_uri"] = redirectUri,
                        ["state"] = "test-state",
                        ["scope"] = "armada:read",
                        ["resource"] = publicBase + "/mcp",
                        ["code_challenge"] = challenge,
                        ["code_challenge_method"] = "S256",
                        ["csrf"] = csrf,
                        ["owner_secret"] = ownerSecret,
                        ["decision"] = "approve"
                    })
                };
                approvalRequest.Headers.TryAddWithoutValidation("Cookie", "armada_oauth_csrf=" + csrf);
                using HttpResponseMessage approval = await client.SendAsync(approvalRequest).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Redirect, approval.StatusCode);
                string code = ParseQueryValue(approval.Headers.Location!, "code");

                using HttpResponseMessage tokenResponse = await client.PostAsync(
                    "/oauth/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["client_id"] = clientId,
                        ["code"] = code,
                        ["redirect_uri"] = redirectUri,
                        ["code_verifier"] = verifier,
                        ["resource"] = publicBase + "/mcp"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, tokenResponse.StatusCode);
                JsonElement tokens = JsonSerializer.Deserialize<JsonElement>(
                    await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false));

                using HttpRequestMessage authorized = CreateRequest("/mcp", 2, "tools/list", new { });
                authorized.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", tokens.GetProperty("access_token").GetString());
                using HttpResponseMessage authorizedResponse = await client.SendAsync(authorized).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, authorizedResponse.StatusCode);

                string firstRefreshToken = tokens.GetProperty("refresh_token").GetString()!;
                using HttpResponseMessage refreshResponse = await client.PostAsync(
                    "/oauth/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["refresh_token"] = firstRefreshToken,
                        ["resource"] = publicBase + "/mcp"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, refreshResponse.StatusCode);

                using HttpResponseMessage replayResponse = await client.PostAsync(
                    "/oauth/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["refresh_token"] = firstRefreshToken,
                        ["resource"] = publicBase + "/mcp"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.BadRequest, replayResponse.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("FixedParticipantRejectsSpoofingAndFeedsAudit", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                server.BearerToken = "test-token";
                server.FixedParticipantKey = "armada-lead";
                McpToolCallAudit? observedAudit = null;
                string? observedParticipant = null;
                server.ToolCallAuditSink = (audit, token) =>
                {
                    observedAudit = audit;
                    return Task.CompletedTask;
                };
                server.RegisterTool(
                    "armada_identity",
                    "Return the assigned participant",
                    new { type = "object" },
                    args =>
                    {
                        observedParticipant = ArmadaMcpHttpServer.CurrentParticipantKey;
                        return Task.FromResult((object)new { Participant = observedParticipant });
                    });
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };
                using HttpRequestMessage spoofed = CreateRequest(
                    "/mcp", 1, "tools/call", new { name = "armada_identity", arguments = new { } });
                spoofed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", "test-token");
                spoofed.Headers.TryAddWithoutValidation(ArmadaMcpHttpServer.ParticipantHeaderName, "attacker");
                using HttpResponseMessage spoofedResponse = await client.SendAsync(spoofed).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Forbidden, spoofedResponse.StatusCode);

                using HttpRequestMessage valid = CreateRequest(
                    "/mcp", 2, "tools/call", new { name = "armada_identity", arguments = new { } });
                valid.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", "test-token");
                using HttpResponseMessage validResponse = await client.SendAsync(valid).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, validResponse.StatusCode);
                AssertEqual("armada-lead", observedParticipant);
                AssertNotNull(observedAudit, "The successful tool call must reach the audit sink.");
                AssertEqual("armada-lead", observedAudit!.ParticipantKey);
                AssertEqual("armada_identity", observedAudit.ToolName);
                AssertTrue(observedAudit.Succeeded);
            }).ConfigureAwait(false);

            await RunTest("RequiredAuditFailureBlocksToolHandler", async () =>
            {
                int port = GetAvailablePort();
                await using ArmadaMcpHttpServer server = CreateServer(port);
                bool handlerRan = false;
                server.RequireToolCallAudit = true;
                server.ToolCallAuditSink = (audit, token) =>
                    Task.FromException(new InvalidOperationException("audit unavailable"));
                server.RegisterTool(
                    "armada_guarded",
                    "Must not run without its durable audit",
                    new { type = "object" },
                    args =>
                    {
                        handlerRan = true;
                        return Task.FromResult((object)new { Status = "unexpected" });
                    });
                await server.StartAsync().ConfigureAwait(false);

                using HttpClient client = new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + port)
                };
                JsonElement response = await PostLegacyAsync(
                    client,
                    "/rpc",
                    30,
                    "tools/call",
                    new { name = "armada_guarded", arguments = new { } }).ConfigureAwait(false);
                AssertTrue(response.TryGetProperty("error", out _), "audit failure should be a JSON-RPC error");
                AssertFalse(handlerRan, "the tool handler must not run before its required audit exists");
            }).ConfigureAwait(false);
        }

        private static void RegisterStatusTool(ArmadaMcpHttpServer server)
        {
            server.RegisterTool(
                "armada_status",
                "Report fleet status",
                new { type = "object" },
                args => Task.FromResult((object)new { Status = "armada_status ok" }));
        }

        private static async Task<JsonElement> CallStatusToolAsync(
            HttpClient client,
            int id,
            string? participantKey)
        {
            return await CallToolAsync(client, id, "armada_status", participantKey).ConfigureAwait(false);
        }

        private static async Task<JsonElement> CallToolAsync(
            HttpClient client,
            int id,
            string toolName,
            string? participantKey)
        {
            HttpRequestMessage request = CreateRequest(
                "/mcp",
                id,
                "tools/call",
                new { name = toolName, arguments = new { } });
            if (participantKey != null)
                request.Headers.TryAddWithoutValidation(
                    ArmadaMcpHttpServer.ParticipantHeaderName, participantKey);
            return await SendAsync(client, request).ConfigureAwait(false);
        }

        private static string ResultText(JsonElement response)
        {
            StringBuilder text = new StringBuilder();
            foreach (JsonElement block in response.GetProperty("result").GetProperty("content").EnumerateArray())
            {
                if (block.TryGetProperty("text", out JsonElement value))
                    text.AppendLine(value.GetString());
            }

            return text.ToString();
        }

        private static ArmadaMcpHttpServer CreateServer(int port)
        {
            return new ArmadaMcpHttpServer("127.0.0.1", port)
            {
                ServerName = "Armada Test",
                ServerVersion = "9.9.9"
            };
        }

        private static async Task<JsonElement> PostModernAsync(
            HttpClient client,
            string path,
            int id,
            string method,
            object parameters,
            string? requestName = null)
        {
            JsonElement paramsElement = JsonSerializer.SerializeToElement(parameters);
            Dictionary<string, object?> paramsDictionary = new Dictionary<string, object?>();
            foreach (JsonProperty property in paramsElement.EnumerateObject())
                paramsDictionary[property.Name] = property.Value.Clone();
            paramsDictionary["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new Dictionary<string, object>()
            };

            HttpRequestMessage request = CreateRequest(path, id, method, paramsDictionary);
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            request.Headers.Add("Mcp-Method", method);
            if (requestName != null)
                request.Headers.Add("Mcp-Name", requestName);
            return await SendAsync(client, request).ConfigureAwait(false);
        }

        private static async Task<JsonElement> PostLegacyAsync(
            HttpClient client,
            string path,
            int id,
            string method,
            object parameters)
        {
            return await SendAsync(client, CreateRequest(path, id, method, parameters)).ConfigureAwait(false);
        }

        private static HttpRequestMessage CreateRequest(
            string path,
            int id,
            string method,
            object parameters)
        {
            string json = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            });
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Accept", "application/json, text/event-stream");
            return request;
        }

        private static HttpRequestMessage CreateRequestWithoutParams(
            string path,
            int id,
            string method)
        {
            string json = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method
            });
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Accept", "application/json, text/event-stream");
            return request;
        }

        private static async Task<JsonElement> SendAsync(HttpClient client, HttpRequestMessage request)
        {
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception("MCP HTTP request failed with " + response.StatusCode + ": " + body);

            string json = ExtractJsonRpc(body, response.Content.Headers.ContentType?.MediaType);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        private static string ExtractJsonRpc(string body, string? mediaType)
        {
            if (!String.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                return body;

            foreach (string line in body.Split('\n'))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    return line.Substring(5).Trim();
            }

            throw new InvalidDataException("MCP SSE response did not contain a data event.");
        }

        private static int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string ExtractHiddenValue(string html, string name)
        {
            string marker = "name=\"" + name + "\" value=\"";
            int start = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("Hidden form value not found: " + name);
            start += marker.Length;
            int end = html.IndexOf('"', start);
            return WebUtility.HtmlDecode(html.Substring(start, end - start));
        }

        private static string ParseQueryValue(Uri location, string name)
        {
            foreach (string pair in location.Query.TrimStart('?').Split('&'))
            {
                string[] parts = pair.Split('=', 2);
                if (String.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.Ordinal))
                    return Uri.UnescapeDataString(parts.Length == 2 ? parts[1] : "");
            }
            throw new InvalidDataException("Query value not found: " + name);
        }
    }
}
