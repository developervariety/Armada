namespace Armada.Test.Unit.Suites.Services
{
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Linq;
    using Armada.Core.Settings;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
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
                CaptainToolService service = new CaptainToolService(logging, database.Driver, new ArmadaSettings(), http);
                CaptainToolAccessResult result = await service.DescribeAsync(new Captain { Id = "cpt_custom", Name = "Custom", Runtime = AgentRuntimeEnum.Custom });
                AssertEqual("unsupported-runtime", result.AvailabilitySource);
                AssertFalse(result.McpConnectionPlanned);
            });

            await RunTest("ApiEndpointCaptain_ReportsWorkspaceToolsWithoutArmadaMcp", async () =>
            {
                using TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync();
                using HttpClient http = new HttpClient(new McpHandler(new[] { "armada_status" }));
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                CaptainToolService service = new CaptainToolService(logging, database.Driver, new ArmadaSettings(), http);
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
                CaptainToolService service = new CaptainToolService(logging, database.Driver, settings, plannedHttp);
                CaptainToolAccessResult planned = await service.DescribeAsync(captain, plannedAsk: true);
                AssertTrue(planned.McpConnectionPlanned, "Ask context must use the planned probe for a busy captain.");
                AssertEqual(1, planned.ArmadaToolCount);
                using HttpClient activeHttp = new HttpClient(new McpHandler(new[] { "unexpected" }));
                CaptainToolService activeService = new CaptainToolService(logging, database.Driver, settings, activeHttp);
                CaptainToolAccessResult active = await activeService.DescribeAsync(captain);
                AssertFalse(active.McpConnectionPlanned, "Default captain tools context must inspect active runtime state.");
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
            CaptainToolService service = new CaptainToolService(logging, database.Driver, settings, http);
            Captain captain = new Captain { Id = "cpt_discovery", Name = "Discovery", Runtime = AgentRuntimeEnum.ClaudeCode };
            return await service.DescribeAsync(captain, token);
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
