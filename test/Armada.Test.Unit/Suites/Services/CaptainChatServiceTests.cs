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

        private const string DeferredDiscoveryToolName = "armada_discover_mcp_tools";

        /// <summary>A CLI runtime with no read-only mapping; reaching StartAsync is recorded and fails the launch.</summary>
        private sealed class NoReadOnlyCliRuntime : BaseAgentRuntime
        {
            public NoReadOnlyCliRuntime(LoggingModule logging) : base(logging) { }

            public override string Name => "NoReadOnlyCli";

            public override bool SupportsResume => false;

            public CaptainLaunchIsolationPlan? StartedWith { get; private set; }

            public override Task<int> StartAsync(string workingDirectory, string prompt, Dictionary<string, string>? environment = null,
                string? logFilePath = null, string? finalMessageFilePath = null, string? model = null, Captain? captain = null,
                bool showThinking = false, CancellationToken token = default, CaptainLaunchIsolationPlan? isolationPlan = null)
            {
                StartedWith = isolationPlan;
                throw new InvalidOperationException("launch reached the process start");
            }

            protected override string GetCommand() => "no-read-only-cli";

            protected override List<string> BuildArguments(string workingDirectory, string prompt, string? model, string? finalMessageFilePath, Captain? captain) =>
                new List<string>();
        }

        /// <summary>A Claude Code runtime that records how the chat service configured it, then fails the launch.</summary>
        private sealed class RecordingClaudeRuntime : ClaudeCodeRuntime
        {
            public RecordingClaudeRuntime(LoggingModule logging) : base(logging) { }

            public override Task<int> StartAsync(string workingDirectory, string prompt, Dictionary<string, string>? environment = null,
                string? logFilePath = null, string? finalMessageFilePath = null, string? model = null, Captain? captain = null,
                bool showThinking = false, CancellationToken token = default, CaptainLaunchIsolationPlan? isolationPlan = null)
            {
                throw new InvalidOperationException("launch reached the process start");
            }
        }

        private sealed class RecordingClaudeRuntimeFactory : AgentRuntimeFactory
        {
            private readonly LoggingModule _Logging;

            public RecordingClaudeRuntimeFactory(LoggingModule logging) : base(logging) { _Logging = logging; }

            public RecordingClaudeRuntime? LastRuntime { get; private set; }

            public override IAgentRuntime Create(AgentRuntimeEnum runtimeType)
            {
                LastRuntime = new RecordingClaudeRuntime(_Logging);
                return LastRuntime;
            }
        }

        private sealed class NoReadOnlyCliRuntimeFactory : AgentRuntimeFactory
        {
            private readonly LoggingModule _Logging;

            public NoReadOnlyCliRuntimeFactory(LoggingModule logging) : base(logging) { _Logging = logging; }

            public NoReadOnlyCliRuntime? LastRuntime { get; private set; }

            public override IAgentRuntime Create(AgentRuntimeEnum runtimeType)
            {
                LastRuntime = new NoReadOnlyCliRuntime(_Logging);
                return LastRuntime;
            }
        }

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

            await RunTest("An Ask turn launches every CLI runtime read-only by default, and the operator setting widens it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    SessionTokenService sessionTokens = new SessionTokenService();
                    ArmadaSettings settings = new ArmadaSettings { McpPort = 51790 };
                    AuthContext caller = AuthContext.Authenticated("tenant-native", "user-native", true, true, "Session");

                    foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex, AgentRuntimeEnum.Gemini, AgentRuntimeEnum.Cursor, AgentRuntimeEnum.OpenCode, AgentRuntimeEnum.Mux })
                    {
                        Captain captain = new Captain("chat-native-" + runtime, runtime);
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                        ReplayRuntimeFactory factory = new ReplayRuntimeFactory(logging, new[] { "reply" });
                        CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, settings, sessionTokens);
                        AskTurn savedTurn = new AskTurn { ThreadId = "thread-native", RequestId = "turn-native-" + runtime, CaptainId = captain.Id };

                        settings.Ask.NativeToolAccess = NativeToolAccessEnum.ReadOnly;
                        await chat.ChatAsync(caller, captain.Id, new CaptainChatRequest { Message = "question" }).ConfigureAwait(false);
                        AssertEqual(NativeToolAccessEnum.ReadOnly, factory.LastRuntime!.ReceivedIsolationPlan!.NativeToolAccess, runtime + " chat turn launches read-only");
                        await chat.ChatSavedAsync(caller, savedTurn, new CaptainChatRequest { Message = "saved question" }).ConfigureAwait(false);
                        AssertEqual(NativeToolAccessEnum.ReadOnly, factory.LastRuntime!.ReceivedIsolationPlan!.NativeToolAccess, runtime + " saved turn launches read-only");
                        AssertEqual(NativeToolAccessEnum.ReadOnly, settings.Ask.NativeToolAccess, runtime + ": the launch leaves the shared setting alone");

                        settings.Ask.NativeToolAccess = NativeToolAccessEnum.Unrestricted;
                        await chat.ChatSavedAsync(caller, savedTurn, new CaptainChatRequest { Message = "saved question" }).ConfigureAwait(false);
                        AssertEqual(NativeToolAccessEnum.Unrestricted, factory.LastRuntime!.ReceivedIsolationPlan!.NativeToolAccess, runtime + " launches with mission defaults once the operator widens Ask");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Only an attended saved Claude Code turn sends tool permission prompts to Armada, and only when the setting is on", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    SessionTokenService sessionTokens = new SessionTokenService();
                    ArmadaSettings settings = new ArmadaSettings { McpPort = 51790 };
                    AuthContext caller = AuthContext.Authenticated("tenant-prompt", "user-prompt", true, true, "Session");
                    Captain captain = new Captain("chat-prompt", AgentRuntimeEnum.ClaudeCode);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    RecordingClaudeRuntimeFactory factory = new RecordingClaudeRuntimeFactory(logging);
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, settings, sessionTokens);
                    AskTurn savedTurn = new AskTurn { ThreadId = "thread-prompt", RequestId = "turn-prompt", CaptainId = captain.Id };

                    await chat.ChatSavedAsync(caller, savedTurn, new CaptainChatRequest { Message = "saved question" }).ConfigureAwait(false);
                    AssertNull(factory.LastRuntime!.PermissionPromptTool, "The setting is off by default");

                    settings.Ask.ApproveNativeToolsInArmada = true;
                    settings.Ask.ToolApprovalTimeoutSeconds = 45;
                    await chat.ChatAsync(caller, captain.Id, new CaptainChatRequest { Message = "temporary question" }).ConfigureAwait(false);
                    AssertNull(factory.LastRuntime!.PermissionPromptTool, "Temporary chat is not an attended saved turn");

                    await chat.ChatSavedAsync(caller, savedTurn, new CaptainChatRequest { Message = "saved question" }).ConfigureAwait(false);
                    AssertEqual("mcp__armada__armada_cli_permission_prompt", factory.LastRuntime!.PermissionPromptTool, "A saved Claude Code turn names the Armada prompt tool");
                    AssertEqual(45, factory.LastRuntime!.PermissionPromptTimeoutSeconds, "The prompt wait follows the setting");
                }
            }).ConfigureAwait(false);

            await RunTest("An Ask turn is refused for a CLI runtime that cannot express read-only native tools", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    Captain captain = new Captain("chat-no-read-only", AgentRuntimeEnum.Cursor);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    NoReadOnlyCliRuntimeFactory factory = new NoReadOnlyCliRuntimeFactory(logging);
                    ArmadaSettings settings = new ArmadaSettings();
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, factory, null, null, logging, settings);

                    CaptainChatResponse refused = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "question" }).ConfigureAwait(false);
                    AssertFalse(refused.Success, "The Ask turn must be refused");
                    AssertContains("NoReadOnlyCli cannot restrict its native tools to read-only", refused.Error ?? String.Empty, "The refusal names the runtime and the reason");
                    AssertNull(factory.LastRuntime!.StartedWith, "A refused Ask turn must never start the runtime");

                    settings.Ask.NativeToolAccess = NativeToolAccessEnum.Unrestricted;
                    CaptainChatResponse widened = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "question" }).ConfigureAwait(false);
                    AssertContains("launch reached the process start", widened.Error ?? String.Empty, "Widened Ask launches the runtime with mission defaults");
                    AssertEqual(NativeToolAccessEnum.Unrestricted, factory.LastRuntime!.StartedWith!.NativeToolAccess, "The widened launch carries no restriction");
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
                            AssertFalse(credentials.SessionToken!.StartsWith("armada-launch-", StringComparison.Ordinal), "The session token never uses the retired global-token format");
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

            await RunTest("DeferredMcp_AdminDiscoversAndCallsOnlyCallerSelectedTools", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(testDb, logging).ConfigureAwait(false);
                    string adminUserId = await fixture.CreateGlobalAdminAsync().ConfigureAwait(false);
                    bool hiddenToolRan = false;
                    bool targetToolRan = false;
                    const string targetName = "admin_catalog_target";
                    const string targetDescription = "deferred target description sentinel";

                    await using (ArmadaMcpHttpServer server = fixture.CreateServer())
                    {
                        RegisterAdminCatalog(server, targetName, targetDescription, () =>
                        {
                            targetToolRan = true;
                            return Task.FromResult((object)new { result = "target-handler-result" });
                        }, () =>
                        {
                            hiddenToolRan = true;
                            return Task.FromResult((object)new { result = "hidden-handler-result" });
                        });
                        await server.StartAsync().ConfigureAwait(false);

                        ScriptedToolChatClient client = new ScriptedToolChatClient(new ToolChatResponse[]
                        {
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_hidden", Name = "admin_catalog_02", ArgumentsJson = "{}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_discover_target", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"query\":\"admin_catalog_target\",\"offset\":0}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_discover_repeat", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"query\":\"admin_catalog_target\",\"offset\":0}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_target", Name = targetName, ArgumentsJson = "{\"targetArgumentSentinel\":\"ok\"}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_wildcard", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"query\":\"*\",\"offset\":0}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_next_page", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"query\":\"*\",\"offset\":4}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_bad_offset", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"query\":\"*\",\"offset\":-1}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_long_query", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"query\":\"" + new String('x', 257) + "\",\"offset\":0}" } } },
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_long_description", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"query\":\"admin_catalog_39\",\"offset\":0}" } } },
                            new ToolChatResponse { Success = true, Text = "Deferred catalog complete", ToolCalls = new List<ToolCall>() }
                        }, logging);

                        CaptainChatService chat = new CaptainChatService(testDb.Driver, new ScriptedApiRuntimeFactory(logging, client), null, null, logging, fixture.Settings, fixture.SessionTokens);
                        AuthContext adminCaller = AuthContext.Authenticated(fixture.TenantAId, adminUserId, true, false, "Session");
                        CaptainChatResponse adminResponse = await chat.ChatAsync(adminCaller, fixture.CaptainId, new CaptainChatRequest { Message = "Find the target and use it" }).ConfigureAwait(false);

                        AssertTrue(adminResponse.Success, "The admin chat completes: " + (adminResponse.Error ?? String.Empty));
                        AssertTrue(client.Requests.Count >= 10, "Each discovery and call result returns to the model");
                        List<ToolDefinition> initialTools = client.Requests[0].Tools ?? new List<ToolDefinition>();
                        AssertTrue(initialTools.Any(tool => tool.Name == DeferredDiscoveryToolName), "A large selected catalog offers its local discovery tool");
                        AssertFalse(initialTools.Any(tool => tool.Name == targetName), "The target schema is deferred before discovery");
                        AssertFalse(initialTools.Any(tool => tool.Name == "admin_catalog_02"), "Other selected schemas are also deferred");
                        AssertTrue(hiddenToolRan, "An eligible selected tool remains callable before its schema is discovered");

                        List<ToolDefinition> afterTargetDiscovery = client.Requests[2].Tools ?? new List<ToolDefinition>();
                        AssertEqual(1, afterTargetDiscovery.Count(tool => tool.Name == targetName), "The first discovery adds the target schema to the next request");
                        List<ToolDefinition> afterRepeatedDiscovery = client.Requests[3].Tools ?? new List<ToolDefinition>();
                        List<ToolDefinition> targetDefinitions = afterRepeatedDiscovery.Where(tool => tool.Name == targetName).ToList();
                        AssertEqual(1, targetDefinitions.Count, "Repeating discovery does not add the target definition twice");
                        AssertEqual(targetDescription, targetDefinitions[0].Description, "Discovery adds the full target description for the next model request");
                        AssertContains("targetArgumentSentinel", JsonSerializer.Serialize(targetDefinitions[0].Parameters), "Discovery adds the full target input schema");
                        AssertContains("target-handler-result", FindToolResult(client.Requests[4], targetName), "The authenticated MCP call result returns to a later model request");
                        AssertTrue(targetToolRan, "The discovered target handler runs through the real MCP endpoint");

                        DeferredDiscoveryResult firstPage = JsonSerializer.Deserialize<DeferredDiscoveryResult>(FindToolResult(client.Requests[5], DeferredDiscoveryToolName), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        AssertNotNull(firstPage, "Wildcard discovery returns a structured page");
                        AssertEqual(4, firstPage.Tools.Count, "Wildcard discovery returns four entries per page");
                        AssertEqual(4, firstPage.NextOffset ?? -1, "The first wildcard page points to offset four");
                        List<ToolDefinition> beforeNextPage = client.Requests[5].Tools ?? new List<ToolDefinition>();
                        List<ToolDefinition> afterNextPage = client.Requests[6].Tools ?? new List<ToolDefinition>();
                        HashSet<string> beforeNames = new HashSet<string>(beforeNextPage.Select(tool => tool.Name), StringComparer.Ordinal);
                        List<ToolDefinition> newlyAdded = afterNextPage.Where(tool => !beforeNames.Contains(tool.Name)).ToList();
                        AssertEqual(4, newlyAdded.Count, "The wildcard page adds only its four full schemas");
                        HashSet<string> firstPageNames = new HashSet<string>(firstPage.Tools.Select(tool => tool.Name), StringComparer.Ordinal);
                        DeferredDiscoveryResult secondPage = JsonSerializer.Deserialize<DeferredDiscoveryResult>(FindToolResult(client.Requests[6], DeferredDiscoveryToolName), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        AssertNotNull(secondPage, "The next wildcard call returns a structured page");
                        AssertEqual(4, secondPage.Tools.Count, "The second wildcard page also returns four entries");
                        AssertFalse(secondPage.Tools.Any(tool => firstPageNames.Contains(tool.Name)), "The next page contains distinct tools");
                        AssertEqual(8, secondPage.NextOffset ?? -1, "The second wildcard page advances to offset eight");
                        AssertEqual(4, afterNextPage.Count(tool => secondPage.Tools.Any(entry => entry.Name == tool.Name)), "The next page adds exactly its four full schemas");
                        AssertContains("invalid_arguments", FindToolResult(client.Requests[7], DeferredDiscoveryToolName), "A negative page offset is rejected as invalid arguments");
                        AssertContains("invalid_arguments", FindToolResult(client.Requests[8], DeferredDiscoveryToolName), "A query longer than 256 characters is rejected as invalid arguments");
                        DeferredDiscoveryResult longDescriptionResult = JsonSerializer.Deserialize<DeferredDiscoveryResult>(FindToolResult(client.Requests[9], DeferredDiscoveryToolName), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        AssertNotNull(longDescriptionResult, "A matching tool with a long description is returned");
                        AssertEqual(1, longDescriptionResult.Tools.Count, "Exact discovery returns only the matching tool");
                        AssertEqual(400, longDescriptionResult.Tools[0].Description.Length, "Discovery bounds returned descriptions to 400 characters");
                        AssertTrue((client.Requests[9].Tools ?? new List<ToolDefinition>()).Any(tool => tool.Name == "admin_catalog_39" && tool.Description.Length > 400), "The next request receives the full tool definition");

                        ScriptedToolChatClient regularClient = new ScriptedToolChatClient(new ToolChatResponse[]
                        {
                            new ToolChatResponse { Success = true, Text = "Regular caller complete", ToolCalls = new List<ToolCall>() }
                        }, logging);
                        CaptainChatService regularChat = new CaptainChatService(testDb.Driver, new ScriptedApiRuntimeFactory(logging, regularClient), null, null, logging, fixture.Settings, fixture.SessionTokens);
                        AuthContext regularCaller = AuthContext.Authenticated(fixture.TenantAId, fixture.UserAId, false, false, "Session");
                        CaptainChatResponse regularResponse = await regularChat.ChatAsync(regularCaller, fixture.CaptainId, new CaptainChatRequest { Message = "Continue" }).ConfigureAwait(false);
                        AssertTrue(regularResponse.Success, "A regular caller's separate chat turn completes");
                        List<ToolDefinition> regularTools = regularClient.Requests[0].Tools ?? new List<ToolDefinition>();
                        AssertFalse(regularTools.Any(tool => tool.Name == DeferredDiscoveryToolName), "A small caller-filtered catalog stays eager");
                        AssertFalse(regularTools.Any(tool => tool.Name.StartsWith("admin_catalog_", StringComparison.Ordinal)), "The admin catalog does not leak into another caller's chat");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("DeferredMcp_RemoteDiscoveryNameCollisionUsesEagerFallback", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ChatMcpFixture fixture = await ChatMcpFixture.CreateAsync(testDb, logging).ConfigureAwait(false);
                    string adminUserId = await fixture.CreateGlobalAdminAsync().ConfigureAwait(false);
                    bool collisionToolRan = false;
                    const string targetName = "collision_catalog_target";
                    const string collisionDescription = "remote collision description sentinel";

                    await using (ArmadaMcpHttpServer server = fixture.CreateServer())
                    {
                        RegisterAdminCatalog(server, targetName, "collision target schema sentinel", () =>
                            Task.FromResult((object)new { result = "target" }), () => Task.FromResult((object)new { result = "other" }));
                        server.RegisterTool(DeferredDiscoveryToolName, collisionDescription, new { type = "object", properties = new { collisionArgument = new { type = "string" } } }, args =>
                        {
                            collisionToolRan = true;
                            return Task.FromResult((object)new { result = "remote-collision-result" });
                        });
                        await server.StartAsync().ConfigureAwait(false);

                        ScriptedToolChatClient client = new ScriptedToolChatClient(new ToolChatResponse[]
                        {
                            new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "call_collision", Name = DeferredDiscoveryToolName, ArgumentsJson = "{\"collisionArgument\":\"ok\"}" } } },
                            new ToolChatResponse { Success = true, Text = "Collision fallback complete", ToolCalls = new List<ToolCall>() }
                        }, logging);
                        CaptainChatService chat = new CaptainChatService(testDb.Driver, new ScriptedApiRuntimeFactory(logging, client), null, null, logging, fixture.Settings, fixture.SessionTokens);
                        AuthContext adminCaller = AuthContext.Authenticated(fixture.TenantAId, adminUserId, true, false, "Session");
                        CaptainChatResponse response = await chat.ChatAsync(adminCaller, fixture.CaptainId, new CaptainChatRequest { Message = "Use the remote tool" }).ConfigureAwait(false);

                        AssertTrue(response.Success, "The collision chat completes: " + (response.Error ?? String.Empty));
                        List<ToolDefinition> offered = client.Requests[0].Tools ?? new List<ToolDefinition>();
                        List<ToolDefinition> collisionDefinitions = offered.Where(tool => tool.Name == DeferredDiscoveryToolName).ToList();
                        AssertEqual(1, collisionDefinitions.Count, "The reserved name is offered once when the remote catalog uses it");
                        AssertEqual(collisionDescription, collisionDefinitions[0].Description, "The selected remote definition wins the reserved-name collision");
                        AssertTrue(offered.Any(tool => tool.Name == targetName), "The collision falls back to eager catalog schemas");
                        AssertTrue(collisionToolRan, "The remote tool with the reserved name remains routed to MCP");
                        AssertContains("remote-collision-result", FindToolResult(client.Requests[1], DeferredDiscoveryToolName), "The collision handler result reaches the next model request");
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
                        AuthContext caller = AuthContext.Authenticated("tenant-cli", "user-cli", true, true, "Session");
                        AuthContext scopedCaller = AuthContext.Authenticated("tenant-cli", "user-cli", true, true, "Session");
                        scopedCaller.MissionId = "msn_chat_scope";
                        AssertNull(CallerMcpToolAccessFactory.Create(scopedCaller, sessionTokens, settings.McpPort),
                            "mission callers cannot obtain an unrestricted child credential");
                        CaptainChatResponse refused = await chat.ChatAsync(scopedCaller, captain.Id,
                            new CaptainChatRequest { Message = "hello" }).ConfigureAwait(false);
                        AssertFalse(refused.Success, "mission callers cannot start conversational chat");
                        AssertNull(factory.LastRuntime, "scope refusal happens before creating a runtime");

                        AuthContext nestedAskCaller = AuthContext.Authenticated("tenant-cli", "user-cli", true, true, "Session");
                        nestedAskCaller.AskThreadId = "thread-cli";
                        nestedAskCaller.AskTurnId = "turn-cli";
                        AssertNull(CallerMcpToolAccessFactory.Create(nestedAskCaller, sessionTokens, settings.McpPort),
                            "saved-turn callers cannot exchange their scope for an ordinary MCP token");
                        CaptainChatResponse nestedAskRefused = await chat.ChatAsync(nestedAskCaller, captain.Id,
                            new CaptainChatRequest { Message = "nested" }).ConfigureAwait(false);
                        AssertFalse(nestedAskRefused.Success, "a saved-turn credential cannot start nested chat");
                        AssertNull(factory.LastRuntime, "nested saved-turn chat is refused before runtime creation");

                        // The reply content is irrelevant here; the launched runtime records the isolation plan the
                        // chat turn built when StartAsync is called, so assert on that plan regardless of the reply.
                        await chat.ChatAsync(caller, captain.Id, new CaptainChatRequest { Message = "hello" }).ConfigureAwait(false);

                        CaptainLaunchIsolationPlan plan = factory.LastRuntime!.ReceivedIsolationPlan!;
                        AssertNotNull(plan);

                        AssertTrue(plan.EnvironmentOverrides.TryGetValue(McpCredentialReference.ChatEnvironmentVariable, out string? carried), runtime + " carries the caller token in the chat variable");
                        AssertFalse(String.IsNullOrEmpty(carried), runtime + " carries a non-empty caller token");
                        AssertFalse(plan.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable), runtime + " never sets the launch credential variable");
                        AssertFalse(plan.EnvironmentOverrides.Values.Any(value => value.StartsWith("armada-launch-", StringComparison.Ordinal)), runtime + " never carries the retired global-token format");

                        // The carried token authenticates as the caller, so the endpoint scopes the turn to it.
                        AuthContext? validated = sessionTokens.ValidateToken(carried!);
                        AssertNotNull(validated);
                        AssertEqual("tenant-cli", validated!.TenantId, runtime + " token resolves to the caller's tenant");
                        AssertEqual("user-cli", validated.UserId, runtime + " token resolves to the caller's user");

                        AskTurn savedTurn = new AskTurn { ThreadId = "thread-cli", RequestId = "reserved-turn-cli", CaptainId = captain.Id };
                        await chat.ChatSavedAsync(caller, savedTurn,
                            new CaptainChatRequest { Message = "saved message", TurnId = "untrusted-request-field" }).ConfigureAwait(false);
                        CaptainLaunchIsolationPlan savedPlan = factory.LastRuntime!.ReceivedIsolationPlan!;
                        AssertTrue(savedPlan.EnvironmentOverrides.TryGetValue(McpCredentialReference.ChatEnvironmentVariable, out string? savedCarried), runtime + " saved turn carries a scoped credential");
                        AuthContext? savedScope = sessionTokens.ValidateToken(savedCarried!);
                        AssertEqual("thread-cli", savedScope!.AskThreadId, runtime + " saved credential names its server-bound thread");
                        AssertEqual("reserved-turn-cli", savedScope.AskTurnId, runtime + " saved credential uses the reserved turn, not the request field");
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
                        AuthContext? validatedCredential = fixture.SessionTokens.ValidateToken(bearerCredential);
                        AssertNotNull(validatedCredential, "The plan credential validates as a session token");
                        AssertEqual(fixture.TenantAId, validatedCredential!.TenantId, "The plan credential belongs to the caller's tenant");
                        AssertEqual(fixture.UserAId, validatedCredential.UserId, "The plan credential belongs to the caller");

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

                    List<string> records = new List<string> { "[ARMADA:ACTIVITY] codex item reasoning", "The answer" };
                    CaptainChatService chat = new CaptainChatService(testDb.Driver, new ReplayRuntimeFactory(logging, records), null, null, logging);
                    CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);

                    AssertEqual("The answer", response.Reply, "A non-tool activity record is not part of the reply");
                }
            }).ConfigureAwait(false);

            await RunTest("A provider failure from any runtime fails the chat turn", async () =>
            {
                // Each record is what a runtime writes for its provider's failure event: Codex
                // {"type":"error","message":"You've hit your usage limit. Try again at 3:05 PM."},
                // Gemini/Cursor/Mux {"type":"error","message":"quota exceeded"}, and the API endpoint's
                // failed inference call.
                string[] failures =
                {
                    "[ARMADA:ACTIVITY] codex error You've hit your usage limit. Try again at 3:05 PM.",
                    "[ARMADA:ACTIVITY] gemini error quota exceeded",
                    "[ARMADA:ACTIVITY] api error inference call failed: overloaded"
                };
                foreach (string failure in failures)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        Captain captain = new Captain("chat-failure", AgentRuntimeEnum.Codex);
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        List<string> records = new List<string> { failure };
                        CaptainChatService chat = new CaptainChatService(testDb.Driver, new ReplayRuntimeFactory(logging, records), null, null, logging);
                        CaptainChatResponse response = await chat.ChatAsync(captain.Id, new CaptainChatRequest { Message = "Question" }).ConfigureAwait(false);

                        AssertFalse(response.Success, "A provider failure fails the turn: " + failure);
                        AssertContains(failure.Substring("[ARMADA:ACTIVITY] ".Length), response.Error ?? String.Empty, "The failure text is the turn's error");
                        AssertFalse((response.Reply ?? String.Empty).Contains("error", StringComparison.Ordinal), "The failure is not written into the reply");
                    }
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

        private static void RegisterAdminCatalog(
            ArmadaMcpHttpServer server,
            string targetName,
            string targetDescription,
            Func<Task<object>> targetHandler,
            Func<Task<object>> otherHandler)
        {
            for (int index = 0; index < 40; index++)
            {
                string name = index == 0 ? targetName : "admin_catalog_" + index.ToString("D2");
                string description = index == 0 ? targetDescription : index == 39 ? new String('d', 500) : "Admin catalog tool " + index.ToString("D2");
                object schema = index == 0
                    ? new { type = "object", properties = new Dictionary<string, object> { ["targetArgumentSentinel"] = new { type = "string" } } }
                    : new { type = "object", properties = new Dictionary<string, object> { ["catalogValue"] = new { type = "string" } } };
                Func<Task<object>> handler = index == 0 ? targetHandler : otherHandler;
                server.RegisterTool(name, description, schema, args => handler());
            }
        }

        private static string FindToolResult(ToolChatRequest request, string toolName)
        {
            if (request.Messages != null)
            {
                for (int index = request.Messages.Count - 1; index >= 0; index--)
                {
                    ChatMessage message = request.Messages[index];
                    if (String.Equals(message.ToolName, toolName, StringComparison.Ordinal)) return message.Content ?? String.Empty;
                }
            }
            return String.Empty;
        }

        private sealed class DeferredDiscoveryResult
        {
            /// <summary>Create a discovery result for typed JSON deserialization.</summary>
            public DeferredDiscoveryResult() { }

            /// <summary>Number of selected tools that match the query.</summary>
            public int Total { get; set; }

            /// <summary>Current page offset.</summary>
            public int Offset { get; set; }

            /// <summary>Next page offset when more matches remain.</summary>
            public int? NextOffset { get; set; }

            /// <summary>Tool entries returned on this page.</summary>
            public List<DeferredDiscoveredTool> Tools { get; set; } = new List<DeferredDiscoveredTool>();
        }

        private sealed class DeferredDiscoveredTool
        {
            /// <summary>Create a discovery entry for typed JSON deserialization.</summary>
            public DeferredDiscoveredTool() { }

            /// <summary>Selected remote tool name.</summary>
            public string Name { get; set; } = String.Empty;

            /// <summary>Bounded catalog description.</summary>
            public string Description { get; set; } = String.Empty;
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
