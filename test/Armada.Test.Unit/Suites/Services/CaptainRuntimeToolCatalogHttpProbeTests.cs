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

            if (OperatingSystem.IsWindows())
            {
                SkipTest("Mux inventory lists the built-in tools entry from the endpoint config without a provider call", "the fake mux CLI is a POSIX shell script on PATH");
            }
            else await RunTest("Mux inventory lists the built-in tools entry from the endpoint config without a provider call", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_mux_builtin_" + Guid.NewGuid().ToString("N"));
                string configDirectory = Path.Combine(root, "mux-config");
                string shimDirectory = Path.Combine(root, "bin");
                string argsFile = Path.Combine(root, "mux-args.txt");
                Directory.CreateDirectory(configDirectory);
                Directory.CreateDirectory(shimDirectory);
                string originalPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
                try
                {
                    WriteFakeMux(shimDirectory, argsFile);
                    Environment.SetEnvironmentVariable("PATH", shimDirectory + Path.PathSeparator + originalPath);

                    // The provider base URL is a port nothing listens on and the HTTP handler records every
                    // request, so any completion request would show up in one of the two logs.
                    File.WriteAllText(Path.Combine(configDirectory, "endpoints.json"),
                        "{ \"endpoints\": [\n" +
                        "  { \"name\": \"other\", \"adapterType\": \"ollama\", \"baseUrl\": \"http://127.0.0.1:2/other\", \"model\": \"m0\", \"isDefault\": true },\n" +
                        "  { \"name\": \"fleet\", \"adapterType\": \"openai-compatible\", \"baseUrl\": \"http://127.0.0.1:3/v1\", \"model\": \"m1\" },\n" +
                        "  { \"name\": \"no-tools\", \"adapterType\": \"openai-compatible\", \"baseUrl\": \"http://127.0.0.1:4/v1\", \"model\": \"m2\", \"quirks\": { \"supportsTools\": false } }\n" +
                        "] }");

                    ArmadaSettings settings = new ArmadaSettings { LogDirectory = Path.Combine(root, "logs"), McpPort = 7892, SeedDockRuntimeMcpConfig = false };
                    ScriptedMcpHandler handler = new ScriptedMcpHandler(sse: false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (HttpClient client = new HttpClient(handler))
                    {
                        Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Mux built-in mission")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                        LoggingModule logging = new LoggingModule();
                        logging.Settings.EnableConsole = false;
                        CaptainRuntimeToolCatalogService service = new CaptainRuntimeToolCatalogService(
                            logging, settings, client, root, new FixedSessionTokens("mission-owner-token"));

                        CaptainToolServerSummary enabled = await DescribeMuxBuiltInAsync(service, testDb, mission, configDirectory, "fleet").ConfigureAwait(false);
                        AssertTrue(enabled.Enabled, "tool calling is on when the endpoint's quirks do not disable it");
                        AssertEqual("http://127.0.0.1:3/v1", enabled.Url, "base URL of the named endpoint, not the default one");
                        AssertContains("openai-compatible", enabled.Target);
                        AssertContains("not reported by mux --version", enabled.Status);
                        AssertTrue(enabled.Reachable, "the Mux CLI answered and tool calling is on");

                        CaptainToolServerSummary disabled = await DescribeMuxBuiltInAsync(service, testDb, mission, configDirectory, "no-tools").ConfigureAwait(false);
                        AssertFalse(disabled.Enabled, "quirks.supportsTools false disables tool calling");
                        AssertEqual("Tool calling disabled on this endpoint", disabled.Status, "disabled status");

                        CaptainToolServerSummary missing = await DescribeMuxBuiltInAsync(service, testDb, mission, configDirectory, "absent").ConfigureAwait(false);
                        AssertFalse(missing.Enabled, "an endpoint endpoints.json does not name is not reported as enabled");
                        AssertContains("absent", missing.ErrorMessage ?? String.Empty);
                    }

                    List<string> invocations = File.Exists(argsFile) ? File.ReadAllLines(argsFile).ToList() : new List<string>();
                    AssertEqual(3, invocations.Count, "one Mux CLI call per inventory");
                    AssertTrue(invocations.All(line => line == "--version"), "the only Mux CLI call is the version check, never print or probe: " + String.Join(" / ", invocations));
                    AssertFalse(handler.RequestUris.Any(u => u.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)), "no request reaches a provider base URL");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PATH", originalPath);
                    try { Directory.Delete(root, true); } catch (IOException) { }
                }
            }).ConfigureAwait(false);
        }

        private async Task<CaptainToolServerSummary> DescribeMuxBuiltInAsync(
            CaptainRuntimeToolCatalogService service,
            TestDatabase testDb,
            Mission mission,
            string configDirectory,
            string endpoint)
        {
            Captain captain = new Captain("mux-captain-" + endpoint)
            {
                Runtime = AgentRuntimeEnum.Mux,
                CurrentMissionId = mission.Id,
                RuntimeOptionsJson = "{\"configDirectory\":" + System.Text.Json.JsonSerializer.Serialize(configDirectory) +
                    ",\"endpoint\":" + System.Text.Json.JsonSerializer.Serialize(endpoint) + "}"
            };

            CaptainRuntimeToolCatalogService.RuntimeToolCatalogSnapshot? snapshot = await service.TryDescribeAsync(captain, testDb.Driver).ConfigureAwait(false);
            AssertNotNull(snapshot, "inventory for endpoint " + endpoint);
            CaptainToolServerSummary? builtIn = snapshot!.Servers.FirstOrDefault(s => s.Name == "Mux Built-In Tools");
            AssertNotNull(builtIn, "the Mux Built-In Tools entry is listed for endpoint " + endpoint + " (" + snapshot.Summary + ")");
            AssertEqual("RuntimeBuiltIn", builtIn!.SourceKind, "entry source kind");
            AssertContains("not reported by `mux --version`", snapshot.Summary);
            return builtIn;
        }

        private static void WriteFakeMux(string directory, string argsFile)
        {
            string shim = Path.Combine(directory, "mux");
            File.WriteAllText(shim, "#!/bin/sh\necho \"$*\" >> '" + argsFile + "'\necho 0.0.0-test\n");
            File.SetUnixFileMode(shim,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
