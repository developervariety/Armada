namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Table-driven tests for the D5 preflight text-half adapter. The adapter runs AFTER the
    /// deterministic preflight block, so every case seeds the preview with one deterministic Error
    /// issue and proves it still stands: the model only ADDS issues. Off makes no call and changes
    /// nothing; an unavailable model records one unavailable event and changes nothing; a below-
    /// threshold answer records one rule-kept event and changes nothing; a Gate answer at or above the
    /// threshold adds blocking questions as Error issues and keeps Q4/Q13 as advisory Warnings. An
    /// advisory-only result records a rule-kept event; a blocking result records a gated event. Q13 does
    /// not post an owner-addressed note. The adapter never throws into the caller and
    /// forwards the caller's token so the client timeout links to it.
    /// </summary>
    public class PreflightTextAdapterTests : TestSuite
    {
        public override string Name => "Preflight Text Adapter (D5)";

        private const string _Vessel = "vsl_example";
        private const string _DeterministicIssue = "objective_preflight_incomplete";
        private const double _Threshold = 0.80;

        protected override async Task RunTestsAsync()
        {
            List<PreflightCase> cases = new List<PreflightCase>
            {
                new PreflightCase
                {
                    Name = "Off_DeterministicOnly_NoCallNoEvent",
                    GlobalMode = TypedDecisionModeEnum.Off,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.99 }, null, 0.0),
                    ExpectClientCalls = 0,
                    ExpectTypedEvent = null,
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 0
                },
                new PreflightCase
                {
                    Name = "Unavailable_DeterministicOnly_UnavailableEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Unavailable("http_429"),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeUnavailable,
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 0
                },
                new PreflightCase
                {
                    Name = "BelowThreshold_DeterministicOnly_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.50, ["q9"] = 0.40 }, "none", 0.10),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeRuleKept,
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 0
                },
                new PreflightCase
                {
                    Name = "GateAboveThreshold_AddsBlockingErrors_GatedEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.95, ["q9"] = 0.90, ["q5"] = 0.20 }, "none", 0.05),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectModelErrorIssues = 2,
                    ExpectModelWarningIssues = 0
                },
                new PreflightCase
                {
                    Name = "GateQ13OwnerRuling_AddsWarning_NoNote_AdvisoryEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "needs_owner_ruling", 0.92),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeRuleKept,
                    ExpectGateOutcome = "advisory_only",
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 1
                },
                new PreflightCase
                {
                    Name = "GateQ13NeedsRepoFact_AddsWarning_NoNote_AdvisoryEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "needs_repo_fact", 0.90),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeRuleKept,
                    ExpectGateOutcome = "advisory_only",
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 1
                },
                new PreflightCase
                {
                    Name = "GateQ4_AddsWarning_NoBlock_AdvisoryEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q4"] = 0.91 }, "none", 0.10),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeRuleKept,
                    ExpectGateOutcome = "advisory_only",
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 1
                },
                new PreflightCase
                {
                    Name = "GateBlockingQuestionAndQ13_AddsErrorAndWarning_GatedEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.91, ["q4"] = 0.92 }, "needs_owner_ruling", 0.93),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectGateOutcome = "applied",
                    ExpectModelErrorIssues = 1,
                    ExpectModelWarningIssues = 2
                },
                new PreflightCase
                {
                    Name = "GateQ1WithPassingCitationFact_RaisesNoFlag_AdvisoryEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.95 }, "none", 0.05),
                    PassingCitationFact = true,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeRuleKept,
                    ExpectGateOutcome = "advisory_only",
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 0
                },
                new PreflightCase
                {
                    Name = "GateQ1AndQ9WithPassingCitationFact_BlocksOnQ9Only_GatedEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.95, ["q9"] = 0.90 }, "none", 0.05),
                    PassingCitationFact = true,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectGateOutcome = "applied",
                    ExpectModelErrorIssues = 1,
                    ExpectModelWarningIssues = 0
                },
                new PreflightCase
                {
                    Name = "ShadowMode_AddsWarningNotError_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Shadow,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.95 }, "needs_owner_ruling", 0.95),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeRuleKept,
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 2
                }
            };

            foreach (PreflightCase testCase in cases)
            {
                await RunTest("Preflight_" + testCase.Name, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(testCase.Result);
                    PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, testCase.GlobalMode, testCase.DecisionMode);

                    ObjectiveDispatchPreview preview = SeededPreview();
                    if (testCase.PassingCitationFact)
                    {
                        preview.Preflight.Facts.Add(new ObjectiveDispatchPreflightFact
                        {
                            QuestionNumber = 1,
                            Status = PreflightFactStatusEnum.Pass,
                            Detail = "Every cited path and line range resolves at the target tip."
                        });
                    }

                    await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), preview, CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(testCase.ExpectClientCalls, client.Calls, "client call count");

                    AssertTrue(
                        preview.Issues.Any(issue => issue.Code == _DeterministicIssue && issue.Severity == ReadinessSeverityEnum.Error),
                        "the deterministic Error issue still stands");

                    int modelErrors = preview.Issues.Count(issue =>
                        issue.Code == PreflightTextAdapter.ModelFlagIssueCode && issue.Severity == ReadinessSeverityEnum.Error);
                    AssertEqual(testCase.ExpectModelErrorIssues, modelErrors, "model Error issue count");

                    int modelWarnings = preview.Issues.Count(issue =>
                        issue.Code.StartsWith("preflight_q", StringComparison.Ordinal)
                        && issue.Code.EndsWith("_model", StringComparison.Ordinal)
                        && issue.Severity == ReadinessSeverityEnum.Warning);
                    AssertEqual(testCase.ExpectModelWarningIssues, modelWarnings, "model Warning issue count");

                    List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                    if (testCase.ExpectTypedEvent == null)
                    {
                        AssertEqual(0, typed.Count, "no typed-decision event when the decision is off");
                    }
                    else
                    {
                        AssertEqual(1, typed.Count, "exactly one typed-decision event per call");
                        AssertEqual(testCase.ExpectTypedEvent, typed[0].EventType, "typed-decision event type");
                        if (testCase.ExpectGateOutcome != null)
                            AssertContains("\"gate_outcome\":\"" + testCase.ExpectGateOutcome + "\"", typed[0].Payload ?? "", "typed-decision outcome");
                    }
                });
            }

            await RunTest("Preflight cache: a repeat of the same state makes one client call and applies identical issues", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlaggingResult("jev-1.13.0"));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate, out TypedDecisionSettings _);

                ObjectiveDispatchPreview fresh = SeededPreview();
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), fresh, CancellationToken.None).ConfigureAwait(false);
                ObjectiveDispatchPreview cached = SeededPreview();
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), cached, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.Calls, "the repeat is answered from the cache");
                AssertEqual(DescribeIssues(fresh), DescribeIssues(cached), "cached and fresh answers apply the same issues");
                AssertTrue(fresh.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode), "the seed flags a blocking question");

                List<ArmadaEvent> calls = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertEqual(1, calls.Count, "only the fresh call records a typed-decision call event");
                List<ArmadaEvent> hits = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeCacheHit, 50).ConfigureAwait(false);
                AssertEqual(1, hits.Count, "the repeat records one cache-hit event");
                AssertContains("\"gate_outcome\":\"applied\"", hits[0].Payload ?? "", "the cache hit records the outcome its answers produced");
                AssertContains("\"input_tokens\":0", hits[0].Payload ?? "", "a cache hit records no tokens");
            });

            await RunTest("Preflight cache: a changed state, question contract, configured model or reported model each make a new call", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                string reportedModel = "jev-1.13.0";
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(_ => FlaggingResult(reportedModel));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate, out TypedDecisionSettings settings);

                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, client.Calls, "the first state is asked");

                Objective changed = Objective();
                changed.Title = "Port the vendor telemetry result decoder and its checksum";
                await adapter.EvaluateAsync(changed, Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(2, client.Calls, "a changed state misses");

                Func<IReadOnlyDictionary<string, TypedQuestion>> shipped = adapter.QuestionFactory;
                adapter.QuestionFactory = () =>
                {
                    Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(shipped(), StringComparer.Ordinal);
                    questions["q1"] = new NoulQuestion("A reworded premise question.");
                    return questions;
                };
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(3, client.Calls, "a changed question contract misses");
                adapter.QuestionFactory = shipped;

                settings.Model = "jev-pinned";
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(4, client.Calls, "a changed configured model misses");
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(4, client.Calls, "the same state under the same model hits");

                reportedModel = "jev-1.14.0";
                await adapter.EvaluateAsync(changed, Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(5, client.Calls, "a state not cached under this model is asked, and reports a new model version");
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(6, client.Calls, "a newly reported model version empties the cache");
            });

            await RunTest("Preflight cache: an unavailable answer is not cached", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "http_402" });
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate, out TypedDecisionSettings _);

                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(2, client.Calls, "an unavailable answer is asked again");
            });

            await RunTest("Preflight_ForwardsCallerTokenToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), cts.Token).ConfigureAwait(false);

                AssertEqual(1, client.Calls, "the client was called");
                AssertTrue(client.LastToken.Equals(cts.Token), "the adapter forwards the caller's token so the client timeout applies");
            });

            await RunTest("Preflight_ClientThrows_NeverThrows_DeterministicStands", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                ObjectiveDispatchPreview preview = SeededPreview();

                // Must not throw.
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, preview.Issues.Count(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode),
                    "a client fault adds no model issue; the deterministic preview stands");
                AssertTrue(
                    preview.Issues.Any(issue => issue.Code == _DeterministicIssue),
                    "the deterministic Error issue still stands after a client fault");

                List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertEqual(1, typed.Count, "a client fault records one unavailable event");
                AssertEqual(TypedDecisionRecorder.EventTypeUnavailable, typed[0].EventType, "the fault event is unavailable");
            });

            await RunTest("Preflight_ForwardsRedactedStateToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(client.LastRequest != null, "the client received a request");
                AssertEqual(PreflightTextAdapter.DecisionPoint, client.LastRequest!.DecisionPoint, "the request names the preflight decision");
                string state = FakeTypedDecisionClient.StateText(client.LastRequest);
                AssertTrue(state.Contains("#id", StringComparison.Ordinal), "the redactor replaced the objective id in the transmitted state");
                AssertTrue(!state.Contains("obj_secret", StringComparison.Ordinal), "the raw objective id never egresses");
                AssertTrue(client.LastRequest.Questions.ContainsKey("q13"), "the Q13 owner-question choice is asked");
                AssertTrue(client.LastRequest.Questions.ContainsKey("q1"), "the Q1 premise-versus-facts noul is asked");
            });

            await RunTest("Preflight_DockObservability_AndExampleValue_QuestionsJudgeAgainstTheDockAndExemptControls", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);

                // Q4 judges observability against what the dock holds, and Q9 exempts a stated control value.
                NoulQuestion q4 = (NoulQuestion)client.LastRequest!.Questions["q4"];
                AssertContains("dock context", q4.Instructions, "Q4 judges observability against the dock context");
                AssertContains("evidence root", q4.Instructions, "a listed evidence root is observable");
                NoulQuestion q9 = (NoulQuestion)client.LastRequest.Questions["q9"];
                AssertContains("unchanged on purpose", q9.Instructions, "a stated control value is not the Q9 defect");
                AssertContains("different defect", q9.Instructions, "a value guarding a separate defect is not the Q9 defect");
            });

            await RunTest("Preflight_DescribeRequest_BuildsTheStateAndQuestionsTheProviderReceives", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                Objective objective = Objective();

                TypedDecisionBatchItem? described = adapter.DescribeRequest(objective, Vessel(), Pipeline(), SeededPreview());
                await adapter.EvaluateAsync(objective, Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);

                // The evaluation set replays recorded briefs through this builder, so it must be what production sends.
                AssertNotNull(described, "the request builds");
                AssertEqual(JsonSerializer.Serialize(client.LastRequest!.State), JsonSerializer.Serialize(described!.State.State),
                    "the described state is the state the provider receives");
                AssertEqual(String.Join(",", client.LastRequest.Questions.Keys), String.Join(",", described.Questions.Keys),
                    "the described questions are the questions the provider receives");
                AssertEqual(((NoulQuestion)client.LastRequest.Questions["q9"]).Instructions, ((NoulQuestion)described.Questions["q9"]).Instructions,
                    "the described q9 wording is the wording the provider receives");
            });

            await RunTest("Preflight_ExampleValueFlag_QuotesTheProofLines_AndTheDeclaredParityValuesApart", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    ModelResult(new Dictionary<string, double> { ["q9"] = 0.97, ["q1"] = 0.05 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                Objective objective = Objective();
                objective.Description = "Stop the per-poll warning.\n"
                    + "What proves the fix: each status poll writes one warning line before the change and zero after it.\n"
                    + "\n"
                    + "Parity values, unchanged on purpose:\n"
                    + "- the poll state stays rejected\n"
                    + "- the submit warning stays one line\n"
                    + "\n"
                    + "Keep the public response shape.";
                ObjectiveDispatchPreview preview = SeededPreview();

                await adapter.EvaluateAsync(objective, Vessel(), Pipeline(), preview, CancellationToken.None).ConfigureAwait(false);

                // The brief states one line before the change and zero after it, so the flag is a warning.
                ObjectiveDispatchPreviewIssue flag = preview.Issues.Single(issue =>
                    issue.Code == "preflight_q9_model" && issue.RelatedValue == "q9" && issue.Severity == ReadinessSeverityEnum.Warning);
                AssertContains("\"What proves the fix: each status poll writes one warning line before the change and zero after it\"", flag.Message,
                    "the q9 flag quotes the brief line that states the proof value");
                AssertContains("which question 9 excludes: \"Parity values, unchanged on purpose:\"; \"- the poll state stays rejected\"; \"- the submit warning stays one line\"",
                    flag.Message, "the declared parity section is quoted apart, as excluded");
                AssertFalse(flag.Message.Contains("Keep the public response shape", StringComparison.Ordinal),
                    "a line after the parity section that states no value is not quoted");

                ObjectiveDispatchPreview noProof = SeededPreview();
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), noProof, CancellationToken.None).ConfigureAwait(false);
                ObjectiveDispatchPreviewIssue bare = noProof.Issues.Single(issue =>
                    issue.Code == PreflightTextAdapter.ModelFlagIssueCode && issue.RelatedValue == "q9");
                AssertContains("No brief line states a proof or expected value", bare.Message,
                    "a brief without a proof line is told what to add");
            });

            await RunTest("Preflight_Q9Flag_IsAWarning_WhenTheBriefStatesDifferentProofValuesAndNoFactFails", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    ModelResult(new Dictionary<string, double> { ["q9"] = 0.95, ["q1"] = 0.05 }, "none", 0.05));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                string scope = "Scope rule: the freeze applies only to instruments whose expected session has ended.\n";

                Objective changes = Objective();
                changes.Description = scope + "What proves the fix: a fixture of four instruments freezes 2 after the fix and 3 at the old commit.";
                ObjectiveDispatchPreview demoted = SeededPreview();
                await adapter.EvaluateAsync(changes, Vessel(), Pipeline(), demoted, CancellationToken.None).ConfigureAwait(false);
                AssertFalse(demoted.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode),
                    "a q9 flag against a brief whose proof value changes does not block dispatch");
                ObjectiveDispatchPreviewIssue note = demoted.Issues.Single(issue => issue.Code == "preflight_q9_model");
                AssertEqual(ReadinessSeverityEnum.Warning, note.Severity, "the q9 flag stays visible as a warning");
                AssertContains("\"What proves the fix: a fixture of four instruments freezes 2 after the fix and 3 at the old commit\"", note.Message,
                    "the warning quotes the proof sentence the rule read");
                List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertContains("\"gate_outcome\":\"advisory_only\"", typed.Last().Payload ?? "", "the demoted flag records an advisory outcome");

                Objective same = Objective();
                same.Description = scope + "What proves the fix: a fixture of four instruments freezes 3 after the fix and 3 at the old commit.";
                ObjectiveDispatchPreview unchanged = SeededPreview();
                await adapter.EvaluateAsync(same, Vessel(), Pipeline(), unchanged, CancellationToken.None).ConfigureAwait(false);
                AssertTrue(unchanged.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode && issue.RelatedValue == "q9"),
                    "a proof value stated the same on both sides still blocks");

                ObjectiveDispatchPreview failing = SeededPreview();
                failing.Preflight.Facts.Add(new ObjectiveDispatchPreflightFact
                {
                    QuestionNumber = 2,
                    Status = PreflightFactStatusEnum.Fail,
                    Detail = "The objective kind does not match the deliverable."
                });
                await adapter.EvaluateAsync(changes, Vessel(), Pipeline(), failing, CancellationToken.None).ConfigureAwait(false);
                AssertTrue(failing.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode && issue.RelatedValue == "q9"),
                    "a failing deterministic fact keeps the q9 flag blocking");
            });

            await RunTest("Preflight_WithoutDockContext_StateSaysTheDockWasNotMeasured", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);

                AssertContains("\"dock\":{\"measured\":false}", FakeTypedDecisionClient.StateText(client.LastRequest),
                    "an unmeasured dock is stated, never sent as an empty dock");
            });

            await RunTest("Preflight_RuleAnswers_MapTheQ1FactAndLeaveUnansweredQuestionsNull", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                ObjectiveDispatchPreview preview = SeededPreview();
                preview.Preflight.Facts.Add(new ObjectiveDispatchPreflightFact
                {
                    QuestionNumber = 1,
                    Status = PreflightFactStatusEnum.Fail,
                    Detail = "A cited path does not resolve at the target tip."
                });

                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), preview, CancellationToken.None).ConfigureAwait(false);

                List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertEqual(1, typed.Count, "one call was recorded");
                string payload = typed[0].Payload ?? String.Empty;
                AssertContains("\"rule_answers\":{", payload, "the rule's answers are recorded");
                AssertContains("\"q1\":true", payload, "a failed Q1 fact answers the Q1 defect true");
                AssertContains("\"q4\":null", payload, "a question with no deterministic fact records null");
                AssertContains("\"q13\":null", payload, "the Q13 choice has no deterministic answer");
                AssertContains("\"subject_type\":\"objective\"", payload, "the preflight subject is the objective");
            });
        }

        private static PreflightTextAdapter BuildAdapter(
            DatabaseDriver database,
            FakeTypedDecisionClient client,
            TypedDecisionModeEnum globalMode,
            TypedDecisionModeEnum decisionMode)
        {
            return BuildAdapter(database, client, globalMode, decisionMode, out TypedDecisionSettings _);
        }

        private static PreflightTextAdapter BuildAdapter(
            DatabaseDriver database,
            FakeTypedDecisionClient client,
            TypedDecisionModeEnum globalMode,
            TypedDecisionModeEnum decisionMode,
            out TypedDecisionSettings settingsOut)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions[PreflightTextAdapter.DecisionPoint].Mode = decisionMode;
            settings.Decisions[PreflightTextAdapter.DecisionPoint].GateThreshold = _Threshold;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            settingsOut = settings;
            return new PreflightTextAdapter(settings, client, recorder, new LoggingModule());
        }

        // Flags the blocking Q5 noul above the threshold, so a Gate call applies a model Error issue.
        private static TypedDecisionResult FlaggingResult(string model)
        {
            TypedDecisionResult basis = ModelResult(new Dictionary<string, double> { ["q5"] = 0.97, ["q1"] = 0.05 }, "none", 0.10);
            return new TypedDecisionResult
            {
                Available = true,
                Answers = basis.Answers,
                Model = model,
                InputTokens = basis.InputTokens,
                OutputTokens = basis.OutputTokens,
                LatencyMs = basis.LatencyMs
            };
        }

        private static string DescribeIssues(ObjectiveDispatchPreview preview)
        {
            return String.Join(";", preview.Issues.Select(issue => issue.Code + "/" + issue.Severity + "/" + issue.Message));
        }

        private static Objective Objective()
        {
            return new Objective
            {
                Id = "obj_secret_row",
                Title = "Port the vendor telemetry result decoder",
                Description = "Confirm the response predicate at the target tip and port the missing decode (row obj_secret_row).",
                Kind = ObjectiveKindEnum.Feature,
                AcceptanceCriteria = new List<string> { "The decode reproduces the source polarity.", "0 failed." },
                NonGoals = new List<string> { "No legacy driver plumbing." },
                RefinementSummary = "Consume the landed parameter seam; port only the missing frame.",
                VesselIds = new List<string> { _Vessel }
            };
        }

        private static Vessel Vessel()
        {
            return new Vessel { Name = "ExampleVessel" };
        }

        private static Pipeline Pipeline()
        {
            return new Pipeline
            {
                Name = "Tested",
                Stages = new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker"),
                    new PipelineStage(2, "TestEngineer"),
                    new PipelineStage(3, "Judge")
                }
            };
        }

        private static ObjectiveDispatchPreview SeededPreview()
        {
            ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview
            {
                ObjectiveId = "obj_secret_row",
                VesselId = _Vessel
            };
            // The deterministic block always runs first; model runs after and only adds.
            preview.Issues.Add(new ObjectiveDispatchPreviewIssue
            {
                Code = _DeterministicIssue,
                Area = "preflight",
                Severity = ReadinessSeverityEnum.Error,
                Message = "The dispatch preflight is incomplete.",
                RelatedValue = "1, 4"
            });
            preview.Preflight.Facts.Add(new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = 3,
                Status = PreflightFactStatusEnum.Pass,
                Detail = "The objective names 1 target vessel(s); dispatch needs exactly one.",
                RecordedAnswer = ObjectivePreflightAnswerEnum.Yes
            });
            return preview;
        }

        private static TypedDecisionResult ModelResult(Dictionary<string, double> nouls, string? q13Choice, double q13Confidence)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> entry in nouls)
            {
                answers[entry.Key] = new TypedAnswer { Type = "noul", Noul = entry.Value, Confidence = entry.Value };
            }
            if (q13Choice != null)
            {
                answers["q13"] = new TypedAnswer { Type = "choice", Choice = q13Choice, Confidence = q13Confidence };
            }
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 12, OutputTokens = 6, LatencyMs = 20 };
        }

        private static async Task<List<ArmadaEvent>> AllTypedDecisionEventsAsync(DatabaseDriver database)
        {
            List<ArmadaEvent> all = new List<ArmadaEvent>();
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeRuleKept, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 50).ConfigureAwait(false));
            return all;
        }

        private sealed class PreflightCase
        {
            public required string Name { get; init; }
            public required TypedDecisionModeEnum GlobalMode { get; init; }
            public required TypedDecisionModeEnum DecisionMode { get; init; }
            public required TypedDecisionResult Result { get; init; }
            public required int ExpectClientCalls { get; init; }
            public required string? ExpectTypedEvent { get; init; }
            public string? ExpectGateOutcome { get; init; }
            public bool PassingCitationFact { get; init; }
            public required int ExpectModelErrorIssues { get; init; }
            public required int ExpectModelWarningIssues { get; init; }
        }
    }
}
