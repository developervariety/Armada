namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using SyslogLogging;
    using WatsonWebserver.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Exercises the local checkout import REST and MCP boundaries through a live server.</summary>
    public sealed class VesselCheckoutImportApiSuite : IArmadaTestSuite
    {
        private sealed class McpCallResponse
        {
            public McpCallResult? Result { get; set; }
        }

        private sealed class McpCallResult
        {
            public bool IsError { get; set; }
            public List<McpCallContent> Content { get; set; } = new List<McpCallContent>();
        }

        private sealed class McpCallContent
        {
            public string? Text { get; set; }
        }

        private sealed class McpToolError
        {
            public string? ErrorCode { get; set; }
        }

        private const string SuiteId = "E2E.VesselCheckoutImportApi";
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>Build the local checkout route boundary case.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor(SuiteId, "operator_scope_import_and_preserve_checkout", "Local import is global-admin operator only and vessel deletion preserves the checkout", async (CancellationToken _) => await VerifyImportBoundaryAsync().ConfigureAwait(false), new List<string> { TestTags.Positive })
            };
            return new TestSuiteDescriptor(SuiteId, "Vessel Checkout Import API", cases);
        }

        private static async Task VerifyImportBoundaryAsync()
        {
            string root = String.Empty;
            E2EServerFixture fx = await E2EServerFixture.StartIsolatedAsync(settings =>
            {
                root = Path.Combine(settings.DataDirectory, "local-checkouts");
                Directory.CreateDirectory(root);
                settings.VesselImport.AllowedRoots.Add(root);
            }).ConfigureAwait(false);
            try
            {
                string checkout = Path.Combine(root, "checkout-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(checkout);
                await InitializeCheckoutAsync(checkout).ConfigureAwait(false);

                using (HttpResponseMessage noAuth = await fx.UnauthClient.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Unauthorized, noAuth.StatusCode, "unauthenticated discovery is refused");
                using (HttpResponseMessage noAuthImport = await fx.UnauthClient.PostAsync("/api/v1/vessels/import-checkout", JsonHelper.ToJsonContent(new { checkoutPath = checkout, name = "NoAuth" })).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Unauthorized, noAuthImport.StatusCode, "unauthenticated import is refused");

                using (HttpClient tenantAdmin = await AskThreadApiTestClients.CreateBearerUserAsync(fx, true).ConfigureAwait(false))
                using (HttpClient regularUser = await AskThreadApiTestClients.CreateBearerUserAsync(fx, false).ConfigureAwait(false))
                {
                    using (HttpResponseMessage tenantDiscovery = await tenantAdmin.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.Forbidden, tenantDiscovery.StatusCode, "tenant administrators cannot inspect host checkout roots");
                    using (HttpResponseMessage tenantImport = await tenantAdmin.PostAsync("/api/v1/vessels/import-checkout", JsonHelper.ToJsonContent(new { checkoutPath = checkout, name = "TenantAdmin" })).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.Forbidden, tenantImport.StatusCode, "tenant administrators cannot import host paths");
                    using (HttpResponseMessage regularDiscovery = await regularUser.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.Forbidden, regularDiscovery.StatusCode, "regular users cannot inspect host checkout roots");
                    using (HttpResponseMessage regularImport = await regularUser.PostAsync("/api/v1/vessels/import-checkout", JsonHelper.ToJsonContent(new { checkoutPath = checkout, name = "RegularUser" })).ConfigureAwait(false))
                        AssertEqual(HttpStatusCode.Forbidden, regularImport.StatusCode, "regular users cannot import host paths");

                    using (HttpClient tenantMcp = CreateBearerClient("http://127.0.0.1:" + fx.McpPort, tenantAdmin.DefaultRequestHeaders.Authorization!.Parameter!))
                    {
                        string tenantSession = await InitializeMcpAsync(tenantMcp).ConfigureAwait(false);
                        string tools = await ListToolsAsync(tenantMcp, tenantSession).ConfigureAwait(false);
                        AssertFalse(tools.Contains("armada_discover_vessel_checkouts", StringComparison.Ordinal), "tenant administrators do not receive the host discovery tool");
                        AssertFalse(tools.Contains("armada_import_vessel_checkout", StringComparison.Ordinal), "tenant administrators do not receive the host import tool");
                    }
                    using (HttpClient regularMcp = CreateBearerClient("http://127.0.0.1:" + fx.McpPort, regularUser.DefaultRequestHeaders.Authorization!.Parameter!))
                    {
                        string regularSession = await InitializeMcpAsync(regularMcp).ConfigureAwait(false);
                        string tools = await ListToolsAsync(regularMcp, regularSession).ConfigureAwait(false);
                        AssertFalse(tools.Contains("armada_discover_vessel_checkouts", StringComparison.Ordinal), "regular users do not receive the host discovery tool");
                        AssertFalse(tools.Contains("armada_import_vessel_checkout", StringComparison.Ordinal), "regular users do not receive the host import tool");
                    }
                }

                SessionTokenService tokenService = new SessionTokenService(fx.Settings.SessionTokenEncryptionKey);
                string missionToken = tokenService.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, "mis_checkout_scope_" + Guid.NewGuid().ToString("N")).Token!;
                using (HttpClient missionRest = CreateBearerClient(fx.BaseUrl, missionToken))
                {
                    using (HttpResponseMessage missionDenied = await missionRest.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false))
                        AssertRefused(missionDenied.StatusCode, "mission credentials cannot inspect host checkout roots");
                    using (HttpResponseMessage missionImportDenied = await missionRest.PostAsync("/api/v1/vessels/import-checkout", JsonHelper.ToJsonContent(new { checkoutPath = checkout, name = "Mission" })).ConfigureAwait(false))
                        AssertRefused(missionImportDenied.StatusCode, "mission credentials cannot import host paths");
                }
                using (HttpClient missionMcp = CreateBearerClient("http://127.0.0.1:" + fx.McpPort, missionToken))
                {
                    string missionSession = await InitializeMcpAsync(missionMcp).ConfigureAwait(false);
                    string tools = await ListToolsAsync(missionMcp, missionSession).ConfigureAwait(false);
                    AssertFalse(tools.Contains("armada_discover_vessel_checkouts", StringComparison.Ordinal), "mission credentials do not receive the host discovery tool");
                    AssertFalse(tools.Contains("armada_import_vessel_checkout", StringComparison.Ordinal), "mission credentials do not receive the host import tool");
                }

                AskThread thread;
                using (HttpResponseMessage threadResponse = await fx.AuthClient.PostAsync("/api/v1/ask/threads", JsonHelper.ToJsonContent(new { title = "Checkout scope" })).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, threadResponse.StatusCode, "the human creates a thread for the scoped token control");
                    thread = await JsonHelper.DeserializeAsync<AskThread>(threadResponse).ConfigureAwait(false);
                }
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                string askToken;
                using (DatabaseDriver database = DatabaseDriverFactory.Create(fx.Settings.Database, logging))
                {
                    string requestId = "ask_" + Guid.NewGuid().ToString("N");
                    AskTurn turn = new AskTurn { ThreadId = thread.Id, RequestId = requestId, CaptainId = "cpt_checkout", Content = "Inspect a local checkout." };
                    AskTurnReservation reservation = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, turn).ConfigureAwait(false);
                    AssertEqual(AskTurnReservationResultEnum.Created, reservation.Result, "the scoped turn is saved");
                    AssertTrue(await database.AskThreads.MarkRunningAsync(thread.TenantId, thread.UserId, thread.Id, requestId).ConfigureAwait(false), "the scoped turn is active");
                    askToken = tokenService.CreateAskTurnToken(thread.TenantId, thread.UserId, thread.Id, requestId).Token!;
                }
                using (HttpClient askRest = CreateBearerClient(fx.BaseUrl, askToken))
                {
                    using (HttpResponseMessage askDenied = await askRest.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false))
                        AssertRefused(askDenied.StatusCode, "Ask-turn credentials cannot inspect host checkout roots");
                    using (HttpResponseMessage askImportDenied = await askRest.PostAsync("/api/v1/vessels/import-checkout", JsonHelper.ToJsonContent(new { checkoutPath = checkout, name = "Ask" })).ConfigureAwait(false))
                        AssertRefused(askImportDenied.StatusCode, "Ask-turn credentials cannot import host paths");
                }
                using (HttpClient askMcp = CreateBearerClient("http://127.0.0.1:" + fx.McpPort, askToken))
                {
                    string askSession = await InitializeMcpAsync(askMcp).ConfigureAwait(false);
                    string toolList = await ListToolsAsync(askMcp, askSession).ConfigureAwait(false);
                    AssertFalse(toolList.Contains("armada_discover_vessel_checkouts", StringComparison.Ordinal), "Ask catalog omits the host filesystem tool");
                    AssertFalse(toolList.Contains("armada_import_vessel_checkout", StringComparison.Ordinal), "Ask catalog omits the host import tool");
                }

                VesselCheckoutDiscovery discovery;
                using (HttpResponseMessage response = await fx.AuthClient.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, response.StatusCode, "human global administrator can discover local checkout candidates");
                    discovery = await JsonHelper.DeserializeAsync<VesselCheckoutDiscovery>(response).ConfigureAwait(false);
                }
                AssertEqual(1, discovery.Candidates.Count, "discovery returns the one direct child checkout");
                AssertEqual(Path.GetFullPath(checkout), discovery.Candidates[0].CheckoutPath, "discovery returns the canonical checkout path");

                string mcpSession = await InitializeMcpAsync(fx.McpClient).ConfigureAwait(false);
                string registeredTools = await ListToolsAsync(fx.McpClient, mcpSession).ConfigureAwait(false);
                AssertContains("armada_discover_vessel_checkouts", registeredTools, "the regular MCP catalog always registers discovery");
                AssertContains("armada_import_vessel_checkout", registeredTools, "the regular MCP catalog always registers import");
                string importedText = await CallToolAsync(fx.McpClient, mcpSession, "armada_import_vessel_checkout", new { checkoutPath = discovery.Candidates[0].CheckoutPath, name = "Imported Local Checkout" }).ConfigureAwait(false);
                Vessel imported = JsonSerializer.Deserialize<Vessel>(importedText, _JsonOptions)!;
                AssertFalse(String.IsNullOrWhiteSpace(imported.Id), "the MCP tool returns the created vessel");
                AssertEqual(Path.GetFullPath(checkout), imported.WorkingDirectory, "the vessel references the external working tree");
                AssertNull(imported.LocalPath, "the import does not claim ownership of a bare clone");

                using (HttpResponseMessage deleted = await fx.AuthClient.DeleteAsync("/api/v1/vessels/" + Uri.EscapeDataString(imported.Id)).ConfigureAwait(false))
                    AssertTrue(deleted.IsSuccessStatusCode, "the imported vessel can be deleted through MissionOperations");
                AssertTrue(Directory.Exists(checkout), "deleting the vessel preserves the external checkout");
                AssertTrue(File.Exists(Path.Combine(checkout, ".git", "refs", "heads", "main")), "deleting the vessel preserves the checkout's local branch reference");

                fx.Settings.VesselImport.AllowedRoots.Clear();
                using (HttpResponseMessage unconfiguredDiscoveryResponse = await fx.AuthClient.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, unconfiguredDiscoveryResponse.StatusCode, "discovery remains available when import roots are not configured");
                    VesselCheckoutDiscovery unconfiguredDiscovery = await JsonHelper.DeserializeAsync<VesselCheckoutDiscovery>(unconfiguredDiscoveryResponse).ConfigureAwait(false);
                    AssertFalse(unconfiguredDiscovery.Configured, "discovery reports that local checkout import is not configured");
                }

                using (HttpResponseMessage unavailableImportResponse = await fx.AuthClient.PostAsync(
                    "/api/v1/vessels/import-checkout",
                    JsonHelper.ToJsonContent(new { checkoutPath = checkout, name = "Unavailable Import Must Not Create" })).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.ServiceUnavailable, unavailableImportResponse.StatusCode, "an import without configured roots reports service unavailable");
                    string responseBody = await unavailableImportResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    JsonSerializerOptions errorJson = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new JsonStringEnumConverter() }
                    };
                    ApiErrorResponse? error = JsonSerializer.Deserialize<ApiErrorResponse>(responseBody, errorJson);
                    AssertNotNull(error, "the unavailable REST response has an error body");
                    AssertEqual(ApiResultEnum.InternalError, error!.Error, "the error body matches HTTP service unavailable");
                    AssertTrue(error.Message.StartsWith("Unavailable", StringComparison.Ordinal), "the REST error names unavailable configuration");
                }

                string mcpUnavailableBody = await SendMcpAsync(fx.McpClient, mcpSession, "tools/call", new
                {
                    name = "armada_import_vessel_checkout",
                    arguments = new { checkoutPath = checkout, name = "Unavailable MCP Import Must Not Create" }
                }).ConfigureAwait(false);
                McpCallResponse? mcpResponse = JsonSerializer.Deserialize<McpCallResponse>(mcpUnavailableBody, _JsonOptions);
                AssertNotNull(mcpResponse, "the MCP unavailable import returns a response");
                AssertNotNull(mcpResponse!.Result, "the MCP unavailable import returns a result");
                AssertTrue(mcpResponse.Result!.IsError, "the MCP unavailable import is reported as a tool error");
                AssertTrue(mcpResponse.Result.Content.Count > 0, "the MCP error result has content");
                string content = mcpResponse.Result.Content[0].Text ?? String.Empty;
                McpToolError? toolError = JsonSerializer.Deserialize<McpToolError>(content, _JsonOptions);
                AssertNotNull(toolError, "the MCP unavailable result has a typed error envelope");
                AssertEqual("Unavailable", toolError!.ErrorCode, "the MCP tool keeps the named unavailable error code");

                using (HttpResponseMessage vesselsResponse = await fx.AuthClient.GetAsync("/api/v1/vessels?pageSize=500").ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, vesselsResponse.StatusCode, "the operator can verify vessel state after refused imports");
                    EnumerationResult<Vessel> vessels = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(vesselsResponse).ConfigureAwait(false);
                    foreach (Vessel vessel in vessels.Objects)
                        AssertFalse(vessel.Name == "Unavailable Import Must Not Create" || vessel.Name == "Unavailable MCP Import Must Not Create",
                            "unavailable REST and MCP imports create no vessel");
                }
            }
            finally
            {
                fx.Stop();
            }
        }

        private static async Task InitializeCheckoutAsync(string checkout)
        {
            await RunGitAsync(checkout, "init").ConfigureAwait(false);
            await RunGitAsync(checkout, "symbolic-ref", "HEAD", "refs/heads/main").ConfigureAwait(false);
            await RunGitAsync(checkout, "config", "user.email", "checkout-test@armada.test").ConfigureAwait(false);
            await RunGitAsync(checkout, "config", "user.name", "Checkout Test").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(checkout, "README.md"), "local checkout\n").ConfigureAwait(false);
            await RunGitAsync(checkout, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(checkout, "-c", "commit.gpgsign=false", "commit", "-m", "initial local checkout").ConfigureAwait(false);
        }

        private static async Task RunGitAsync(string checkout, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = checkout,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using (Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git could not start."))
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                await stdoutTask.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture setup failed: " + stderr);
            }
        }

        private static void AssertRefused(HttpStatusCode status, string message)
        {
            AssertTrue(status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden, message + ": " + status);
        }

        private static HttpClient CreateBearerClient(string baseUrl, string token)
        {
            HttpClient client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private static async Task<string> InitializeMcpAsync(HttpClient client)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            await SendMcpAsync(client, sessionId, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "checkout-import-e2e", version = "1.0" } }).ConfigureAwait(false);
            return sessionId;
        }

        private static async Task<string> ListToolsAsync(HttpClient client, string sessionId)
        {
            return await SendMcpAsync(client, sessionId, "tools/list", new { }).ConfigureAwait(false);
        }

        private static async Task<string> CallToolAsync(HttpClient client, string sessionId, string name, object arguments)
        {
            string response = await SendMcpAsync(client, sessionId, "tools/call", new { name, arguments }).ConfigureAwait(false);
            JsonArray? content = JsonNode.Parse(response)?["result"]?["content"]?.AsArray();
            if (content == null || content.Count == 0) throw new InvalidOperationException("MCP tool result has no content.");
            return content[0]?["text"]?.GetValue<string>() ?? String.Empty;
        }

        private static async Task<string> SendMcpAsync(HttpClient client, string sessionId, string method, object parameters)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
            {
                request.Content = JsonHelper.ToJsonContent(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
                request.Headers.Add("X-Session-Id", sessionId);
                request.Headers.Add("Accept", "application/json, text/event-stream");
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Assert(response.IsSuccessStatusCode, "MCP request failed with " + response.StatusCode + ": " + body);
                    if (!String.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) return body;
                    foreach (string line in body.Split('\n'))
                        if (line.StartsWith("data:", StringComparison.Ordinal)) return line.Substring(5).Trim();
                    throw new InvalidOperationException("MCP event stream has no data event.");
                }
            }
        }
    }
}
