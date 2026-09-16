namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
    /// threshold answer records one shadow event and changes nothing; a Gate answer at or above the
    /// threshold adds one Error issue per flagged question and records a gated event; a Q13 owner
    /// ruling also posts one owner-addressed board note. The adapter never throws into the caller and
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
                    ExpectModelWarningIssues = 0,
                    ExpectNotes = 0
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
                    ExpectModelWarningIssues = 0,
                    ExpectNotes = 0
                },
                new PreflightCase
                {
                    Name = "BelowThreshold_DeterministicOnly_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.50, ["q9"] = 0.40 }, "none", 0.10),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 0,
                    ExpectNotes = 0
                },
                new PreflightCase
                {
                    Name = "GateAboveThreshold_AddsErrorIssues_GatedEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.95, ["q9"] = 0.90, ["q5"] = 0.20 }, "none", 0.05),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectModelErrorIssues = 2,
                    ExpectModelWarningIssues = 0,
                    ExpectNotes = 0
                },
                new PreflightCase
                {
                    Name = "GateQ13OwnerRuling_AddsErrorAndPostsNote_GatedEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "needs_owner_ruling", 0.92),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectModelErrorIssues = 1,
                    ExpectModelWarningIssues = 0,
                    ExpectNotes = 1
                },
                new PreflightCase
                {
                    Name = "GateQ13NeedsRepoFact_AddsError_NoNote",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "needs_repo_fact", 0.90),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectModelErrorIssues = 1,
                    ExpectModelWarningIssues = 0,
                    ExpectNotes = 0
                },
                new PreflightCase
                {
                    Name = "ShadowMode_AddsWarningNotError_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Shadow,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = ModelResult(new Dictionary<string, double> { ["q1"] = 0.95 }, "needs_owner_ruling", 0.95),
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectModelErrorIssues = 0,
                    ExpectModelWarningIssues = 2,
                    ExpectNotes = 0
                }
            };

            foreach (PreflightCase testCase in cases)
            {
                await RunTest("Preflight_" + testCase.Name, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(testCase.Result);
                    FakeOwnerDecisionNotePoster poster = new FakeOwnerDecisionNotePoster();
                    PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, poster, testCase.GlobalMode, testCase.DecisionMode);

                    ObjectiveDispatchPreview preview = SeededPreview();

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

                    AssertEqual(testCase.ExpectNotes, poster.Posts.Count, "owner-addressed note count");

                    List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                    if (testCase.ExpectTypedEvent == null)
                    {
                        AssertEqual(0, typed.Count, "no typed-decision event when the decision is off");
                    }
                    else
                    {
                        AssertEqual(1, typed.Count, "exactly one typed-decision event per call");
                        AssertEqual(testCase.ExpectTypedEvent, typed[0].EventType, "typed-decision event type");
                    }
                });
            }

            await RunTest("Preflight_ForwardsCallerTokenToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    ModelResult(new Dictionary<string, double> { ["q1"] = 0.10 }, "none", 0.10));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, new FakeOwnerDecisionNotePoster(),
                    TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), cts.Token).ConfigureAwait(false);

                AssertEqual(1, client.Calls, "the client was called");
                AssertTrue(client.LastToken.Equals(cts.Token), "the adapter forwards the caller's token so the client timeout applies");
            });

            await RunTest("Preflight_ClientThrows_NeverThrows_DeterministicStands", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, new FakeOwnerDecisionNotePoster(),
                    TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

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
                PreflightTextAdapter adapter = BuildAdapter(testDb.Driver, client, new FakeOwnerDecisionNotePoster(),
                    TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                await adapter.EvaluateAsync(Objective(), Vessel(), Pipeline(), SeededPreview(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(client.LastRequest != null, "the client received a request");
                AssertEqual(PreflightTextAdapter.DecisionPoint, client.LastRequest!.DecisionPoint, "the request names the preflight decision");
                string state = client.LastRequest.State as string ?? String.Empty;
                AssertTrue(state.Contains("#id", StringComparison.Ordinal), "the redactor replaced the objective id in the transmitted state");
                AssertTrue(!state.Contains("obj_secret", StringComparison.Ordinal), "the raw objective id never egresses");
                AssertTrue(client.LastRequest.Questions.ContainsKey("q13"), "the Q13 owner-question choice is asked");
                AssertTrue(client.LastRequest.Questions.ContainsKey("q1"), "the Q1 premise-versus-facts noul is asked");
            });
        }

        private static PreflightTextAdapter BuildAdapter(
            DatabaseDriver database,
            FakeTypedDecisionClient client,
            FakeOwnerDecisionNotePoster poster,
            TypedDecisionModeEnum globalMode,
            TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions[PreflightTextAdapter.DecisionPoint].Mode = decisionMode;
            settings.Decisions[PreflightTextAdapter.DecisionPoint].GateThreshold = _Threshold;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new PreflightTextAdapter(settings, client, recorder, poster, new LoggingModule());
        }

        private static Objective Objective()
        {
            return new Objective
            {
                Id = "obj_secret_row",
                Title = "Port the Caterpillar injector result decoder",
                Description = "Confirm the response predicate at the target tip and port the missing decode (row obj_secret_row).",
                Kind = ObjectiveKindEnum.Feature,
                AcceptanceCriteria = new List<string> { "The decode reproduces the source polarity.", "0 failed." },
                NonGoals = new List<string> { "No RP1210 driver plumbing." },
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
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow, 50).ConfigureAwait(false));
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
            public required int ExpectModelErrorIssues { get; init; }
            public required int ExpectModelWarningIssues { get; init; }
            public required int ExpectNotes { get; init; }
        }
    }
}
