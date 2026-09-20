namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
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
    /// Tests for the captain-facing typed-decision tools. The tools are enabled by default and return
    /// unavailable once an operator disables them; when enabled they redact state before egress, write
    /// exactly one event per call, and have no side effect on any Armada record. Each pre-shaped helper
    /// is dormant until its own decision is enabled. The smoke test proves every tool is in the
    /// mission-scoped catalogue, lists there, and returns typed answers or unavailable.
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
            await RunTest("Caller without a mission retains participant attribution", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("provider", 0.93);
                    Harness harness = Harness.Create(testDb, client, enabled: true, participantKey: "operator-session-test");
                    await harness.CallAsync("armada_typed_decision", new
                    {
                        state = "test state",
                        questions = new { cause = new { type = "choice", instructions = "Cause", criteria = new { provider = "provider", environmental = "environment" } } }
                    }).ConfigureAwait(false);
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(1, events.Count);
                    AssertNull(events[0].CaptainId, "An operator session is not a captain id");
                    AssertContains("\"participant_key\":\"operator-session-test\"", events[0].Payload ?? "");
                    AssertContains("\"tool_name\":\"armada_typed_decision\"", events[0].Payload ?? "");
                }
            });

            await RunTest("The captain tools are registered and mission-scoped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    Harness harness = Harness.Create(testDb, client, enabled: false);

                    foreach (string name in new[] { "armada_typed_decision", "armada_score_items", "armada_check_premise", "armada_memory_triage", "armada_check_prior_art", "armada_change_quality", "armada_corpus_prelabel", "armada_run_custom_decision" })
                        AssertTrue(harness.Handlers.ContainsKey(name), "Tool should be registered: " + name);

                    // A non-admin mission caller may list and call each tool, like the memory tools.
                    AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                    foreach (string name in new[] { "armada_typed_decision", "armada_score_items", "armada_check_premise", "armada_memory_triage", "armada_check_prior_art", "armada_change_quality", "armada_corpus_prelabel", "armada_run_custom_decision" })
                        AssertTrue(McpToolAccessPolicy.IsAllowed(captain, name), "Mission caller may use: " + name);
                }
            });

            await RunTest("armada_change_quality asks the five dimension questions and returns the readings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["dry_duplicates"] = new TypedAnswer { Type = "noul", Noul = 0.95, Confidence = 0.95 },
                        ["readability_unclear_names"] = new TypedAnswer { Type = "noul", Noul = 0.91, Confidence = 0.91 }
                    };
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = new TypedDecisionResult { Available = true, Answers = answers };
                    Harness harness = Harness.Create(testDb, client, enabled: true);

                    string response = await harness.CallAsync("armada_change_quality", new { diff = "diff --git a/x b/x\n@@ -1 +1,2 @@\n a\n+b\n" }).ConfigureAwait(false);

                    AssertContains("\"available\":true", Compact(response));
                    foreach (string q in new[] { "dry_duplicates", "complexity_nested", "modularity_unrelated", "readability_unclear_names", "maintainability_coupling" })
                        AssertTrue(client.LastQuestionIds.Contains(q), "asked dimension question: " + q);
                }
            });

            await RunTest("armada_score_items asks one Noul per real item and returns the expected count", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["items[0]__matches"] = new TypedAnswer { Type = "noul", Noul = 0.96 },
                        ["items[1]__matches"] = new TypedAnswer { Type = "noul", Noul = 0.04 },
                        ["items[2]__matches"] = new TypedAnswer { Type = "noul", Noul = 0.87 },
                        ["best"] = new TypedAnswer
                        {
                            Type = "choice",
                            Choice = "truncates",
                            Confidence = 0.91,
                            Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                            {
                                ["truncates"] = 0.91,
                                ["filters"] = 0.04,
                                ["none"] = 0.03,
                                ["unclear"] = 0.02
                            }
                        }
                    };
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = new TypedDecisionResult { Available = true, Model = "jev-1.13.0", Answers = answers };
                    Harness harness = Harness.Create(testDb, client, enabled: true);

                    string response = await harness.CallAsync("armada_score_items", new
                    {
                        claim = "This snippet truncates a payload byte instead of filtering printable characters.",
                        pick = "Which listed snippet is the clearest truncation?",
                        items = new object[]
                        {
                            new { id = "truncates", text = "byte b = (byte)A_0.Message.Data[i];" },
                            new { id = "filters", text = "(b >= 32 && b <= 127)" },
                            "var remaining = (byte)message.Length;"
                        }
                    }).ConfigureAwait(false);

                    AssertContains("\"available\":true", Compact(response));
                    AssertContains("\"expectedCount\":1.87", Compact(response));
                    AssertTrue(client.LastQuestionIds.Contains("items[0]__matches"), "asked per-item noul 0");
                    AssertTrue(client.LastQuestionIds.Contains("items[1]__matches"), "asked per-item noul 1");
                    AssertTrue(client.LastQuestionIds.Contains("items[2]__matches"), "asked per-item noul 2");
                    AssertTrue(client.LastQuestionIds.Contains("best"), "asked the optional pick Choice");
                    AssertEqual(4, client.LastQuestionIds.Count, "no empty-slot questions");
                    AssertTrue(client.LastQuestions!["items[0]__matches"].Instructions.Contains("`items[0]`"), "names the JSON path");
                    AssertContains("\"choice\":\"truncates\"", Compact(response));
                }
            });

            await RunTest("armada_score_items with no items is invalid and does not call the provider", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    Harness harness = Harness.Create(testDb, client, enabled: true);
                    string response = await harness.CallAsync("armada_score_items", new { claim = "unused", items = Array.Empty<string>() }).ConfigureAwait(false);
                    AssertContains("\"available\":false", Compact(response));
                    AssertContains("invalid", Compact(response));
                    AssertEqual(0, client.CallCount, "empty list never reaches the provider");
                }
            });

            await RunTest("A captain tool about a mission on an excluded vessel sends nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    Harness seeder = Harness.Create(testDb, client, enabled: true);
                    string missionId = await seeder.SeedMissionAsync(testDb).ConfigureAwait(false);
                    Mission? seeded = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    Harness harness = Harness.Create(testDb, client, enabled: true,
                        configure: s => s.TypedDecisions.EgressExcludedVesselIds = new List<string> { seeded!.VesselId! });

                    string response = await harness.CallAsync("armada_typed_decision", new
                    {
                        missionId,
                        state = "a plain state to reason over",
                        questions = new { cause = new { type = "choice", instructions = "Pick the cause", criteria = new { environmental = "host", provider = "provider" } } }
                    }).ConfigureAwait(false);

                    AssertContains("\"available\":false", Compact(response));
                    AssertContains("egress_excluded_vessel", response);
                    AssertEqual(0, client.CallCount, "the captain's state never left the host");
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
                    // product identifier (must survive).
                    object args = new
                    {
                        state = "mission msn_secret001 on host at /srv/example/docks failed decoding Frame65259 AccessHandshake token",
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
                    AssertContains("Frame65259", egressed);
                    AssertContains("AccessHandshake", egressed);

                    // One event, and it must NOT carry the raw state.
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "Exactly one captain event per call");
                    AssertFalse((events[0].Payload ?? "").Contains("msn_secret001", StringComparison.Ordinal), "The event must not carry the state");
                    AssertContains("state_sha256", events[0].Payload ?? "");
                }
            });

            await RunTest("Repeated calls from one mission are never capped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("provider", 0.93);
                    Harness harness = Harness.Create(testDb, client, enabled: true);

                    string missionId = await harness.SeedMissionAsync(testDb).ConfigureAwait(false);
                    object RepeatArgs() => new
                    {
                        state = "a state to reason over",
                        questions = new { cause = new { type = "choice", instructions = "why", criteria = new { provider = "provider" } } },
                        missionId = missionId
                    };

                    const int calls = 60;
                    for (int i = 0; i < calls; i++)
                    {
                        string response = await harness.CallAsync("armada_typed_decision", RepeatArgs()).ConfigureAwait(false);
                        AssertContains("\"available\":true", Compact(response), "call " + (i + 1) + " should be answered");
                        AssertFalse(response.Contains("budget_exhausted", StringComparison.Ordinal), "no call may be refused for a budget");
                    }
                    AssertEqual(calls, client.CallCount, "Every call reaches the provider");
                }
            });

            await RunTest("A helper is dormant until its own decision is enabled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = NoulResult("contradicts_scope", 0.2);

                    // Tool enabled, but premise_check decision Off: dormant.
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

                    // The helper builds the four memory-triage questions itself; the client sees them.
                    AssertTrue(client.LastQuestionIds.Contains("type_fits"), "type_fits question shaped");
                    AssertTrue(client.LastQuestionIds.Contains("durable_record"), "durable_record question shaped");
                    AssertTrue(client.LastQuestionIds.Contains("duplicate_of"), "duplicate_of question shaped");
                    AssertTrue(client.LastQuestionIds.Contains("will_go_stale"), "will_go_stale question shaped");
                    AssertTrue(client.LastQuestionIds.Contains("belongs_in_ai_memory"), "belongs_in_ai_memory question shaped");
                }
            });

            await RunTest("The corpus-prelabel helper records its call under its own decision, never the general tool's", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = new TypedDecisionResult
                    {
                        Available = true,
                        Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                        {
                            ["provisional_kind"] = new TypedAnswer { Type = "choice", Choice = "failure_class", Confidence = 0.96 }
                        }
                    };
                    Harness harness = Harness.Create(testDb, client, enabled: true, enableCorpusPrelabel: true);

                    string response = await harness.CallAsync("armada_corpus_prelabel", new
                    {
                        record = new { input_type = "mission_failure", failure_reason = "the run failed at /srv/example/work/file.cs" }
                    }).ConfigureAwait(false);

                    AssertContains("\"available\":true", Compact(response));
                    AssertTrue(client.LastQuestionIds.Contains("provisional_kind"), "provisional_kind question shaped");
                    AssertEqual(1, client.LastQuestionIds.Count, "The helper asks exactly one question");

                    // The state is redacted before egress: the path never reaches the client.
                    AssertTrue(client.LastState != null && !client.LastState.Contains("/srv/example"), "State is redacted before egress");

                    // Exactly one event, carrying this decision's key and never the general tool's.
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "Exactly one event per call");
                    AssertContains("\"decision\":\"corpus_prelabel\"", Compact(events[0].Payload ?? ""));
                    AssertTrue(!Compact(events[0].Payload ?? "").Contains("\"decision\":\"captain_tool\""), "No event is recorded under the general tool's decision");

                    // The event carries the state's measurements, never the state.
                    AssertContains("state_sha256", events[0].Payload ?? "");
                    AssertContains("state_bytes", events[0].Payload ?? "");
                    AssertTrue(!(events[0].Payload ?? "").Contains("the run failed"), "The event never carries the state");
                }
            });

            await RunTest("The corpus-prelabel helper is dormant while its decision is Off", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    Harness dormant = Harness.Create(testDb, client, enabled: true, enableCorpusPrelabel: false);

                    string response = await dormant.CallAsync("armada_corpus_prelabel", new
                    {
                        record = new { input_type = "mail", payload = "keep the existing output format" }
                    }).ConfigureAwait(false);

                    AssertContains("\"available\":false", Compact(response));
                    AssertEqual(0, client.CallCount, "A dormant helper performs no egress");

                    // Dormant is still observable, and still under its own decision key.
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "A dormant call is still recorded once");
                    AssertContains("\"decision\":\"corpus_prelabel\"", Compact(events[0].Payload ?? ""));
                }
            });

            await RunTest("The corpus kinds the helper offers match the drafter script's kinds", async () =>
            {
                // One vocabulary, two copies in this repository: the choices this helper actually
                // sends, and the drafter script's own list. They are compared against each other, so
                // adding a kind to one and not the other fails here instead of drifting silently.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    Harness harness = Harness.Create(testDb, client, enabled: true, enableCorpusPrelabel: true);
                    await harness.CallAsync("armada_corpus_prelabel", new
                    {
                        record = new { input_type = "incident", summary = "a gate rejected a review" }
                    }).ConfigureAwait(false);

                    AssertTrue(client.LastQuestions != null, "The helper sent its question");
                    ChoiceQuestion? asked = client.LastQuestions!["provisional_kind"] as ChoiceQuestion;
                    AssertTrue(asked != null, "The provisional-kind question is a choice question");

                    string scriptPath = Path.Combine(FindRepositoryRoot(), "scripts", "autonomy", "draft-corpus-line.mjs");
                    AssertTrue(File.Exists(scriptPath), "The drafter script exists");
                    List<string> scriptKinds = ReadDrafterKinds(File.ReadAllText(scriptPath));
                    AssertTrue(scriptKinds.Count > 0, "The drafter script declares its kinds");

                    AssertEqual(
                        String.Join(",", scriptKinds),
                        String.Join(",", asked!.Criteria.Keys),
                        "The helper offers the drafter's corpus kinds, in the same order");
                }
            });

            await RunTest("A helper in Shadow consults and records but returns unavailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("provider", 0.93);
                    Harness harness = Harness.Create(testDb, client, enabled: true,
                        configure: s => s.TypedDecisions.Decisions["premise_check"].Mode = TypedDecisionModeEnum.Shadow);

                    string response = await harness.CallAsync("armada_check_premise", new { restatement = "I will add a retry to the uploader." }).ConfigureAwait(false);

                    AssertContains("\"available\":false", Compact(response), "Shadow quiets the helper");
                    AssertContains("shadow", response, "and says why");
                    AssertEqual(1, client.CallCount, "Shadow still consults the provider");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one event");
                    AssertContains("\"gate_outcome\":\"shadow\"", events[0].Payload ?? "", "recorded as a shadow call");
                }
            });

            await RunTest("The custom runner records one event on every call that never reaches the provider", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = ChoiceResult("provider", 0.93);
                    Harness enabled = Harness.Create(testDb, client, enabled: true,
                        configure: s => s.TypedDecisions.Custom["house_rule"] = CustomDecision(TypedDecisionModeEnum.Off));
                    Harness disabled = Harness.Create(testDb, client, enabled: false,
                        configure: s => s.TypedDecisions.Custom["house_rule"] = CustomDecision(TypedDecisionModeEnum.Gate));

                    await enabled.CallAsync("armada_run_custom_decision", new { name = "house_rule", context = new { diff = "a" } }).ConfigureAwait(false);
                    await enabled.CallAsync("armada_run_custom_decision", new { name = "no_such_rule", context = new { diff = "a" } }).ConfigureAwait(false);
                    await disabled.CallAsync("armada_run_custom_decision", new { name = "house_rule", context = new { diff = "a" } }).ConfigureAwait(false);

                    AssertEqual(0, client.CallCount, "none of the three reaches the provider");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCaptain).ConfigureAwait(false);
                    AssertEqual(3, events.Count, "one event per call");
                    string payloads = String.Join("\n", events.ConvertAll(e => e.Payload ?? ""));
                    AssertContains("\"gate_outcome\":\"dormant\"", payloads, "an Off decision is dormant");
                    AssertContains("\"gate_outcome\":\"not_found\"", payloads, "an unknown decision is not_found");
                    AssertContains("\"gate_outcome\":\"disabled\"", payloads, "a disabled tool is disabled");
                    AssertContains("custom:house_rule", payloads, "under the custom decision point");
                }
            });

            await RunTest("The custom runner in Shadow returns unavailable and records a shadow event", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient();
                    client.NextResult = new TypedDecisionResult
                    {
                        Available = true,
                        Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal) { ["finding"] = new TypedAnswer { Type = "noul", Noul = 0.97 } }
                    };
                    Harness harness = Harness.Create(testDb, client, enabled: true,
                        configure: s => s.TypedDecisions.Custom["house_rule"] = CustomDecision(TypedDecisionModeEnum.Shadow),
                        participantKey: "operator-session-test");

                    string response = await harness.CallAsync("armada_run_custom_decision", new { name = "house_rule", context = new { diff = "a" } }).ConfigureAwait(false);

                    AssertContains("\"available\":false", Compact(response), "Shadow quiets the custom runner");
                    AssertContains("shadow", response, "and says why");
                    AssertEqual(1, client.CallCount, "Shadow still consults the provider");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one shadow event");
                    AssertNull(events[0].CaptainId, "A session key must not become a captain id");
                    AssertContains("\"participant_key\":\"operator-session-test\"", events[0].Payload ?? "");
                    AssertContains("\"tool_name\":\"armada_run_custom_decision\"", events[0].Payload ?? "");
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

        private static object CompactionArgs(string? missionId)
        {
            object[] candidates =
            {
                new { tool = "bash", askedFor = "count the helper copies", outputHead = "11 matches across 7 files", outputBytes = 2048, turnsAgo = 30 },
                new { tool = "bash", askedFor = "list the test directory", outputHead = "test_one.py test_two.py", outputBytes = 4096, turnsAgo = 40 }
            };
            return missionId == null
                ? new { goal = "publish the count you measured", candidates }
                : new { missionId, goal = "publish the count you measured", candidates };
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

        /// Read the corpus kinds the drafter script declares, in declaration order.
        private static List<string> ReadDrafterKinds(string script)
        {
            List<string> kinds = new List<string>();
            int start = script.IndexOf("KIND_MEANINGS = Object.freeze({", StringComparison.Ordinal);
            if (start < 0) return kinds;
            int end = script.IndexOf("});", start, StringComparison.Ordinal);
            if (end <= start) return kinds;

            foreach (string line in script.Substring(start, end - start).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                int colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                string name = trimmed.Substring(0, colon).Trim();
                if (name.Length > 0 && name.All(c => Char.IsLetter(c) || c == '_')) kinds.Add(name);
            }

            return kinds;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                    Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }

        private static string Compact(string json)
        {
            return json.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        }

        #region Private-Types

        private static CustomTypedDecisionSettings CustomDecision(TypedDecisionModeEnum mode)
        {
            return new CustomTypedDecisionSettings
            {
                Mode = mode,
                GateThreshold = 0.9,
                Surface = CustomDecisionSurfaceEnum.CaptainTool,
                Questions = new List<CustomTypedQuestionSettings>
                {
                    new CustomTypedQuestionSettings { Id = "finding", Type = "noul", Instructions = "The change has the finding." }
                }
            };
        }

        private sealed class Harness
        {
            public Dictionary<string, Func<JsonElement?, Task<object>>> Handlers { get; } = new Dictionary<string, Func<JsonElement?, Task<object>>>();

            public static Harness Create(
                TestDatabase testDb,
                ITypedDecisionClient client,
                bool enabled,
                bool enablePremiseCheck = false,
                bool enableMemoryRecord = false,
                bool enableCorpusPrelabel = false,
                Action<ArmadaSettings>? configure = null,
                string? participantKey = null)
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.TypedDecisions.CaptainTool.Enabled = enabled;
                settings.TypedDecisions.Decisions["premise_check"].Mode = enablePremiseCheck ? TypedDecisionModeEnum.Gate : TypedDecisionModeEnum.Off;
                settings.TypedDecisions.Decisions["memory_record"].Mode = enableMemoryRecord ? TypedDecisionModeEnum.Gate : TypedDecisionModeEnum.Off;
                settings.TypedDecisions.Decisions["corpus_prelabel"].Mode = enableCorpusPrelabel ? TypedDecisionModeEnum.Gate : TypedDecisionModeEnum.Off;
                configure?.Invoke(settings);

                Harness harness = new Harness();
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());
                McpTypedDecisionTools.Register(
                    (name, _, _, handler) => { harness.Handlers[name] = handler; },
                    testDb.Driver,
                    client,
                    recorder,
                    settings,
                    new LoggingModule(),
                    () => participantKey);
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

            public IReadOnlyDictionary<string, TypedQuestion>? LastQuestions { get; private set; }

            public TypedDecisionResult NextResult { get; set; } = new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>()
            };

            public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
            {
                CallCount++;
                LastState = Armada.Test.Unit.TestHelpers.FakeTypedDecisionClient.StateText(request);
                LastQuestionIds.Clear();
                LastQuestions = request.Questions;
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
