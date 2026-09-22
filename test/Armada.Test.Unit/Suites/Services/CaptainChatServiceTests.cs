namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using Armada.Runtimes.Mcp;
    using Armada.Server;
    using Armada.Server.Mcp;
    using PolyPrompt.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for how Ask Armada assembles a captain chat reply from runtime output.
    /// </summary>
    public class CaptainChatServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Chat Service";

        /// <summary>
        /// Captured opencode --format json events: a tool call followed by the assistant answer.
        /// </summary>
        internal static readonly string[] OpenCodeToolThenAnswer = new string[]
        {
            "{\"type\":\"step_start\",\"part\":{\"type\":\"step-start\"}}",
            "{\"type\":\"tool_use\",\"timestamp\":1781834748604,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"callID\":\"read_0\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"src/File.cs\"},\"output\":\"<path>...</path>\"},\"title\":\"\"}}",
            "{\"type\":\"step_finish\",\"part\":{\"reason\":\"tool-calls\",\"type\":\"step-finish\"}}",
            "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"Here are the entries\"}}",
            "{\"type\":\"step_finish\",\"part\":{\"reason\":\"stop\",\"type\":\"step-finish\"}}"
        };

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Chat runtime configuration is written only inside its scoped directory", () =>
            {
                string baseDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_chat_scope_" + Guid.NewGuid().ToString("N"));
                string scoped = System.IO.Path.Combine(baseDirectory, "runtime-config");
                try
                {
                    CaptainLaunchIsolationPlan inside = new CaptainLaunchIsolationPlan();
                    inside.FilesToWrite.Add(new IsolationConfigFile { RelativePath = ".gemini/settings.json", Contents = "{}" });
                    CaptainChatService.MaterializeIsolationPlan(inside, scoped + System.IO.Path.DirectorySeparatorChar);
                    AssertTrue(System.IO.File.Exists(System.IO.Path.Combine(scoped, ".gemini", "settings.json")),
                        "a file below the scoped directory is written, also when the directory is given with a trailing separator");

                    CaptainLaunchIsolationPlan sibling = new CaptainLaunchIsolationPlan();
                    sibling.FilesToWrite.Add(new IsolationConfigFile { RelativePath = "../runtime-config-backup/settings.json", Contents = "{}" });
                    AssertThrows<InvalidOperationException>(() => CaptainChatService.MaterializeIsolationPlan(sibling, scoped));
                    AssertFalse(System.IO.Directory.Exists(System.IO.Path.Combine(baseDirectory, "runtime-config-backup")),
                        "nothing is written to a sibling directory that shares the scoped directory's name prefix");
                }
                finally
                {
                    if (System.IO.Directory.Exists(baseDirectory)) System.IO.Directory.Delete(baseDirectory, recursive: true);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An OpenCode chat reply contains only the answer, not tool activity records", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-opencode", AgentRuntimeEnum.OpenCode);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<string> records = new OpenCodeRecordTransform(logging).Records(OpenCodeToolThenAnswer);
                    ReplayRuntimeFactory factory = new ReplayRuntimeFactory(logging, records);
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging);
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "List the entries" }).ConfigureAwait(false);

                    Console.WriteLine("CHAT reply: " + (response.Reply ?? "<null>").Replace("\n", "\\n") + " | error: " + (response.Error ?? "<none>"));
                    AssertTrue(response.Success, "The chat turn succeeds");
                    AssertEqual("Here are the entries", response.Reply, "The reply is the answer text only");
                    AssertFalse((response.Reply ?? String.Empty).Contains(ActivityRecords.ActivityMarker, StringComparison.Ordinal), "No activity record reaches the reply");
                }
            }).ConfigureAwait(false);

            await RunTest("ChatAsync_ApiEndpointCaptainUsesTheLaunchAdmissionRule", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("ChatAdmissionTenant")).ConfigureAwait(false);
                    UserMaster owner = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "owner@chat.test", "pass")).ConfigureAwait(false);
                    UserMaster other = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "other@chat.test", "pass")).ConfigureAwait(false);
                    ModelEndpoint privateEndpoint = new ModelEndpoint
                    {
                        Name = "private-endpoint",
                        TenantId = tenant.Id,
                        UserId = owner.Id,
                        Scope = ScopeEnum.UserSpecific,
                        Provider = ModelProviderEnum.OpenAICompatible,
                        Kind = ModelEndpointKindEnum.Inference,
                        BaseUrl = "http://127.0.0.1:1",
                        Model = "fixture-model",
                        Enabled = true
                    };
                    await testDb.Driver.ModelEndpoints.CreateAsync(privateEndpoint).ConfigureAwait(false);
                    Captain captain = new Captain("chat-api-other-user", AgentRuntimeEnum.ApiEndpoint)
                    {
                        TenantId = tenant.Id,
                        UserId = other.Id,
                        ModelEndpointId = privateEndpoint.Id,
                        Model = "fixture-model"
                    };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    RecordingEndpointRuntimeFactory factory = new RecordingEndpointRuntimeFactory(logging);
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, new ArmadaSettings());
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);
                    AssertFalse(response.Success, "Chat must refuse an endpoint the captain may not use.");
                    AssertContains("not available to this captain", response.Error ?? String.Empty, "Chat must return the launch admission reason.");
                    AssertEqual(0, factory.EndpointCreations, "No API runtime may be created for a refused endpoint.");

                    privateEndpoint.Scope = ScopeEnum.TenantWide;
                    privateEndpoint.Enabled = false;
                    await testDb.Driver.ModelEndpoints.UpdateAsync(privateEndpoint).ConfigureAwait(false);
                    response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);
                    AssertFalse(response.Success, "Chat must refuse a disabled endpoint.");
                    AssertContains("disabled", response.Error ?? String.Empty, "Chat must return the disabled admission reason.");
                    AssertEqual(0, factory.EndpointCreations, "No API runtime may be created for a disabled endpoint.");
                }
            }).ConfigureAwait(false);

            await RunTest("ChatAsync_ApiEndpointCaptainIsOfferedNoCommandTool", async () =>
            {
                // A chat caller is a different principal from a dispatched mission. The command tool is switched
                // on only by the mission launch path; if chat ever offered it, a dashboard user would hold a shell
                // in the admiral's container. This is the regression that would do that.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("ChatCommandTenant")).ConfigureAwait(false);
                    UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "chat-command@chat.test", "pass")).ConfigureAwait(false);
                    ModelEndpoint endpoint = new ModelEndpoint
                    {
                        Name = "chat-command-endpoint",
                        TenantId = tenant.Id,
                        UserId = user.Id,
                        Scope = ScopeEnum.TenantWide,
                        Provider = ModelProviderEnum.OpenAICompatible,
                        Kind = ModelEndpointKindEnum.Inference,
                        BaseUrl = "http://127.0.0.1:1",
                        Model = "fixture-model",
                        Enabled = true
                    };
                    await testDb.Driver.ModelEndpoints.CreateAsync(endpoint).ConfigureAwait(false);
                    Captain captain = new Captain("chat-api-command", AgentRuntimeEnum.ApiEndpoint)
                    {
                        TenantId = tenant.Id,
                        UserId = user.Id,
                        ModelEndpointId = endpoint.Id,
                        Model = "fixture-model"
                    };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    OfferRecordingEndpointRuntimeFactory factory = new OfferRecordingEndpointRuntimeFactory(logging);
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, new ArmadaSettings());
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Run the tests for me." }).ConfigureAwait(false);

                    AssertTrue(response.Success, "The chat turn should complete with the scripted client: " + response.Error);
                    AssertEqual(1, factory.Runtimes.Count, "Chat must build exactly one API-endpoint runtime.");
                    AssertFalse(factory.Runtimes[0].CommandToolEnabled, "A chat turn must not switch the command tool on.");
                    AssertTrue(factory.Client.OfferedTools.Count > 0, "The model must have been offered the workspace tools.");
                    AssertFalse(factory.Client.OfferedTools.Contains("run_command"),
                        "A chat turn must not offer run_command; offered: " + String.Join(", ", factory.Client.OfferedTools));
                }
            }).ConfigureAwait(false);

            await RunTest("ChatAsync refuses when requireAccountLogin is on and the captain has no account", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-require-account", AgentRuntimeEnum.OpenCode);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    ReplayRuntimeFactory factory = new ReplayRuntimeFactory(logging, new[] { "should not run" });
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.ModelTier.UsageRouting.RequireAccountLogin = true;
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, settings);
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);
                    AssertFalse(response.Success, "Chat must refuse without an account login.");
                    AssertContains(CaptainAccountLaunch.ReasonAccountRequired, response.Error ?? String.Empty, "Chat names account_required.");
                    AssertTrue(factory.LastRuntime == null, "No runtime may start for a refused chat.");
                }
            }).ConfigureAwait(false);

            await RunTest("ChatAsync_PassesAskIsolationPlanAndCleansTemporaryConfig", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-isolation", AgentRuntimeEnum.OpenCode);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    ReplayRuntimeFactory factory = new ReplayRuntimeFactory(logging, new[] { "Ask reply" });
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, new ArmadaSettings());
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);
                    AssertTrue(response.Success, "Chat should complete with the fake runtime.");
                    AssertNotNull(factory.LastRuntime);
                    AssertNotNull(factory.LastRuntime!.ReceivedIsolationPlan);
                    AssertTrue(factory.LastRuntime.ReceivedIsolationPlan!.FilesToWrite.Any(file => file.RelativePath == "opencode.json"), "Chat must pass the OpenCode MCP plan.");
                    AssertFalse(Directory.Exists(factory.LastRuntime.ReceivedWorkingDirectory!), "Chat must clean the temporary runtime directory.");
                }
            }).ConfigureAwait(false);

            await RunTest("An API-endpoint chat lists and calls Armada MCP tools with the caller's own session credential and scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(testDb, logging).ConfigureAwait(false);
                    await using (ArmadaMcpHttpServer server = fixture.CreateServer())
                    {
                        await server.StartAsync().ConfigureAwait(false);

                        ScriptedToolChatClient client = new ScriptedToolChatClient(new ToolChatResponse[]
                        {
                            new ToolChatResponse
                            {
                                Success = true,
                                ToolCalls = new List<ToolCall>
                                {
                                    new ToolCall { Id = "call_own", Name = "get_memory", ArgumentsJson = "{\"memoryId\":\"" + fixture.OwnMemoryId + "\"}" },
                                    new ToolCall { Id = "call_foreign", Name = "get_memory", ArgumentsJson = "{\"memoryId\":\"" + fixture.ForeignMemoryId + "\"}" },
                                    new ToolCall { Id = "call_operator", Name = "armada_stop_server", ArgumentsJson = "{}" }
                                }
                            },
                            new ToolChatResponse { Success = true, Text = "Done", ToolCalls = new List<ToolCall>() }
                        }, logging);

                        CaptainChatService chat = new CaptainChatService(testDb.Driver, new ScriptedApiRuntimeFactory(logging, client), null, null, logging, fixture.Settings, fixture.SessionTokens);
                        AuthContext caller = AuthContext.Authenticated(fixture.TenantAId, fixture.UserAId, false, false, "Session");
                        CaptainChatResponse response = await chat.ChatAsync(caller, fixture.CaptainId, new CaptainChatRequest { Message = "Read my memory" }).ConfigureAwait(false);

                        AssertTrue(response.Success, "The chat turn succeeds: " + (response.Error ?? String.Empty));
                        AssertTrue(client.Requests.Count >= 2, "The model is called again with the tool results");
                        List<string> offered = client.Requests[0].Tools == null ? new List<string>() : client.Requests[0].Tools!.Select(tool => tool.Name).ToList();
                        AssertTrue(offered.Contains("get_memory"), "The caller-scoped get_memory tool is offered to the model");
                        AssertTrue(offered.Contains("search_memory"), "The caller-scoped search_memory tool is offered to the model");
                        AssertFalse(offered.Contains("armada_stop_server"), "An operator-control tool is not offered to a caller who may not use it");

                        string toolResults = JsonSerializer.Serialize(client.Requests[1].Messages);
                        AssertContains("OWN-TENANT-MEMORY", toolResults, "The caller reads its own memory through MCP");
                        AssertFalse(toolResults.Contains("OTHER-TENANT-MEMORY", StringComparison.Ordinal), "The caller cannot read another tenant's memory");
                        AssertContains("Memory not found: " + fixture.ForeignMemoryId, toolResults, "Another tenant's memory reads as not found");
                        AssertFalse(fixture.OperatorToolRan, "The operator-control tool never runs");

                        List<McpRequestCredentials> seen = fixture.SeenCredentials();
                        AssertTrue(seen.Count > 0, "The runtime reached the MCP endpoint");
                        foreach (McpRequestCredentials credentials in seen)
                        {
                            AssertTrue(String.IsNullOrEmpty(credentials.Authorization), "The runtime never presents an Authorization header, so the launch credential cannot reach MCP");
                            AssertTrue(String.IsNullOrEmpty(credentials.ApiKey), "The runtime never presents an API key");
                            AssertFalse(String.IsNullOrEmpty(credentials.SessionToken), "Every MCP request carries the caller's session token");
                            AssertFalse(McpLaunchCredential.Matches(credentials.SessionToken), "The session token is not the admiral launch credential");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("An API-endpoint chat without an authenticated caller reaches no Armada MCP tool", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(testDb, logging).ConfigureAwait(false);
                    await using (ArmadaMcpHttpServer server = fixture.CreateServer())
                    {
                        await server.StartAsync().ConfigureAwait(false);

                        ScriptedToolChatClient client = new ScriptedToolChatClient(new ToolChatResponse[]
                        {
                            new ToolChatResponse
                            {
                                Success = true,
                                ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_own", Name = "get_memory", ArgumentsJson = "{\"memoryId\":\"" + fixture.OwnMemoryId + "\"}" } }
                            },
                            new ToolChatResponse { Success = true, Text = "Done", ToolCalls = new List<ToolCall>() }
                        }, logging);

                        CaptainChatService chat = new CaptainChatService(testDb.Driver, new ScriptedApiRuntimeFactory(logging, client), null, null, logging, fixture.Settings, fixture.SessionTokens);
                        CaptainChatResponse response = await chat.ChatAsync(fixture.CaptainId, new CaptainChatRequest { Message = "Read memory" }).ConfigureAwait(false);

                        AssertTrue(response.Success, "The chat turn still completes with the workspace tools: " + (response.Error ?? String.Empty));
                        List<string> offered = client.Requests[0].Tools == null ? new List<string>() : client.Requests[0].Tools!.Select(tool => tool.Name).ToList();
                        AssertFalse(offered.Contains("get_memory"), "No MCP tool is offered without a caller");
                        AssertEqual(0, fixture.SeenCredentials().Count, "The runtime never contacts MCP without a caller credential");
                        AssertFalse(JsonSerializer.Serialize(client.Requests[1].Messages).Contains("OWN-TENANT-MEMORY", StringComparison.Ordinal), "No memory is read without a caller");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("The MCP tool client is refused without a credential and with a forged session token", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(testDb, logging).ConfigureAwait(false);
                    await using (ArmadaMcpHttpServer server = fixture.CreateServer())
                    {
                        await server.StartAsync().ConfigureAwait(false);
                        string url = ArmadaMcpConfigBuilder.GetMcpUrl(fixture.Settings.McpPort);

                        foreach (string? presented in new string?[] { null, "forged-session-token" })
                        {
                            using (McpToolClient client = new McpToolClient(url, presented, logging))
                            {
                                McpClientException? refused = null;
                                try
                                {
                                    await client.ListToolsAsync().ConfigureAwait(false);
                                }
                                catch (McpClientException ex)
                                {
                                    refused = ex;
                                }

                                AssertNotNull(refused, "Listing tools is refused for credential: " + (presented ?? "<none>"));
                                AssertEqual(401, refused!.StatusCode ?? 0, "The refusal is an authentication failure");
                            }
                        }

                        AssertFalse(fixture.OperatorToolRan, "No refused request reaches a tool handler");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("A CLI chat turn carries the caller's session token, never the admiral launch credential", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    SessionTokenService sessionTokens = new SessionTokenService();
                    ArmadaSettings settings = new ArmadaSettings { McpPort = 51789 };

                    // A CLI captain is a real external process that reads its MCP credential from an environment
                    // variable named by its scoped configuration. Every CLI runtime must carry the caller's own
                    // session token there for a chat turn, never the admiral launch credential.
                    foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex, AgentRuntimeEnum.Gemini, AgentRuntimeEnum.Cursor, AgentRuntimeEnum.OpenCode, AgentRuntimeEnum.Mux })
                    {
                        Captain captain = new Captain("chat-cli-" + runtime, runtime);
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        ReplayRuntimeFactory factory = new ReplayRuntimeFactory(logging, new[] { "reply" });
                        CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, settings, sessionTokens);
                        AuthContext caller = AuthContext.Authenticated("tenant-cli", "user-cli", false, false, "Session");
                        // The reply content is irrelevant here; the launched runtime records the isolation plan the
                        // chat turn built when StartAsync is called, so assert on that plan regardless of the reply.
                        await chat.ChatAsync(caller, captain.Id, new CaptainChatRequest { Message = "hello" }).ConfigureAwait(false);

                        CaptainLaunchIsolationPlan plan = factory.LastRuntime!.ReceivedIsolationPlan!;
                        AssertNotNull(plan);

                        AssertTrue(plan.EnvironmentOverrides.TryGetValue(McpCredentialReference.ChatEnvironmentVariable, out string? carried), runtime + " carries the caller token in the chat variable");
                        AssertFalse(String.IsNullOrEmpty(carried), runtime + " carries a non-empty caller token");
                        AssertFalse(plan.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable), runtime + " never sets the launch credential variable");
                        AssertFalse(plan.EnvironmentOverrides.Values.Any(value => McpLaunchCredential.Matches(value)), runtime + " never carries the launch credential value");

                        // The carried token authenticates as the caller, so the endpoint scopes the turn to it.
                        AuthContext? validated = sessionTokens.ValidateToken(carried!);
                        AssertNotNull(validated);
                        AssertEqual("tenant-cli", validated!.TenantId, runtime + " token resolves to the caller's tenant");
                        AssertEqual("user-cli", validated.UserId, runtime + " token resolves to the caller's user");
                    }

                    // A chat turn with no authenticated caller carries no token and reaches no MCP tool.
                    Captain anon = new Captain("chat-cli-anon", AgentRuntimeEnum.OpenCode);
                    await testDb.Driver.Captains.CreateAsync(anon).ConfigureAwait(false);
                    ReplayRuntimeFactory anonFactory = new ReplayRuntimeFactory(logging, new[] { "reply" });
                    CaptainChatService anonChat = new CaptainChatService(testDb.Driver, anonFactory, null, null, logging, settings, sessionTokens);
                    CaptainChatResponse anonResponse = await anonChat.ChatAsync(anon.Id, new CaptainChatRequest { Message = "hello" }).ConfigureAwait(false);
                    AssertTrue(anonResponse.Success, "The anonymous chat turn still completes");
                    CaptainLaunchIsolationPlan anonPlan = anonFactory.LastRuntime!.ReceivedIsolationPlan!;
                    AssertFalse(anonPlan.EnvironmentOverrides.ContainsKey(McpCredentialReference.ChatEnvironmentVariable), "An anonymous chat turn sets no caller token");
                    AssertFalse(anonPlan.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable), "An anonymous chat turn never sets the launch credential");
                }
            }).ConfigureAwait(false);

            await RunTest("A CLI chat captain reaching MCP with the plan's credential gets only the caller's scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(testDb, logging).ConfigureAwait(false);
                    await using (ArmadaMcpHttpServer server = fixture.CreateServer())
                    {
                        await server.StartAsync().ConfigureAwait(false);
                        string url = ArmadaMcpConfigBuilder.GetMcpUrl(fixture.Settings.McpPort);

                        // Build the isolation plan a CLI (Claude Code) chat turn produces for this caller, then act as
                        // the launched captain: read the credential the plan puts in the environment and present it in
                        // the Authorization bearer header, exactly as the runtime's scoped MCP config would.
                        AuthenticateResult issued = fixture.SessionTokens.CreateToken(fixture.TenantAId, fixture.UserAId);
                        CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(
                            AgentRuntimeEnum.ClaudeCode, fixture.Settings.McpPort, "/tmp/cli-chat-scope", McpCredentialReference.ForChat(issued.Token!));
                        string bearerCredential = plan.EnvironmentOverrides[McpCredentialReference.ChatEnvironmentVariable];
                        AssertFalse(McpLaunchCredential.Matches(bearerCredential), "The plan credential is not the admiral launch credential");

                        using (BearerMcpProbe probe = new BearerMcpProbe(url, bearerCredential))
                        {
                            await probe.InitializeAsync().ConfigureAwait(false);
                            List<string> offered = await probe.ListToolNamesAsync().ConfigureAwait(false);
                            AssertTrue(offered.Contains("get_memory"), "The caller-scoped get_memory tool is offered");
                            AssertTrue(offered.Contains("search_memory"), "The caller-scoped search_memory tool is offered");
                            AssertFalse(offered.Contains("armada_stop_server"), "The operator tool is not offered to a non-admin caller");

                            BearerMcpProbe.ToolResult own = await probe.CallToolAsync("get_memory", "{\"memoryId\":\"" + fixture.OwnMemoryId + "\"}").ConfigureAwait(false);
                            AssertFalse(own.Refused, "Reading the caller's own memory is not refused");
                            AssertContains("OWN-TENANT-MEMORY", own.Text, "The caller reads its own memory through MCP");

                            BearerMcpProbe.ToolResult foreign = await probe.CallToolAsync("get_memory", "{\"memoryId\":\"" + fixture.ForeignMemoryId + "\"}").ConfigureAwait(false);
                            AssertFalse(foreign.Text.Contains("OTHER-TENANT-MEMORY", StringComparison.Ordinal), "The caller cannot read another tenant's memory");
                            AssertContains("Memory not found: " + fixture.ForeignMemoryId, foreign.Text, "Another tenant's memory reads as not found");

                            BearerMcpProbe.ToolResult operatorCall = await probe.CallToolAsync("armada_stop_server", "{}").ConfigureAwait(false);
                            AssertTrue(operatorCall.Refused, "The operator tool call is refused for a non-admin caller");
                            AssertFalse(fixture.OperatorToolRan, "The operator tool never runs");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("A CLI chat captain with no credential and with a forged token reaches no MCP tool", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(testDb, logging).ConfigureAwait(false);
                    await using (ArmadaMcpHttpServer server = fixture.CreateServer())
                    {
                        await server.StartAsync().ConfigureAwait(false);
                        string url = ArmadaMcpConfigBuilder.GetMcpUrl(fixture.Settings.McpPort);

                        foreach (string? presented in new string?[] { null, "forged-session-token" })
                        {
                            using (BearerMcpProbe probe = new BearerMcpProbe(url, presented))
                            {
                                int? refusedStatus = null;
                                try
                                {
                                    await probe.InitializeAsync().ConfigureAwait(false);
                                    await probe.ListToolNamesAsync().ConfigureAwait(false);
                                }
                                catch (BearerMcpProbe.ProbeHttpException ex)
                                {
                                    refusedStatus = ex.StatusCode;
                                }

                                AssertEqual(401, refusedStatus ?? 0, "A CLI captain is refused for credential: " + (presented ?? "<none>"));
                            }
                        }

                        AssertFalse(fixture.OperatorToolRan, "No refused request reaches a tool handler");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Non-tool activity records stay out of a chat reply", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-codex", AgentRuntimeEnum.Codex);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<string> records = new List<string> { "[ARMADA:ACTIVITY] codex error rate limited", "The answer" };
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, new ReplayRuntimeFactory(logging, records), null, null, logging);
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);

                    AssertEqual("The answer", response.Reply, "A non-tool activity record is not part of the reply");
                }
            }).ConfigureAwait(false);

            await RunTest("A canonical tool activity record reads back as name, detail and status", () =>
            {
                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/File.cs (ok)", out ToolActivityRecord read), "A finished read parses");
                AssertEqual("read", read.Name, "Name");
                AssertEqual("src/File.cs", read.Detail, "Detail");
                AssertEqual("ok", read.Status, "Status");
                AssertTrue(read.Succeeded == true, "ok succeeded");

                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool bash dotnet test (error exit 1)", out ToolActivityRecord failed), "A failed command parses");
                AssertEqual("dotnet test", failed.Detail, "Command detail");
                AssertEqual("error exit 1", failed.Status, "Error with exit code");
                AssertTrue(failed.Succeeded == false, "error did not succeed");

                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool grep", out ToolActivityRecord running), "A bare tool name parses");
                AssertNull(running.Detail, "No detail");
                AssertFalse(running.IsFinished, "No status means the call has not finished");

                AssertTrue(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/A (copy).cs (ok)", out ToolActivityRecord parenthesized), "A detail with parentheses parses");
                AssertEqual("src/A (copy).cs", parenthesized.Detail, "Parentheses inside the detail stay in the detail");
                AssertEqual("ok", parenthesized.Status, "The trailing status is read");

                AssertTrue(ActivityRecords.IsActivityRecord("[ARMADA:ACTIVITY] codex error"), "A non-tool activity record is an activity record");
                AssertFalse(ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] codex error", out ToolActivityRecord _), "A non-tool activity record is not a tool record");
                AssertFalse(ActivityRecords.IsActivityRecord("Here are the entries"), "Answer text is not an activity record");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Tool activity cards pair a started call with its completion", () =>
            {
                ChatToolActivityTracker tracker = new ChatToolActivityTracker();
                ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/A.cs", out ToolActivityRecord started);
                ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/A.cs (ok)", out ToolActivityRecord completed);
                ActivityRecords.TryParseToolActivity("[ARMADA:ACTIVITY] tool read src/B.cs (error)", out ToolActivityRecord other);

                ChatToolActivityEvent first = tracker.Next(started);
                ChatToolActivityEvent second = tracker.Next(completed);
                ChatToolActivityEvent third = tracker.Next(other);

                AssertEqual("started", first.Phase, "An unfinished call starts a card");
                AssertEqual(first.Id, second.Id, "The completion updates the same card");
                AssertEqual("completed", second.Phase, "The completion finishes the card");
                AssertTrue(second.Ok == true, "ok is success");
                AssertFalse(third.Id == first.Id, "A different call gets its own card");
                AssertTrue(third.Ok == false, "error is failure");
                AssertEqual("src/B.cs", third.Arguments, "The detail is the card argument");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// A minimal MCP Streamable HTTP probe that presents its credential in the Authorization bearer header,
        /// the way a launched CLI chat captain's scoped MCP configuration does. It exists only to exercise the
        /// server's authentication and authorization from the CLI runtime's wire shape.
        /// </summary>
        private sealed class BearerMcpProbe : IDisposable
        {
            private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            private readonly HttpClient _Http;
            private readonly string _Endpoint;
            private string? _SessionId = null;
            private int _RpcId = 0;
            private bool _Disposed = false;

            public BearerMcpProbe(string endpoint, string? bearerCredential)
            {
                _Endpoint = endpoint;
                _Http = new HttpClient();
                _Http.Timeout = TimeSpan.FromSeconds(30);
                if (!String.IsNullOrWhiteSpace(bearerCredential))
                    _Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + bearerCredential.Trim());
            }

            public async Task InitializeAsync(CancellationToken token = default)
            {
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new Dictionary<string, object>(),
                    ["clientInfo"] = new Dictionary<string, object> { ["name"] = "cli-chat-probe", ["version"] = "1.0" }
                };
                await SendAsync("initialize", parameters, token).ConfigureAwait(false);
            }

            public async Task<List<string>> ListToolNamesAsync(CancellationToken token = default)
            {
                JsonDocument document = await SendAsync("tools/list", new Dictionary<string, object>(), token).ConfigureAwait(false);
                using (document)
                {
                    List<string> names = new List<string>();
                    if (document.RootElement.TryGetProperty("result", out JsonElement result)
                        && result.TryGetProperty("tools", out JsonElement tools)
                        && tools.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement tool in tools.EnumerateArray())
                        {
                            if (tool.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                                names.Add(name.GetString() ?? String.Empty);
                        }
                    }
                    return names;
                }
            }

            public async Task<ToolResult> CallToolAsync(string name, string argumentsJson, CancellationToken token = default)
            {
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["arguments"] = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson, _JsonOptions) ?? new Dictionary<string, object>()
                };
                JsonDocument document = await SendAsync("tools/call", parameters, token).ConfigureAwait(false);
                using (document)
                {
                    JsonElement root = document.RootElement;
                    if (root.TryGetProperty("error", out JsonElement error))
                    {
                        string message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? String.Empty : String.Empty;
                        return new ToolResult { Refused = true, Text = message };
                    }

                    StringBuilder text = new StringBuilder();
                    if (root.TryGetProperty("result", out JsonElement result)
                        && result.TryGetProperty("content", out JsonElement content)
                        && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out JsonElement blockText) && blockText.ValueKind == JsonValueKind.String)
                            {
                                if (text.Length > 0) text.Append('\n');
                                text.Append(blockText.GetString());
                            }
                        }
                    }
                    return new ToolResult { Refused = false, Text = text.ToString() };
                }
            }

            private async Task<JsonDocument> SendAsync(string method, object parameters, CancellationToken token)
            {
                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = System.Threading.Interlocked.Increment(ref _RpcId),
                    ["method"] = method,
                    ["params"] = parameters
                };

                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _Endpoint))
                {
                    request.Content = new StringContent(JsonSerializer.Serialize(payload, _JsonOptions), Encoding.UTF8, "application/json");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    if (!String.IsNullOrEmpty(_SessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _SessionId);

                    using (HttpResponseMessage response = await _Http.SendAsync(request, token).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new ProbeHttpException((int)response.StatusCode);

                        if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values))
                        {
                            foreach (string value in values)
                            {
                                if (!String.IsNullOrWhiteSpace(value)) { _SessionId = value; break; }
                            }
                        }

                        string json = ExtractEnvelope(body) ?? throw new ProbeHttpException(0);
                        return JsonDocument.Parse(json);
                    }
                }
            }

            private static string? ExtractEnvelope(string body)
            {
                if (String.IsNullOrWhiteSpace(body)) return null;
                string trimmed = body.Trim();
                if (trimmed.StartsWith("{", StringComparison.Ordinal)) return trimmed;
                foreach (string rawLine in body.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line.Substring(5).Trim();
                    if (data.StartsWith("{", StringComparison.Ordinal)) return data;
                }
                return null;
            }

            public void Dispose()
            {
                if (_Disposed) return;
                _Disposed = true;
                _Http.Dispose();
            }

            /// <summary>The result of a probe tool call.</summary>
            public sealed class ToolResult
            {
                /// <summary>True when the server refused the call with a JSON-RPC error.</summary>
                public bool Refused { get; set; }

                /// <summary>The concatenated text content, or the refusal message.</summary>
                public string Text { get; set; } = String.Empty;
            }

            /// <summary>A non-success HTTP status from the MCP endpoint (for example a 401 refusal).</summary>
            public sealed class ProbeHttpException : Exception
            {
                /// <summary>Instantiate.</summary>
                /// <param name="statusCode">The HTTP status code.</param>
                public ProbeHttpException(int statusCode) : base("MCP probe HTTP " + statusCode)
                {
                    StatusCode = statusCode;
                }

                /// <summary>The HTTP status code.</summary>
                public int StatusCode { get; }
            }
        }

        private sealed class OfferRecordingEndpointRuntimeFactory : AgentRuntimeFactory
        {
            public List<ApiAgentRuntime> Runtimes { get; } = new List<ApiAgentRuntime>();

            public OfferRecordingClient Client { get; }

            public OfferRecordingEndpointRuntimeFactory(LoggingModule logging) : base(logging)
            {
                Client = new OfferRecordingClient(logging);
            }

            public override IAgentRuntime Create(ModelEndpoint endpoint)
            {
                ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 2, (ep, log) => Client);
                Runtimes.Add(runtime);
                return runtime;
            }
        }

        private sealed class OfferRecordingClient : PolyPrompt.Clients.CompletionClientBase
        {
            public OfferRecordingClient(LoggingModule logging) : base("http://127.0.0.1:1", null, logging) { }

            public List<string> OfferedTools { get; } = new List<string>();

            public override Task<ToolChatStreamingResponse> ToolChatStreamingAsync(ToolChatRequest request, CancellationToken token = default)
            {
                lock (OfferedTools)
                {
                    if (OfferedTools.Count == 0 && request.Tools != null)
                    {
                        foreach (PolyPrompt.Models.ToolDefinition tool in request.Tools) OfferedTools.Add(tool.Name);
                    }
                }

                return Task.FromResult(new ToolChatStreamingResponse { Success = true, Text = "I can only read and edit files here.", ToolCalls = new List<ToolCall>() });
            }

            public override Task<ToolChatResponse> ToolChatAsync(ToolChatRequest request, CancellationToken token = default) => throw new NotSupportedException();
            public override Task<ChatResponse> ChatAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();
            public override Task<ChatStreamingResponse> ChatStreamingAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();
            public override Task<EmbeddingResponse> EmbedAsync(string input, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();
            public override Task<EmbeddingResponse> EmbedAsync(List<string> inputs, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();
            public override Task<GenerationResponse> GenerateAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();
            public override Task<GenerationStreamingResponse> GenerateStreamingAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotSupportedException();
            public override IAsyncEnumerable<ModelInformation> ListModelsAsync(CancellationToken token = default) => throw new NotSupportedException();
            public override Task<ModelInformation?> GetModelInformationAsync(string model, CancellationToken token = default) => throw new NotSupportedException();
        }

        private sealed class RecordingEndpointRuntimeFactory : AgentRuntimeFactory
        {
            public int EndpointCreations { get; private set; }

            public RecordingEndpointRuntimeFactory(LoggingModule logging) : base(logging)
            {
            }

            public override IAgentRuntime Create(ModelEndpoint endpoint)
            {
                EndpointCreations++;
                throw new InvalidOperationException("The endpoint runtime must not be created in this test.");
            }
        }
    }
}
