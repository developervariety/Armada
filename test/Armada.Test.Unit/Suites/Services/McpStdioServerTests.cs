namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Helm.Commands;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the Armada-owned MCP stdio transport.
    /// </summary>
    public class McpStdioServerTests : TestSuite
    {
        /// <summary>
        /// Gets the suite name.
        /// </summary>
        public override string Name => "MCP Stdio Server";

        /// <summary>
        /// Runs MCP stdio transport tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FramedInitialize_ReturnsLineDelimitedServerInfo", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"codex\",\"version\":\"1.0\"}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement result = response.GetProperty("result");
                JsonElement serverInfo = result.GetProperty("serverInfo");

                AssertEqual("2.0", response.GetProperty("jsonrpc").GetString());
                AssertEqual(1, response.GetProperty("id").GetInt32());
                AssertEqual("Armada Test", serverInfo.GetProperty("name").GetString());
                AssertEqual("9.9.9", serverInfo.GetProperty("version").GetString());
                AssertTrue(output.Length == message.TotalBytesRead, "stdout should contain only one line-delimited initialize response");
            }).ConfigureAwait(false);

            await RunTest("LineDelimitedInitialize_ReturnsLineDelimitedServerInfo", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"codex\",\"version\":\"1.0\"}}}\n";

                byte[] output = await RunServerAsync(server, Encoding.UTF8.GetBytes(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertEqual("2.0", response.GetProperty("jsonrpc").GetString());
                AssertEqual(11, response.GetProperty("id").GetInt32());
                AssertEqual("Armada Test", response.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                AssertFalse(message.Body.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase), "stdout should be bare JSON-RPC, not Content-Length framed");
                AssertTrue(output.Length == message.TotalBytesRead, "stdout should contain only one line-delimited initialize response");
            }).ConfigureAwait(false);

            await RunTest("FramedInitialize_WithUtf8Payload_UsesByteContentLength", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cod\u00e9x\",\"version\":\"1.0\"}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertEqual(12, response.GetProperty("id").GetInt32());
                AssertEqual("Armada Test", response.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                AssertTrue(output.Length == message.TotalBytesRead, "stdout should contain only one line-delimited initialize response");
            }).ConfigureAwait(false);

            await RunTest("FramedToolsList_ReturnsRegisteredTools", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_test_tool",
                    "Test tool",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            value = new { type = "string" }
                        }
                    },
                    args => Task.FromResult((object)new { Status = "ok" }));

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"tools-1\",\"method\":\"tools/list\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(ReadSingleMessage(output).Body);
                JsonElement tools = response.GetProperty("result").GetProperty("tools");

                AssertEqual(1, tools.GetArrayLength());
                AssertEqual("armada_test_tool", tools[0].GetProperty("name").GetString());
                AssertEqual("Test tool", tools[0].GetProperty("description").GetString());
                AssertEqual("object", tools[0].GetProperty("inputSchema").GetProperty("type").GetString());
            }).ConfigureAwait(false);

            await RunTest("StdioCatalog_HarborEnabledMatchesAuthenticatedHttpCatalog", async () =>
            {
                foreach (bool harborEnabled in new[] { false, true })
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada_stdio_catalog_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                    ArmadaServer? httpServer = null;
                    try
                    {
                        ArmadaSettings settings = CreateCatalogSettings(root, harborEnabled);
                        httpServer = CreateCatalogServer(settings);
                        await httpServer.StartAsync().ConfigureAwait(false);

                        List<CatalogTool> httpTools = await ReadHttpCatalogAsync(settings).ConfigureAwait(false);
                        List<CatalogTool> stdioTools;
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            LoggingModule logging = CreateQuietLogging();
                            settings.CodeIndex.Enabled = false;
                            GitService git = new GitService(logging, database: testDb.Driver);
                            CodeIndexService codeIndex = new CodeIndexService(logging, testDb.Driver, settings, git);
                            Dictionary<string, CatalogRegistration> registrations = new Dictionary<string, CatalogRegistration>(StringComparer.Ordinal);
                            McpStdioCommand.RegisterTools(
                                (name, description, schema, handler) => registrations.Add(name, new CatalogRegistration(name, description, schema, handler)),
                                testDb.Driver,
                                settings,
                                logging,
                                git,
                                codeIndex);

                            ArmadaMcpStdioServer stdio = CreateServer();
                            foreach (CatalogRegistration registration in registrations.Values)
                            {
                                stdio.RegisterTool(registration.Name, registration.Description, registration.InputSchema, registration.Handler);
                            }

                            string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}";
                            byte[] output;
                            using (McpCallerContext.Begin(AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, true, true, "LocalStdio", null, "Local stdio operator")))
                            {
                                output = await RunServerAsync(stdio, CreateFrame(request)).ConfigureAwait(false);
                            }
                            StdioRpcResponse response = JsonSerializer.Deserialize<StdioRpcResponse>(ReadSingleMessage(output).Body, CatalogJsonOptions())
                                ?? throw new InvalidDataException("The stdio tools/list response was empty.");
                            stdioTools = response.Result?.Tools ?? new List<CatalogTool>();
                        }

                        List<CatalogTool> orderedHttp = httpTools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
                        List<CatalogTool> orderedStdio = stdioTools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
                        AssertEqual(orderedHttp.Count, orderedHttp.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count(), "HTTP catalog names must be unique");
                        AssertEqual(orderedStdio.Count, orderedStdio.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count(), "stdio catalog names must be unique");
                        AssertEqual(String.Join("\n", orderedHttp.Select(tool => tool.Name)), String.Join("\n", orderedStdio.Select(tool => tool.Name)), "stdio and HTTP must expose the same full tool names");
                        for (int index = 0; index < orderedHttp.Count; index++)
                        {
                            AssertEqual(JsonSerializer.Serialize(orderedHttp[index].InputSchema), JsonSerializer.Serialize(orderedStdio[index].InputSchema), "input schema for " + orderedHttp[index].Name);
                        }
                    }
                    finally
                    {
                        if (httpServer != null) httpServer.Stop();
                        DeleteCatalogDirectory(root);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("StdioCatalog_UnavailableProcessServicesRemainVisibleAndNamed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateQuietLogging();
                    string root = Path.Combine(Path.GetTempPath(), "armada_stdio_unavailable_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                    try
                    {
                        ArmadaSettings settings = new ArmadaSettings();
                        settings.CodeIndex.Enabled = false;
                        settings.DocksDirectory = Path.Combine(root, "docks");
                        settings.ReposDirectory = Path.Combine(root, "repos");
                        settings.LogDirectory = Path.Combine(root, "logs");
                        GitService git = new GitService(logging, database: testDb.Driver);
                        CodeIndexService codeIndex = new CodeIndexService(logging, testDb.Driver, settings, git);
                        Dictionary<string, CatalogRegistration> registrations = new Dictionary<string, CatalogRegistration>(StringComparer.Ordinal);
                        McpStdioCommand.RegisterTools(
                            (name, description, schema, handler) => registrations.Add(name, new CatalogRegistration(name, description, schema, handler)),
                            testDb.Driver,
                            settings,
                            logging,
                            git,
                            codeIndex);

                        ArmadaMcpStdioServer server = CreateServer();
                        foreach (CatalogRegistration registration in registrations.Values)
                            server.RegisterTool(registration.Name, registration.Description, registration.InputSchema, registration.Handler);

                        List<UnavailableCallCase> calls = new List<UnavailableCallCase>
                        {
                            new UnavailableCallCase("armada_objective_scheduler_status", "{}", "Unavailable: objective scheduler"),
                            new UnavailableCallCase("armada_dispatch_hold", "{\"action\":\"status\"}", "Unavailable: dispatch hold"),
                            new UnavailableCallCase("armada_vessel_hold", "{\"action\":\"status\"}", "Unavailable: vessel hold"),
                            new UnavailableCallCase("create_backlog_refinement_session", "{\"objectiveId\":\"obj_demo\",\"captainId\":\"cpt_demo\"}", "Unavailable: objective refinement"),
                            new UnavailableCallCase("create_backlog_planning_session", "{\"objectiveId\":\"obj_demo\",\"captainId\":\"cpt_demo\",\"vesselId\":\"vsl_demo\"}", "Unavailable: planning sessions"),
                            new UnavailableCallCase("armada_typed_decision_eval", "{}", "Unavailable: typed decision evaluation"),
                            new UnavailableCallCase("armada_harbor_jobs", "{}", "Unavailable: Harbor jobs"),
                            new UnavailableCallCase("armada_harbor_job_stop", "{\"jobId\":\"hjob_demo\"}", "Unavailable: Harbor jobs"),
                            new UnavailableCallCase("armada_discover_vessel_checkouts", "{}", "Unavailable: local checkout import"),
                            new UnavailableCallCase("armada_import_vessel_checkout", "{\"checkoutPath\":\"/tmp/checkout\",\"name\":\"Checkout\"}", "Unavailable: local checkout import"),
                            new UnavailableCallCase("armada_stop_server", "{}", "Unavailable: server shutdown")
                        };
                        string[] requests = calls.Select((call, index) => CreateToolCallRequest(index + 1, call.Name, call.ArgumentsJson)).ToArray();
                        byte[] output;
                        using (McpCallerContext.Begin(AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, true, true, "LocalStdio", null, "Local stdio operator")))
                        {
                            output = await RunServerAsync(server, CombineFrames(requests)).ConfigureAwait(false);
                        }

                        List<string> messages = ReadLineMessages(output);
                        AssertEqual(calls.Count, messages.Count, "stdio returns one framed response for each tool call");
                        for (int index = 0; index < calls.Count; index++)
                        {
                            StdioRpcResponse response = JsonSerializer.Deserialize<StdioRpcResponse>(messages[index], CatalogJsonOptions())
                                ?? throw new InvalidDataException("The stdio unavailable response was empty.");
                            AssertNotNull(response.Error, calls[index].Name + " must be a JSON-RPC tool error");
                            AssertEqual(-32603, response.Error!.Code, calls[index].Name + " maps an unavailable result to the stdio internal error code");
                            AssertContains(calls[index].ErrorPrefix, response.Error.Message, calls[index].Name + " keeps its named unavailable reason");
                        }
                    }
                    finally
                    {
                        DeleteCatalogDirectory(root);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("FramedToolsCall_PassesArgumentsAndReturnsContent", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string? observedValue = null;
                server.RegisterTool(
                    "armada_echo",
                    "Echo tool",
                    new { type = "object" },
                    args =>
                    {
                        if (!args.HasValue)
                            throw new InvalidDataException("handler should receive arguments.");

                        JsonElement arguments = args.Value;
                        observedValue = arguments.GetProperty("value").GetString();
                        return Task.FromResult((object)new { Status = "ok", Echo = observedValue });
                    });

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"call-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_echo\",\"arguments\":{\"value\":\"codex\"}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement content = response.GetProperty("result").GetProperty("content");
                string text = content[0].GetProperty("text").GetString()!;

                AssertEqual("codex", observedValue);
                AssertEqual("call-1", response.GetProperty("id").GetString());
                AssertEqual(1, content.GetArrayLength());
                AssertEqual("text", content[0].GetProperty("type").GetString());
                AssertContains("\"status\":\"ok\"", text);
                AssertContains("\"echo\":\"codex\"", text);
                AssertEqual(output.Length, message.TotalBytesRead, "stdout should contain only the line-delimited tools/call response");
            }).ConfigureAwait(false);

            await RunTest("FramedNotificationBeforeRequest_OnlyWritesRequestResponse", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string notification = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}";
                string request = "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"initialize\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CombineFrames(notification, request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertEqual(22, response.GetProperty("id").GetInt32());
                AssertTrue(response.TryGetProperty("result", out _), "initialize request should still receive a response");
                AssertEqual(output.Length, message.TotalBytesRead, "notification should not produce an extra stdout line");
            }).ConfigureAwait(false);

            await RunTest("Error_DoesNotWriteContentLengthFrame", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                string request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"missing/method\",\"params\":{}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertFalse(Encoding.ASCII.GetString(output, 0, Math.Min(output.Length, 15)).StartsWith("Content-Length"), "stdout should not start with an MCP frame header");
                AssertEqual(output.Length, message.TotalBytesRead, "stdout should not contain trailing text");
                AssertTrue(response.TryGetProperty("error", out JsonElement error), "unknown method should return a JSON-RPC error");
                AssertEqual(-32601, error.GetProperty("code").GetInt32());
                AssertContains("Method not found", error.GetProperty("message").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("LineInvalidJson_ReturnsLineDelimitedParseError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                byte[] input = Encoding.ASCII.GetBytes("not-json\n");

                byte[] output = await RunServerAsync(server, input).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement error = response.GetProperty("error");

                AssertEqual(-32700, error.GetProperty("code").GetInt32());
                AssertContains("Parse error", error.GetProperty("message").GetString()!);
                AssertEqual(output.Length, message.TotalBytesRead, "parser error should be line-delimited with no trailing stdout");
            }).ConfigureAwait(false);

            await RunTest("ToolCallErrorResult_ReturnsLineDelimitedInternalError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_fail",
                    "Failing tool",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Error = "tool failed" }));

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"fail-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_fail\",\"arguments\":{}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement error = response.GetProperty("error");

                AssertEqual("fail-1", response.GetProperty("id").GetString());
                AssertEqual(-32603, error.GetProperty("code").GetInt32());
                AssertContains("tool failed", error.GetProperty("message").GetString()!);
                AssertEqual(output.Length, message.TotalBytesRead, "tool errors should not write trailing stdout");
            }).ConfigureAwait(false);

            await RunTest("ToolCallNullErrorField_ReturnsResult", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_ok",
                    "Successful tool whose payload carries an unset Error field",
                    new { type = "object" },
                    args => Task.FromResult((object)new { Status = "ok", Error = (string?)null }));

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"ok-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_ok\",\"arguments\":{}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);

                AssertFalse(response.TryGetProperty("error", out _), "a null Error field is not a tool error: " + message.Body);
                AssertContains("ok", response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("ToolCallExplicitIsErrorResult_ReturnsInternalError", async () =>
            {
                ArmadaMcpStdioServer server = CreateServer();
                server.RegisterTool(
                    "armada_refuse",
                    "Tool that returns an explicit protocol error result",
                    new { type = "object" },
                    args => Task.FromResult((object)new
                    {
                        content = new[] { new { type = "text", text = "explicit refusal" } },
                        isError = true
                    }));

                string request = "{\"jsonrpc\":\"2.0\",\"id\":\"refuse-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"armada_refuse\",\"arguments\":{}}}";

                byte[] output = await RunServerAsync(server, CreateFrame(request)).ConfigureAwait(false);
                MessageReadResult message = ReadSingleMessage(output);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(message.Body);
                JsonElement error = response.GetProperty("error");

                AssertEqual(-32603, error.GetProperty("code").GetInt32());
                AssertContains("explicit refusal", error.GetProperty("message").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("McpConfigHelper_CodexUsesStdioAndClaudeRemainsHttp", () =>
            {
                string helper = ReadRepositoryFile("src", "Armada.Helm", "Commands", "McpConfigHelper.cs");

                AssertContains("\"mcp\", \"add\", \"armada\", \"--\"", helper, "Codex install should use a subprocess separator for stdio");
                AssertContains("BuildCodexStdioCommandParts()", helper, "Codex install should be backed by the stdio command builder");
                AssertContains("\"armada\", \"mcp\", \"stdio\"", helper, "Codex fallback command should launch Armada stdio");
                AssertContains("private const int CodexMcpStartupTimeoutSeconds = 120;", helper, "Codex install should pin a startup timeout for cold stdio launches");
                AssertContains("EnsureTomlMcpServerStartupTimeoutAsync", helper, "Codex native CLI install should patch startup_timeout_sec after adding the server");
                AssertContains("claude mcp add --transport http --scope user armada", helper, "Claude default install should remain HTTP");
                AssertContains("claude mcp add --scope user armada -- armada mcp stdio", helper, "Claude stdio alternative should use the fixed Armada stdio path");
            }).ConfigureAwait(false);

            await RunTest("StdioHost_TransitionMissionStatusTool_AppliesTransition", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    string root = Path.Combine(Path.GetTempPath(), "armada_stdio_host_" + Guid.NewGuid().ToString("N"));
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(root, "docks");
                    settings.ReposDirectory = Path.Combine(root, "repos");
                    settings.LogDirectory = Path.Combine(root, "logs");

                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                    McpStdioCommand.RegisterTools(
                        (name, _, _, handler) => tools[name] = handler,
                        testDb.Driver,
                        settings,
                        logging,
                        new GitService(logging, database: testDb.Driver),
                        null);
                    AssertTrue(tools.ContainsKey("armada_transition_mission_status"), "the stdio host registers the transition tool");

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("stdio-transition-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("stdio transition", "")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>> transition = McpTestCaller.Wrap(tools["armada_transition_mission_status"]);
                    object result = await transition(JsonSerializer.SerializeToElement(new { missionId = mission.Id, status = "Cancelled" })).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains(MissionStatusTransitionService.UnavailableMessage), "the stdio host supplies the transition service: " + resultJson);
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Cancelled, stored!.Status, "the transition is applied through the stdio host: " + resultJson);
                }
            }).ConfigureAwait(false);

            await RunTest("StdioHost_SuppliesTheRecordBackedServicesTheAdmiralEndpointSupplies", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = RegisterStdioTools(testDb);

                    string[] serviceTools = new[]
                    {
                        "armada_review_hold",
                        "armada_handoff_hold",
                        "armada_agentwake_status",
                        "create_release",
                        "create_deployment",
                        "list_runbooks",
                        "armada_bench_captain",
                        "armada_unlanded_branches",
                        "armada_reconcile_terminal_voyage_missions",
                        "armada_disk_lifecycle",
                        "armada_trace",
                        "armada_trace_backfill"
                    };
                    List<string> missing = new List<string>();
                    foreach (string name in serviceTools)
                    {
                        if (!tools.ContainsKey(name)) missing.Add(name);
                    }
                    AssertTrue(missing.Count == 0, "the stdio host registers every record-backed service tool; missing: " + String.Join(", ", missing));

                    Mission held = await testDb.Driver.Missions.CreateAsync(new Mission("stdio held judge", "")
                    {
                        Status = MissionStatusEnum.WorkProduced,
                        HeldForOperatorReview = true,
                        HeldForOperatorReviewReason = "review_substance"
                    }).ConfigureAwait(false);
                    object holdResult = await McpTestCaller.Wrap(tools["armada_review_hold"])(JsonSerializer.SerializeToElement(new
                    {
                        action = "fail",
                        missionId = held.Id,
                        reason = "stdio review",
                        @operator = "stdio-operator"
                    })).ConfigureAwait(false);
                    string holdJson = JsonSerializer.Serialize(holdResult);
                    Mission? afterHold = await testDb.Driver.Missions.ReadAsync(held.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, afterHold!.Status, "the review hold is resolved through the stdio host: " + holdJson);
                    AssertFalse(afterHold.HeldForOperatorReview, "the hold is cleared: " + holdJson);

                    object wakeResult = await McpTestCaller.Wrap(tools["armada_agentwake_status"])(JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
                    string wakeJson = JsonSerializer.Serialize(wakeResult);
                    AssertFalse(wakeJson.Contains("Remote trigger service not configured", StringComparison.Ordinal), "the stdio host supplies the remote trigger service: " + wakeJson);
                }
            }).ConfigureAwait(false);
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterStdioTools(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            string root = Path.Combine(Path.GetTempPath(), "armada_stdio_host_" + Guid.NewGuid().ToString("N"));
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(root, "docks");
            settings.ReposDirectory = Path.Combine(root, "repos");
            settings.LogDirectory = Path.Combine(root, "logs");

            Dictionary<string, Func<JsonElement?, Task<object>>> tools = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
            McpStdioCommand.RegisterTools(
                (name, _, _, handler) => tools[name] = handler,
                testDb.Driver,
                settings,
                logging,
                new GitService(logging, database: testDb.Driver),
                null);
            return tools;
        }

        private static ArmadaSettings CreateCatalogSettings(string root, bool harborEnabled)
        {
            DatabaseSettings database = new DatabaseSettings
            {
                Type = DatabaseTypeEnum.Sqlite,
                Filename = Path.Combine(root, "armada.db")
            };
            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = root,
                DatabasePath = database.Filename,
                Database = database,
                LogDirectory = Path.Combine(root, "logs"),
                DocksDirectory = Path.Combine(root, "docks"),
                ReposDirectory = Path.Combine(root, "repos"),
                AdmiralPort = FreePort(),
                McpPort = FreePort(),
                ApiKey = "stdio-catalog-" + Guid.NewGuid().ToString("N"),
                HeartbeatIntervalSeconds = 300,
                WebSocketEnabled = true
            };
            settings.Rest.Hostname = "127.0.0.1";
            settings.AutonomousObjectiveScheduler.Enabled = false;
            settings.Harbor.Enabled = harborEnabled;
            settings.CodeIndex.Enabled = false;
            settings.SettingsFilePath = Path.Combine(root, "settings.json");
            settings.InitializeDirectories();
            return settings;
        }

        private static ArmadaServer CreateCatalogServer(ArmadaSettings settings)
        {
            LoggingModule logging = CreateQuietLogging();
            return new ArmadaServer(logging, settings, quiet: true);
        }

        private static async Task<List<CatalogTool>> ReadHttpCatalogAsync(ArmadaSettings settings)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                string endpoint = "http://127.0.0.1:" + settings.McpPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/mcp";
                int id = 0;
                CatalogHttpSession session = new CatalogHttpSession();
                await SendHttpMcpRequestAsync(client, endpoint, "initialize", new Dictionary<string, object>
                {
                    ["protocolVersion"] = "2025-03-26",
                    ["capabilities"] = new Dictionary<string, object>(),
                    ["clientInfo"] = new Dictionary<string, object> { ["name"] = "stdio-catalog-test", ["version"] = "1.0" }
                }, ++id, session).ConfigureAwait(false);

                List<CatalogTool> tools = new List<CatalogTool>();
                string? cursor = null;
                do
                {
                    Dictionary<string, object> parameters = new Dictionary<string, object>();
                    if (cursor != null) parameters["cursor"] = cursor;
                    string json = await SendHttpMcpRequestAsync(client, endpoint, "tools/list", parameters, ++id, session).ConfigureAwait(false);
                    StdioRpcResponse response = JsonSerializer.Deserialize<StdioRpcResponse>(json, CatalogJsonOptions())
                        ?? throw new InvalidDataException("The HTTP tools/list response was empty.");
                    if (response.Error != null) throw new InvalidDataException("HTTP tools/list failed: " + response.Error.Message);
                    if (response.Result?.Tools != null) tools.AddRange(response.Result.Tools);
                    cursor = response.Result?.NextCursor;
                }
                while (!String.IsNullOrWhiteSpace(cursor));

                return tools;
            }
        }

        private static async Task<string> SendHttpMcpRequestAsync(
            HttpClient client,
            string endpoint,
            string method,
            object parameters,
            int id,
            CatalogHttpSession session)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            };
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Content = new StringContent(JsonSerializer.Serialize(payload, CatalogJsonOptions()), Encoding.UTF8, "application/json");
                if (!String.IsNullOrWhiteSpace(session.SessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.SessionId);
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw new InvalidDataException("HTTP MCP returned status " + (int)response.StatusCode + ": " + body);
                    if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? sessionValues))
                        session.SessionId = sessionValues.FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? session.SessionId;
                    string trimmed = body.Trim();
                    if (trimmed.StartsWith("{", StringComparison.Ordinal)) return trimmed;
                    foreach (string line in body.Split('\n'))
                    {
                        string item = line.Trim();
                        if (item.StartsWith("data:", StringComparison.Ordinal))
                        {
                            string data = item.Substring(5).Trim();
                            if (data.StartsWith("{", StringComparison.Ordinal)) return data;
                        }
                    }
                }
            }
            throw new InvalidDataException("HTTP MCP response had no JSON-RPC envelope.");
        }

        private static JsonSerializerOptions CatalogJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            };
        }

        private static LoggingModule CreateQuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static int FreePort()
        {
            using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }

        private static void DeleteCatalogDirectory(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        private sealed class CatalogRegistration
        {
            public CatalogRegistration(string name, string description, object inputSchema, Func<JsonElement?, Task<object>> handler)
            {
                Name = name;
                Description = description;
                InputSchema = inputSchema;
                Handler = handler;
            }

            public string Name { get; }

            public string Description { get; }

            public object InputSchema { get; }

            public Func<JsonElement?, Task<object>> Handler { get; }
        }

        private sealed class CatalogHttpSession
        {
            public string? SessionId { get; set; }
        }

        private sealed class StdioRpcResponse
        {
            public StdioRpcResult? Result { get; set; }

            public StdioRpcError? Error { get; set; }
        }

        private sealed class StdioRpcResult
        {
            public List<CatalogTool> Tools { get; set; } = new List<CatalogTool>();

            public string? NextCursor { get; set; }
        }

        private sealed class StdioRpcError
        {
            public int Code { get; set; }

            public string Message { get; set; } = String.Empty;
        }

        private sealed class CatalogTool
        {
            public string Name { get; set; } = String.Empty;

            public string Description { get; set; } = String.Empty;

            public JsonElement InputSchema { get; set; }
        }

        private sealed class UnavailableCallCase
        {
            public UnavailableCallCase(string name, string argumentsJson, string errorPrefix)
            {
                Name = name;
                ArgumentsJson = argumentsJson;
                ErrorPrefix = errorPrefix;
            }

            public string Name { get; }

            public string ArgumentsJson { get; }

            public string ErrorPrefix { get; }
        }

        private static ArmadaMcpStdioServer CreateServer()
        {
            return new ArmadaMcpStdioServer
            {
                ServerName = "Armada Test",
                ServerVersion = "9.9.9"
            };
        }

        private static async Task<byte[]> RunServerAsync(ArmadaMcpStdioServer server, byte[] inputBytes)
        {
            using MemoryStream input = new MemoryStream(inputBytes);
            using MemoryStream output = new MemoryStream();
            await server.RunAsync(input, output).ConfigureAwait(false);
            return output.ToArray();
        }

        private static byte[] CreateFrame(string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            byte[] headerBytes = Encoding.ASCII.GetBytes("Content-Length: " + bodyBytes.Length + "\r\n\r\n");
            byte[] frame = new byte[headerBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, frame, 0, headerBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, frame, headerBytes.Length, bodyBytes.Length);
            return frame;
        }

        private static byte[] CombineFrames(params string[] bodies)
        {
            using MemoryStream stream = new MemoryStream();
            foreach (string body in bodies)
            {
                byte[] frame = CreateFrame(body);
                stream.Write(frame, 0, frame.Length);
            }

            return stream.ToArray();
        }

        private static string CreateToolCallRequest(int id, string name, string argumentsJson)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"method\":\"tools/call\",\"params\":{\"name\":" + JsonSerializer.Serialize(name)
                + ",\"arguments\":" + argumentsJson + "}}";
        }

        private static List<string> ReadLineMessages(byte[] output)
        {
            string text = Encoding.UTF8.GetString(output);
            List<string> messages = new List<string>();
            foreach (string line in text.Split('\n'))
            {
                string message = line.TrimEnd('\r');
                if (!String.IsNullOrWhiteSpace(message)) messages.Add(message);
            }
            return messages;
        }

        private static MessageReadResult ReadSingleMessage(byte[] output)
        {
            string text = Encoding.UTF8.GetString(output);
            int newlineIndex = text.IndexOf('\n');
            if (newlineIndex < 0)
                throw new InvalidDataException("No line-delimited MCP response found.");

            string body = text.Substring(0, newlineIndex).TrimEnd('\r');
            string trailing = text.Substring(newlineIndex + 1);
            if (!String.IsNullOrWhiteSpace(trailing))
                throw new InvalidDataException("Unexpected extra line-delimited MCP response.");

            int bytesRead = Encoding.UTF8.GetByteCount(text.Substring(0, newlineIndex + 1));
            return new MessageReadResult(body, bytesRead);
        }

        private static string ReadRepositoryFile(params string[] segments)
        {
            string path = FindRepositoryRoot();
            foreach (string segment in segments)
            {
                path = Path.Combine(path, segment);
            }

            return File.ReadAllText(path);
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Armada.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidDataException("Unable to find repository root.");
        }

        private sealed record MessageReadResult(string Body, int TotalBytesRead);
    }
}
