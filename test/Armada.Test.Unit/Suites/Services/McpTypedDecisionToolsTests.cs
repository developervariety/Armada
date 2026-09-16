namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the captain-facing typed-decision tools. The tool ships disabled and returns
    /// unavailable until an operator enables it; when enabled it redacts state before egress, bounds
    /// calls per mission, writes exactly one <c>typed_decision.captain</c> event per call, and has no
    /// side effect on any Armada record. The two helpers are dormant until their own decision is
    /// enabled. The smoke test proves the tool is in the mission-scoped catalogue, lists there, and
    /// returns typed answers or unavailable.
    /// </summary>
    public class McpTypedDecisionToolsTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Suite name.</summary>
        public override string Name => "MCP Typed Decision Tools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("The four captain tools are registered and mission-scoped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    Harness harness = Harness.Create(testDb, client, enabled: false);

                    foreach (string name in new[] { "armada_typed_decision", "armada_check_premise", "armada_memory_triage", "armada_check_prior_art" })
                        AssertTrue(harness.Handlers.ContainsKey(name), "Tool should be registered: " + name);

                    // A non-admin mission caller may list and call each tool, like the memory tools.
                    AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                    foreach (string name in new[] { "armada_typed_decision", "armada_check_premise", "armada_memory_triage", "armada_check_prior_art" })
                        AssertTrue(McpToolAccessPolicy.IsAllowed(captain, name), "Mission caller may use: " + name);
                }
            });

            await RunTest("Disabled by default: the tool returns unavailable and records one event", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    Harness harness = Harness.Create(testDb, client, enabled: false);

                    string response = await harness.CallAsync("armada_typed_decision", SampleGeneralArgs()).ConfigureAwait(false);
                    AssertContains("\"available\":false", Compact(response));
                    AssertContains("disabled", response);

                    // Disabled means no egress: the client was never asked.
                    AssertEqual(0, client.CallCount, "A disabled tool performs no egress");

                    // But one event is still recorded, so a captain trying the tool is observable.
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "Exactly one captain event per call");
                    AssertContains("disabled", events[0].Payload ?? "");
                }
            });

            await RunTest("Enabled: redaction happens before egress and answers return", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("provider", 0.93);
                    Harness harness = Harness.Create(testDb, client, enabled: true);

                    // The state carries a mission id and an absolute path (must be redacted) plus a
                    // heavy-duty product identifier (must survive).
                    object args = new
                    {
                        state = "mission msn_secret001 on host at /srv/example/docks failed decoding PGN65259 SecurityAccess seed-key",
                        questions = new
                        {
                            cause = new
                            {
                                type = "choice",
                                instructions = "Pick the cause",
                                criteria = new { provider = "provider fault", environmental = "host or infra" }
                            }
                        }
                    };

                    string response = await harness.CallAsync("armada_typed_decision", args).ConfigureAwait(false);
                    AssertContains("\"available\":true", Compact(response));
                    AssertContains("provider", response);

                    AssertEqual(1, client.CallCount, "Egress happened exactly once");
                    string egressed = client.LastState ?? "";
                    AssertFalse(egressed.Contains("msn_secret001", StringComparison.Ordinal), "The mission id must be redacted before egress");
                    AssertFalse(egressed.Contains("/srv/example", StringComparison.Ordinal), "The absolute path must be redacted before egress");
                    AssertContains("PGN65259", egressed);
                    AssertContains("SecurityAccess", egressed);

                    // One event, and it must NOT carry the raw state.
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "Exactly one captain event per call");
                    AssertFalse((events[0].Payload ?? "").Contains("msn_secret001", StringComparison.Ordinal), "The event must not carry the state");
                    AssertContains("state_sha256", events[0].Payload ?? "");
                }
            });

            await RunTest("The per-mission call budget caps egress", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("provider", 0.93);
                    Harness harness = Harness.Create(testDb, client, enabled: true, maxCallsPerMission: 2);

                    string missionId = await harness.SeedMissionAsync(testDb).ConfigureAwait(false);
                    object BudgetArgs() => new
                    {
                        state = "a state to reason over",
                        questions = new { cause = new { type = "choice", instructions = "why", criteria = new { provider = "provider" } } },
                        missionId = missionId
                    };

                    string first = await harness.CallAsync("armada_typed_decision", BudgetArgs()).ConfigureAwait(false);
                    string second = await harness.CallAsync("armada_typed_decision", BudgetArgs()).ConfigureAwait(false);
                    string third = await harness.CallAsync("armada_typed_decision", BudgetArgs()).ConfigureAwait(false);

                    AssertContains("\"available\":true", Compact(first));
                    AssertContains("\"available\":true", Compact(second));
                    AssertContains("budget_exhausted", third);
                    AssertEqual(2, client.CallCount, "The third call is refused before egress");
                }
            });

            await RunTest("A helper is dormant until its own decision is enabled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = NoulResult("contradicts_scope", 0.2);

                    // Tool enabled, but premise_check decision Off (the default): dormant.
                    Harness dormant = Harness.Create(testDb, client, enabled: true);
                    string dormantResponse = await dormant.CallAsync("armada_check_premise", new { restatement = "I will port the decoder." }).ConfigureAwait(false);
                    AssertContains("\"available\":false", Compact(dormantResponse));
                    AssertContains("disabled", dormantResponse);
                    AssertEqual(0, client.CallCount, "A dormant helper performs no egress");

                    // Enable premise_check: the helper now reaches the client.
                    Harness live = Harness.Create(testDb, client, enabled: true, enablePremiseCheck: true);
                    string liveResponse = await live.CallAsync("armada_check_premise", new { restatement = "I will port the decoder." }).ConfigureAwait(false);
                    AssertContains("\"available\":true", Compact(liveResponse));
                    AssertEqual(1, client.CallCount, "An enabled helper reaches the client once");
                }
            });

            await RunTest("The memory-triage helper shapes its own questions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = NoulResult("will_go_stale", 0.9);
                    Harness harness = Harness.Create(testDb, client, enabled: true, enableMemoryRecord: true);

                    string response = await harness.CallAsync("armada_memory_triage", new { candidate = "The build takes 95 seconds as of today." }).ConfigureAwait(false);
                    AssertContains("\"available\":true", Compact(response));

                    // The helper builds the four D23a questions itself; the client sees them.
                    AssertTrue(client.LastQuestionIds.Contains("type_ok"), "type_ok question shaped");
                    AssertTrue(client.LastQuestionIds.Contains("duplicate_of"), "duplicate_of question shaped");
                    AssertTrue(client.LastQuestionIds.Contains("will_go_stale"), "will_go_stale question shaped");
                    AssertTrue(client.LastQuestionIds.Contains("belongs_in_ai_memory"), "belongs_in_ai_memory question shaped");
                }
            });

            await RunTest("The tool has no side effect on any Armada record", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("provider", 0.99);
                    Harness harness = Harness.Create(testDb, client, enabled: true);
                    string missionId = await harness.SeedMissionAsync(testDb).ConfigureAwait(false);

                    Mission before = (await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false))!;

                    await harness.CallAsync("armada_typed_decision", new
                    {
                        state = "state",
                        questions = new { cause = new { type = "choice", instructions = "why", criteria = new { provider = "p" } } },
                        missionId = missionId
                    }).ConfigureAwait(false);

                    // No memory was written, no objective created, and the mission is untouched (only
                    // the observability event exists).
                    List<Memory> memories = await testDb.Driver.Memories.EnumerateAsync().ConfigureAwait(false);
                    AssertEqual(0, memories.Count, "The tool writes no memory");

                    Mission after = (await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false))!;
                    AssertEqual(before.Status, after.Status, "The mission status is unchanged");
                    AssertEqual(before.CaptainId, after.CaptainId, "The mission is untouched");
                }
            });

            await RunTest("Smoke: registered in the full catalogue, lists, and answers or is unavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Disabled path through the whole registrar with the Null client.
                    ArmadaSettings offSettings = new ArmadaSettings();
                    Dictionary<string, Func<JsonElement?, Task<object>>> offHandlers = RegisterAll(testDb, offSettings, new NullTypedDecisionClient());
                    AssertTrue(offHandlers.ContainsKey("armada_typed_decision"), "The tool is in the full catalogue");
                    string offResponse = await CallRawAsync(offHandlers, "armada_typed_decision", SampleGeneralArgs()).ConfigureAwait(false);
                    AssertContains("\"available\":false", Compact(offResponse));

                    // Enabled path with a fake client returns typed answers.
                    McpTypedDecisionTools.ResetBudgetForTests();
                    ArmadaSettings onSettings = new ArmadaSettings();
                    onSettings.TypedDecisions.CaptainTool.Enabled = true;
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("environmental", 0.9);
                    Dictionary<string, Func<JsonElement?, Task<object>>> onHandlers = RegisterAll(testDb, onSettings, client);
                    string onResponse = await CallRawAsync(onHandlers, "armada_typed_decision", SampleGeneralArgs()).ConfigureAwait(false);
                    AssertContains("\"available\":true", Compact(onResponse));
                    AssertContains("environmental", onResponse);
                }
            });
        }

        private static object SampleGeneralArgs()
        {
            return new
            {
                state = "a plain state to reason over",
                questions = new
                {
                    cause = new
                    {
                        type = "choice",
                        instructions = "Pick the cause",
                        criteria = new { environmental = "host", provider = "provider" }
                    }
                }
            };
        }

        private static TypedDecisionResult ChoiceResult(string choice, double confidence)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>
                {
                    ["cause"] = new TypedAnswer { Type = "choice", Choice = choice, Confidence = confidence }
                },
                InputTokens = 40,
                OutputTokens = 8,
                LatencyMs = 12
            };
        }

        private static TypedDecisionResult NoulResult(string questionId, double noul)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>
                {
                    [questionId] = new TypedAnswer { Type = "noul", Noul = noul, Confidence = noul }
                }
            };
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterAll(TestDatabase testDb, ArmadaSettings settings, ITypedDecisionClient client)
        {
            McpTypedDecisionTools.ResetBudgetForTests();
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());
            McpToolRegistrar.RegisterAll(
                (name, _, _, handler) => { handlers[name] = handler; },
                testDb.Driver,
                new StubAdmiral(),
                settings: settings,
                logging: new LoggingModule(),
                typedDecisionClient: client,
                typedDecisionRecorder: recorder);
            return handlers;
        }

        private static async Task<string> CallRawAsync(Dictionary<string, Func<JsonElement?, Task<object>>> handlers, string tool, object args)
        {
            JsonElement element = JsonSerializer.SerializeToElement(args, _JsonOptions);
            AuthContext caller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
            using (McpCallerContext.Begin(caller))
            {
                object result = await handlers[tool](element).ConfigureAwait(false);
                return JsonSerializer.Serialize(result, _JsonOptions);
            }
        }

        private static string Compact(string json)
        {
            return json.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        }

        #region Private-Types

        private sealed class Harness
        {
            public Dictionary<string, Func<JsonElement?, Task<object>>> Handlers { get; } = new Dictionary<string, Func<JsonElement?, Task<object>>>();

            public static Harness Create(
                TestDatabase testDb,
                ITypedDecisionClient client,
                bool enabled,
                int maxCallsPerMission = 40,
                bool enablePremiseCheck = false,
                bool enableMemoryRecord = false)
            {
                McpTypedDecisionTools.ResetBudgetForTests();
                ArmadaSettings settings = new ArmadaSettings();
                settings.TypedDecisions.CaptainTool.Enabled = enabled;
                settings.TypedDecisions.CaptainTool.MaxCallsPerMission = maxCallsPerMission;
                if (enablePremiseCheck) settings.TypedDecisions.Decisions["premise_check"].Mode = TypedDecisionModeEnum.Gate;
                if (enableMemoryRecord) settings.TypedDecisions.Decisions["memory_record"].Mode = TypedDecisionModeEnum.Gate;

                Harness harness = new Harness();
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());
                McpTypedDecisionTools.Register(
                    (name, _, _, handler) => { harness.Handlers[name] = handler; },
                    testDb.Driver,
                    client,
                    recorder,
                    settings,
                    new LoggingModule());
                return harness;
            }

            public async Task<string> CallAsync(string tool, object args)
            {
                JsonElement element = JsonSerializer.SerializeToElement(args, _JsonOptions);
                AuthContext caller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                using (McpCallerContext.Begin(caller))
                {
                    object result = await Handlers[tool](element).ConfigureAwait(false);
                    return JsonSerializer.Serialize(result, _JsonOptions);
                }
            }

            public async Task<string> SeedMissionAsync(TestDatabase testDb)
            {
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                    new Vessel("typed-decision-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                Mission mission = new Mission();
                mission.TenantId = Constants.DefaultTenantId;
                mission.UserId = Constants.DefaultUserId;
                mission.VesselId = vessel.Id;
                mission.Title = "seed mission";
                mission.Status = MissionStatusEnum.InProgress;
                Mission created = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                return created.Id;
            }
        }

        private sealed class FakeTypedDecisionClient : ITypedDecisionClient
        {
            public int CallCount { get; private set; }

            public string? LastState { get; private set; }

            public List<string> LastQuestionIds { get; } = new List<string>();

            public TypedDecisionResult NextResult { get; set; } = new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>()
            };

            public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
            {
                CallCount++;
                LastState = request.State as string ?? request.State?.ToString();
                LastQuestionIds.Clear();
                foreach (KeyValuePair<string, TypedQuestion> entry in request.Questions)
                    LastQuestionIds.Add(entry.Key);
                return Task.FromResult(NextResult);
            }
        }

        private sealed class StubAdmiral : IAdmiralService
        {
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => Task.FromResult(new ArmadaStatus());

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task RecallAllAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task StopAllAgentProcessesAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }

        #endregion
    }
}
