namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Linq;
    using Armada.Core.Settings;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>Behavioral tests for idle Ask MCP preflight discovery.</summary>
    public sealed class CaptainToolServiceDiscoveryTests : TestSuite
    {
        public override string Name => "Captain Tool Service Discovery";

        protected override async Task RunTestsAsync()
        {
            await RunTest("IdleAskPreflight_ReachableReportsPlannedTools", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                using HttpClient http = new HttpClient(new McpHandler(new[] { "armada_status" }));
                CaptainToolAccessResult result = await DescribeAsync(database, http);
                AssertTrue(result.AvailabilityVerified, "Preflight must verify the endpoint response.");
                AssertTrue(result.McpConnectionPlanned, "Idle Ask must be marked as planned.");
                AssertEqual(1, result.ArmadaToolCount);
                AssertEqual(1, result.ReachableServerCount);
            });

            await RunTest("IdleAskPreflight_RefusedReportsUnreachable", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                using HttpClient http = new HttpClient(new McpHandler(null, true));
                CaptainToolAccessResult result = await DescribeAsync(database, http);
                AssertTrue(result.AvailabilityVerified, "A refused probe is a verified unavailable result.");
                AssertTrue(result.McpConnectionPlanned, "Idle Ask must remain marked as planned.");
                AssertEqual(0, result.ArmadaToolCount);
                AssertEqual(0, result.ReachableServerCount);
                AssertContains("preflight failed", result.Summary);
            });

            await RunTest("IdleAskPreflight_CustomRuntimeIsUnsupported", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                using HttpClient http = new HttpClient(new McpHandler(new[] { "unexpected" }));
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                CaptainToolService service = new CaptainToolService(logging, database.Driver, new ArmadaSettings(), http, NewProfileDirectory());
                CaptainToolAccessResult result = await service.DescribeAsync(new Captain { Id = "cpt_custom", Name = "Custom", Runtime = AgentRuntimeEnum.Custom });
                AssertEqual("unsupported-runtime", result.AvailabilitySource);
                AssertFalse(result.McpConnectionPlanned);
            });

            await RunTest("ApiEndpointCaptain_ReportsWorkspaceToolsWithoutArmadaMcp", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                using HttpClient http = new HttpClient(new McpHandler(new[] { "armada_status" }));
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                CaptainToolService service = new CaptainToolService(logging, database.Driver, new ArmadaSettings(), http, NewProfileDirectory());
                Captain captain = new Captain { Id = "cpt_api", Name = "Api", Runtime = AgentRuntimeEnum.ApiEndpoint };
                foreach (bool plannedAsk in new[] { true, false })
                {
                    CaptainToolAccessResult result = await service.DescribeAsync(captain, plannedAsk: plannedAsk);
                    AssertFalse(result.McpConnectionPlanned, "An API captain has no MCP connection to plan.");
                    AssertEqual(0, result.ArmadaToolCount, "An API captain must not report Armada MCP tools.");
                    AssertTrue(result.AvailabilityVerified, "The API captain tool list is the runtime's own registry.");
                    AssertEqual("api-endpoint-workspace-tools", result.AvailabilitySource);
                    AssertTrue(result.Tools.Any(tool => tool.Name == "read_file"), "Workspace tools must be listed.");
                    AssertFalse(result.Tools.Any(tool => tool.Name.StartsWith("armada_", StringComparison.Ordinal)), "No Armada administrative tool may be listed.");
                    AssertFalse(result.Tools.Any(tool => tool.Name == "run_process"), "No shell tool may be listed.");
                    AssertEqual(result.Tools.Count, result.EffectiveToolCount ?? -1);
                }
            });

            await RunTest("ApiEndpointCaptain_PreflightWithCallerListsCallerScopedArmadaMcpTools", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(database, logging);
                await using ArmadaMcpHttpServer server = fixture.CreateServer();
                await server.StartAsync();

                Captain? captain = await database.Driver.Captains.ReadAsync(fixture.CaptainId);
                AssertNotNull(captain, "The fixture API-endpoint captain exists.");
                // A non-admin caller of the captain's own tenant and user: the endpoint scopes MCP to this caller.
                AuthContext caller = AuthContext.Authenticated(fixture.TenantAId, fixture.UserAId, false, false, "Session");

                CaptainToolService service = new CaptainToolService(logging, database.Driver, fixture.Settings, null, NewProfileDirectory(), fixture.SessionTokens);
                foreach (bool plannedAsk in new[] { true, false })
                {
                    CaptainToolAccessResult result = await service.DescribeAsync(captain!, plannedAsk: plannedAsk, caller: caller);

                    AssertEqual("api-endpoint-caller-mcp", result.AvailabilitySource, "The report describes the caller MCP access (plannedAsk=" + plannedAsk + ").");
                    AssertTrue(result.McpConnectionPlanned, "The chat connects to Armada MCP for this caller.");
                    List<string> mcpTools = result.Tools.Where(tool => tool.SourceKind == "McpServer").Select(tool => tool.Name).ToList();
                    AssertTrue(result.ArmadaToolCount > 0, "The caller is offered at least one Armada MCP tool.");
                    AssertEqual(mcpTools.Count, result.ArmadaToolCount, "The Armada tool count is the MCP tools the caller is offered.");
                    AssertTrue(mcpTools.Contains("get_memory"), "A caller-scoped MCP tool is reported as an MCP tool.");
                    AssertFalse(mcpTools.Contains("armada_stop_server"), "An operator tool the access policy refuses this caller is not reported.");
                    AssertTrue(result.Tools.Any(tool => tool.Name == "read_file"), "Workspace tools stay listed beside the MCP tools.");
                    AssertFalse(result.Tools.Any(tool => tool.Name == "run_process"), "No shell tool may be listed.");
                    AssertEqual(result.Tools.Count, result.EffectiveToolCount ?? -1, "Every listed tool is an effective tool.");
                }
            });

            await RunTest("ApiEndpointCaptain_PreflightWithoutCallerReportsWorkspaceRegistryAlone", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(database, logging);
                await using ArmadaMcpHttpServer server = fixture.CreateServer();
                await server.StartAsync();

                Captain? captain = await database.Driver.Captains.ReadAsync(fixture.CaptainId);
                AssertNotNull(captain, "The fixture API-endpoint captain exists.");
                CaptainToolService service = new CaptainToolService(logging, database.Driver, fixture.Settings, null, NewProfileDirectory(), fixture.SessionTokens);
                foreach (bool plannedAsk in new[] { true, false })
                {
                    CaptainToolAccessResult result = await service.DescribeAsync(captain!, plannedAsk: plannedAsk, caller: null);

                    AssertEqual("api-endpoint-workspace-tools", result.AvailabilitySource, "Without a caller the report is the workspace registry (plannedAsk=" + plannedAsk + ").");
                    AssertFalse(result.McpConnectionPlanned, "No MCP connection is planned without a caller.");
                    AssertEqual(0, result.ArmadaToolCount, "No Armada MCP tool is reported without a caller.");
                    AssertTrue(result.Tools.Any(tool => tool.Name == "read_file"), "Workspace tools stay listed.");
                    AssertFalse(result.Tools.Any(tool => tool.SourceKind == "McpServer"), "No MCP tool is listed without a caller.");
                }

                AssertEqual(0, fixture.SeenCredentials().Count, "The report never contacts MCP without a caller.");
            });

            await RunTest("IdleAskPreflight_ViewerScoped_ListsViewerToolsNotOperatorCatalog", async () =>
            {
                // A planned Ask preflight for a non-ApiEndpoint captain probes Armada MCP with the requesting
                // viewer's own scoped session token, so it lists only the tools that viewer may use. Before the
                // fix it probed with the admiral launch credential (global admin) and reported the whole operator
                // catalog to any viewer -- a cross-tenant escalation.
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(database, logging);
                await using ArmadaMcpHttpServer server = fixture.CreateServer();
                await server.StartAsync();

                Captain captain = new Captain { Id = "cpt_ask_cli", Name = "AskCli", Runtime = AgentRuntimeEnum.ClaudeCode };
                // A tenant admin of tenant A who could dispatch a mission, but is not a global operator.
                AuthContext caller = AuthContext.Authenticated(fixture.TenantAId, fixture.UserAId, false, true, "Session");
                CaptainToolService service = new CaptainToolService(logging, database.Driver, fixture.Settings, null, NewProfileDirectory(), fixture.SessionTokens);

                foreach (bool plannedAsk in new[] { true, false })
                {
                    CaptainToolAccessResult result = await service.DescribeAsync(captain, plannedAsk: plannedAsk, caller: caller);
                    List<string> names = result.Tools.Select(tool => tool.Name).ToList();
                    AssertTrue(names.Contains("get_memory"), "The viewer's own caller-scoped tools are listed (plannedAsk=" + plannedAsk + ").");
                    AssertFalse(names.Contains("armada_stop_server"), "An operator-only tool the viewer's scope refuses is never listed (plannedAsk=" + plannedAsk + ").");
                    AssertTrue(result.ArmadaToolCount > 0, "The viewer is offered its scoped Armada tools.");
                }

                // Every probe presented the viewer's own scoped session token and never the admiral launch
                // credential, which the endpoint maps to global admin.
                List<McpRequestCredentials> seen = fixture.SeenCredentials();
                AssertTrue(seen.Count > 0, "The preflight contacted MCP for the viewer.");
                foreach (McpRequestCredentials credentials in seen)
                {
                    AssertFalse(
                        credentials.Authorization != null && credentials.Authorization.Contains(McpLaunchCredential.Token, StringComparison.Ordinal),
                        "The admiral launch credential is never presented by the preflight.");
                }
            });

            await RunTest("IdleAskPreflight_NoViewerScope_FailsClosed_NeverLaunchCredential", async () =>
            {
                // With no issuable viewer scope (no session-token service), the preflight fails closed: it
                // presents no credential and lists no Armada tool. Before the fix it fell back to the admiral
                // launch credential and reported the operator catalog.
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(database, logging);
                await using ArmadaMcpHttpServer server = fixture.CreateServer();
                await server.StartAsync();

                Captain captain = new Captain { Id = "cpt_ask_cli2", Name = "AskCli2", Runtime = AgentRuntimeEnum.ClaudeCode };
                AuthContext caller = AuthContext.Authenticated(fixture.TenantAId, fixture.UserAId, false, true, "Session");
                // No session-token service is supplied, so no caller-scoped credential can be issued.
                CaptainToolService service = new CaptainToolService(logging, database.Driver, fixture.Settings, null, NewProfileDirectory());

                foreach (bool plannedAsk in new[] { true, false })
                {
                    CaptainToolAccessResult result = await service.DescribeAsync(captain, plannedAsk: plannedAsk, caller: caller);
                    AssertEqual("ask-launch-plan-no-viewer-scope", result.AvailabilitySource, "The preflight fails closed with no viewer scope (plannedAsk=" + plannedAsk + ").");
                    AssertEqual(0, result.ArmadaToolCount, "No Armada tool is listed without a viewer scope.");
                    AssertFalse(result.Tools.Any(tool => tool.Name.StartsWith("armada_", StringComparison.Ordinal)), "No operator tool is listed.");
                }

                AssertEqual(0, fixture.SeenCredentials().Count, "The preflight never contacts MCP, so it never presents the launch credential.");
            });

            await RunTest("IdleAskPreflight_CancellationPropagates", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                using HttpClient http = new HttpClient(new McpHandler(null, false, true));
                using CancellationTokenSource source = new CancellationTokenSource();
                Task<CaptainToolAccessResult> pending = DescribeAsync(database, http, source.Token);
                source.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(() => pending);
            });

            await RunTest("BusyCaptain_AskContextUsesPlannedProbe_DefaultUsesActiveContext", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                ArmadaSettings settings = new ArmadaSettings();
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                Captain captain = new Captain { Id = "cpt_busy", Name = "Busy", Runtime = AgentRuntimeEnum.ClaudeCode, CurrentMissionId = "msn_active" };
                using HttpClient plannedHttp = new HttpClient(new McpHandler(new[] { "armada_status" }));
                string profile = NewProfileDirectory();
                CaptainToolService service = new CaptainToolService(logging, database.Driver, settings, plannedHttp, profile, new SessionTokenService());
                AuthContext plannedCaller = AuthContext.Authenticated("tenant-viewer", "user-viewer", false, false, "Session");
                CaptainToolAccessResult planned = await service.DescribeAsync(captain, plannedAsk: true, caller: plannedCaller);
                AssertTrue(planned.McpConnectionPlanned, "Ask context must use the planned probe for a busy captain.");
                AssertEqual(1, planned.ArmadaToolCount);
                using HttpClient activeHttp = new HttpClient(new McpHandler(new[] { "unexpected" }));
                CaptainToolService activeService = new CaptainToolService(logging, database.Driver, settings, activeHttp, profile);
                CaptainToolAccessResult active = await activeService.DescribeAsync(captain);
                AssertFalse(active.McpConnectionPlanned, "Default captain tools context must inspect active runtime state.");
            });

            await RunTest("DiscoveryCases_NeverReadOrStartTheProcessUserProfileServers", async () =>
            {
                string home = NewProfileDirectory();
                string marker = Path.Combine(home, "sentinel-started");
                string script = Path.Combine(home, "sentinel-server.sh");
                File.WriteAllText(script, "#!/bin/sh\ntouch '" + marker + "'\n");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string config = "{\"mcpServers\":{\"home-profile-sentinel\":{\"command\":\"" + script + "\",\"args\":[]}}}";
                File.WriteAllText(Path.Combine(home, ".claude.json"), config);
                Directory.CreateDirectory(Path.Combine(home, ".gemini"));
                File.WriteAllText(Path.Combine(home, ".gemini", "settings.json"), config);

                string? previousHome = Environment.GetEnvironmentVariable("HOME");
                Environment.SetEnvironmentVariable("HOME", home);
                try
                {
                    AssertEqual(home, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "the process user profile is the sentinel home");
                    using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                    using HttpClient http = new HttpClient(new McpHandler(new[] { "armada_status" }));
                    foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Gemini })
                    {
                        Captain busy = new Captain { Id = "cpt_sentinel", Name = "Sentinel", Runtime = runtime, CurrentMissionId = "msn_active" };
                        CaptainToolAccessResult result = await NewService(database, new ArmadaSettings(), http).DescribeAsync(busy);
                        AssertFalse(result.Servers.Any(s => s.Name == "home-profile-sentinel"), runtime + " discovery listed a server from the process user profile");
                    }
                    AssertFalse(File.Exists(marker), "discovery started an MCP server from the process user profile");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("HOME", previousHome);
                }
            });

            await RunTest("BusyClaudeCaptain_ReadsOnlyTheSuppliedProfileConfiguration", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                string profile = NewProfileDirectory();
                string missingCommand = Path.Combine(profile, "no-such-mcp-server");
                File.WriteAllText(Path.Combine(profile, ".claude.json"),
                    "{\"mcpServers\":{\"supplied-profile-probe\":{\"command\":\"" + missingCommand.Replace("\\", "\\\\") + "\",\"args\":[]}}}");
                using HttpClient http = new HttpClient(new McpHandler(new[] { "unexpected" }));
                CaptainToolService service = new CaptainToolService(logging, database.Driver, new ArmadaSettings(), http, profile);
                Captain captain = new Captain { Id = "cpt_profile", Name = "Profile", Runtime = AgentRuntimeEnum.ClaudeCode, CurrentMissionId = "msn_active" };

                CaptainToolAccessResult result = await service.DescribeAsync(captain);

                var mcpServers = result.Servers.Where(s => s.SourceKind == "McpServer").ToList();
                AssertEqual(1, mcpServers.Count, "Only the supplied profile's servers are listed: " + String.Join(", ", mcpServers.Select(s => s.Name)));
                AssertEqual("supplied-profile-probe", mcpServers[0].Name);
                // The supplied command is a path to a nonexistent file, so the stdio probe cannot start
                // the process on any platform. Assert on the configured server's own reachability, not the
                // aggregate ReachableServerCount: the aggregate also counts the runtime built-in tool
                // inventory, which is legitimately reachable wherever the runtime CLI is installed (the
                // deploy host) and absent where it is not (a developer workstation) -- so pinning the
                // aggregate to zero passes only where the CLI happens to be missing.
                AssertFalse(mcpServers[0].Reachable, "The supplied server's missing command cannot start.");
                AssertEqual(0, result.Servers.Count(s => s.SourceKind == "McpServer" && s.Reachable), "No configured MCP server is reachable.");
            });

            await RunTest("IdleAskPreflight_EmptyToolsReportsVerifiedZero", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                using HttpClient http = new HttpClient(new McpHandler(Array.Empty<string>()));
                CaptainToolAccessResult result = await DescribeAsync(database, http);
                AssertTrue(result.AvailabilityVerified, "An empty tools/list response is verified.");
                AssertTrue(result.McpConnectionPlanned, "Idle Ask must remain marked as planned.");
                AssertEqual(0, result.ArmadaToolCount);
                AssertEqual(1, result.ReachableServerCount);
            });
        }

        private static async Task<CaptainToolAccessResult> DescribeAsync(TestDatabase database, HttpClient http, CancellationToken token = default)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ArmadaSettings settings = new ArmadaSettings();
            settings.McpPort = 7891;
            // A planned Ask preflight probes the endpoint with the requesting viewer's own scoped session token,
            // so the service needs a session-token service and an authenticated caller to issue one.
            CaptainToolService service = new CaptainToolService(logging, database.Driver, settings, http, NewProfileDirectory(), new SessionTokenService());
            Captain captain = new Captain { Id = "cpt_discovery", Name = "Discovery", Runtime = AgentRuntimeEnum.ClaudeCode };
            AuthContext caller = AuthContext.Authenticated("tenant-viewer", "user-viewer", false, false, "Session");
            return await service.DescribeAsync(captain, token, caller: caller);
        }

        /// <summary>
        /// The service every discovery case uses: an empty user profile, never the process user's own.
        /// </summary>
        private static CaptainToolService NewService(TestDatabase database, ArmadaSettings settings, HttpClient http)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CaptainToolService(logging, database.Driver, settings, http, NewProfileDirectory());
        }

        /// <summary>
        /// An empty user profile for one service. The default profile is the developer's own, whose configured MCP
        /// servers a busy-captain describe would read and start.
        /// </summary>
        private static string NewProfileDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "armada-tool-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private sealed class McpHandler : HttpMessageHandler
        {
            private readonly string[]? _ToolNames;
            private readonly bool _Refuse;
            private readonly bool _Cancel;

            public McpHandler(string[]? toolNames, bool refuse = false, bool cancel = false) { _ToolNames = toolNames; _Refuse = refuse; _Cancel = cancel; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (_Refuse) throw new HttpRequestException("MCP endpoint refused connection.");
                if (_Cancel) return CancelAsync(token);
                string body = request.Content == null ? String.Empty : request.Content.ReadAsStringAsync(token).GetAwaiter().GetResult();
                if (body.Contains("\"id\":1", StringComparison.Ordinal))
                    return Task.FromResult(Response("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"serverInfo\":{\"name\":\"armada\",\"version\":\"test\"}}}"));
                if (body.Contains("notifications/initialized", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
                string tools = String.Join(",", (_ToolNames ?? Array.Empty<string>()).Select(name => "{\"name\":\"" + name + "\",\"description\":\"test\",\"inputSchema\":{\"type\":\"object\"}}"));
                return Task.FromResult(Response("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[" + tools + "]}}"));
            }

            private static async Task<HttpResponseMessage> CancelAsync(CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Cancellation did not propagate.");
            }

            private static HttpResponseMessage Response(string content) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }
}
