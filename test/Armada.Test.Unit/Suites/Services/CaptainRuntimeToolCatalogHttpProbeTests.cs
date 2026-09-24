namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests the captain MCP connectivity probe against Streamable HTTP servers, which may answer with a plain
    /// JSON body or with a text/event-stream body framed as data lines, and the Mux captain inventory, which
    /// must list the MCP servers a mission launch delivers rather than the servers in the captain's config
    /// directory.
    /// </summary>
    public class CaptainRuntimeToolCatalogHttpProbeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Runtime Tool Catalog HTTP Probe";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("HTTP probe advertises both JSON and event-stream in Accept", async () =>
            {
                ScriptedMcpHandler handler = new ScriptedMcpHandler(sse: false);
                List<CaptainToolSummary> tools = await ProbeAsync(handler).ConfigureAwait(false);

                AssertEqual(1, tools.Count, "a JSON-answering server lists its tool");
                AssertTrue(handler.AcceptHeaders.Count > 0, "the probe sent requests");
                foreach (string accept in handler.AcceptHeaders)
                {
                    AssertContains("application/json", accept);
                    AssertContains("text/event-stream", accept);
                }
            }).ConfigureAwait(false);

            await RunTest("HTTP probe reads tools from a plain JSON response body", async () =>
            {
                List<CaptainToolSummary> tools = await ProbeAsync(new ScriptedMcpHandler(sse: false)).ConfigureAwait(false);
                AssertEqual("armada_status", tools.Single().Name, "tool name from the JSON body");
            }).ConfigureAwait(false);

            await RunTest("HTTP probe reads tools from an SSE data-framed response body", async () =>
            {
                List<CaptainToolSummary> tools = await ProbeAsync(new ScriptedMcpHandler(sse: true)).ConfigureAwait(false);
                AssertEqual(1, tools.Count, "an SSE-answering server must not read as unreachable");
                AssertEqual("armada_status", tools.Single().Name, "tool name from the SSE data line");
            }).ConfigureAwait(false);
            await RunTest("Mux inventory lists the MCP servers the mission launch delivers, not the config directory's", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_mux_inventory_" + Guid.NewGuid().ToString("N"));
                string configDirectory = Path.Combine(root, "mux-config");
                Directory.CreateDirectory(configDirectory);
                try
                {
                    // `mux print` never loads the config directory's servers file, so a server listed only there
                    // is one no mission can reach.
                    File.WriteAllText(Path.Combine(configDirectory, "mcp-servers.json"),
                        "{\"servers\":[{\"name\":\"host-only\",\"transport\":\"http\",\"url\":\"http://127.0.0.1:9\",\"mcpPath\":\"/mcp\"}]}");

                    ArmadaSettings settings = new ArmadaSettings { LogDirectory = Path.Combine(root, "logs"), McpPort = 7891 };
                    Captain captain = new Captain("mux-captain")
                    {
                        Runtime = AgentRuntimeEnum.Mux,
                        RuntimeOptionsJson = "{\"configDirectory\":" + System.Text.Json.JsonSerializer.Serialize(configDirectory) + "}"
                    };
                    ScriptedMcpHandler handler = new ScriptedMcpHandler(sse: false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (HttpClient client = new HttpClient(handler))
                    {
                        Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Mux inventory mission")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);
                        captain.CurrentMissionId = mission.Id;

                        LoggingModule logging = new LoggingModule();
                        logging.Settings.EnableConsole = false;
                        CaptainRuntimeToolCatalogService service = new CaptainRuntimeToolCatalogService(
                            logging, settings, client, root, new FixedSessionTokens("mission-owner-token"));

                        CaptainRuntimeToolCatalogService.RuntimeToolCatalogSnapshot? delivered = await service.TryDescribeAsync(captain, testDb.Driver).ConfigureAwait(false);
                        AssertNotNull(delivered, "inventory");
                        List<string> servers = delivered!.Servers.Where(s => s.SourceKind == "McpServer").Select(s => s.Name).ToList();
                        AssertEqual("armada", String.Join(",", servers), "MCP servers listed (" + delivered.Summary + ")");
                        AssertTrue(delivered.Tools.Any(t => t.Name == "armada_status"), "the delivered Armada server's tools are listed");
                        AssertFalse(handler.RequestUris.Any(u => u.Contains("127.0.0.1:9", StringComparison.Ordinal)), "a server only the config directory names is never contacted");
                        AssertTrue(handler.RequestUris.All(u => u == "http://localhost:7891/mcp"), "the probe targets the launch MCP endpoint");
                        AssertTrue(handler.Authorizations.Count > 0 && handler.Authorizations.All(a => a == "Bearer mission-owner-token"), "the probe presents the mission owner's credential the launch carries");

                        // With dock MCP delivery disabled the launch passes no --mcp-config, so the mission has no MCP server.
                        settings.SeedDockRuntimeMcpConfig = false;
                        CaptainRuntimeToolCatalogService.RuntimeToolCatalogSnapshot? none = await service.TryDescribeAsync(captain, testDb.Driver).ConfigureAwait(false);
                        AssertNotNull(none, "inventory without delivery");
                        AssertEqual(0, none!.Servers.Count(s => s.SourceKind == "McpServer"), "MCP servers listed without delivery (" + none.Summary + ")");
                    }
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch (IOException) { }
                }
            }).ConfigureAwait(false);
        }

        private static async Task<List<CaptainToolSummary>> ProbeAsync(ScriptedMcpHandler handler)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            using (HttpClient client = new HttpClient(handler))
            {
                CaptainRuntimeToolCatalogService service = new CaptainRuntimeToolCatalogService(logging, null, client);
                CaptainRuntimeToolCatalogService.RuntimeMcpServerDefinition server = new CaptainRuntimeToolCatalogService.RuntimeMcpServerDefinition
                {
                    Name = "armada",
                    TransportType = "http",
                    Url = "http://127.0.0.1:1/mcp"
                };
                return await service.ProbeHttpToolsAsync(server, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private sealed class ScriptedMcpHandler : HttpMessageHandler
        {
            private readonly bool _Sse;

            public ScriptedMcpHandler(bool sse)
            {
                _Sse = sse;
            }

            public List<string> AcceptHeaders { get; } = new List<string>();

            public List<string> RequestUris { get; } = new List<string>();

            public List<string> Authorizations { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                AcceptHeaders.Add(String.Join(", ", request.Headers.Accept.Select(item => item.ToString())));
                RequestUris.Add(request.RequestUri?.ToString() ?? String.Empty);
                Authorizations.Add(request.Headers.Authorization?.ToString() ?? String.Empty);
                string body = request.Content == null ? String.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (body.Contains("notifications/initialized", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(String.Empty) };

                string json = body.Contains("\"initialize\"", StringComparison.Ordinal)
                    ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}}}"
                    : "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"armada_status\",\"description\":\"Status\"}]}}";

                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content = _Sse
                    ? new StringContent("event: message\ndata: " + json + "\n\n", Encoding.UTF8, "text/event-stream")
                    : new StringContent(json, Encoding.UTF8, "application/json");
                return response;
            }
        }

        private sealed class FixedSessionTokens : ISessionTokenService
        {
            private readonly string _Token;

            public FixedSessionTokens(string token)
            {
                _Token = token;
            }

            public AuthenticateResult CreateToken(string tenantId, string userId)
            {
                return new AuthenticateResult { Success = true, Token = _Token };
            }

            public AuthContext? ValidateToken(string encryptedToken)
            {
                return null;
            }
        }
    }
}
