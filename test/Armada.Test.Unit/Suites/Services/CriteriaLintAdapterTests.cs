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
    /// Table-driven tests for the D10 criteria_lint adapter. The adapter runs on a finalized refinement
    /// summary and only ever APPENDS criteria_review lines: it never rewrites, reorders, or removes a
    /// criterion. Off makes no call and appends nothing; an unavailable model appends nothing and
    /// records one unavailable event; a below-threshold answer appends nothing and records a shadow
    /// event per criterion; a Gate answer at or above threshold appends review lines and records a
    /// gated event per criterion. Every case proves the acceptance-criteria list is untouched. The
    /// adapter never throws and forwards the caller's token so the client timeout links to it.
    /// </summary>
    public class CriteriaLintAdapterTests : TestSuite
    {
        public override string Name => "Criteria Lint Adapter (D10)";

        private const double _Threshold = 0.80;

        // Two criteria, so a per-criterion loop is exercised (2 calls, 2 events in the running cases).
        private static readonly List<string> _Criteria = new List<string>
        {
            "The decode reproduces the source polarity.",
            "0 failed."
        };

        protected override async Task RunTestsAsync()
        {
            List<LintCase> cases = new List<LintCase>
            {
                new LintCase
                {
                    Name = "Off_NoCall_NoAppend",
                    GlobalMode = TypedDecisionModeEnum.Off,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Flagged(0.95),
                    ExpectCalls = 0,
                    ExpectEventType = null,
                    ExpectEventCount = 0,
                    ExpectAppended = false
                },
                new LintCase
                {
                    Name = "Unavailable_NoAppend_UnavailableEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Unavailable("http_429"),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeUnavailable,
                    ExpectEventCount = 1,
                    ExpectAppended = false
                },
                new LintCase
                {
                    Name = "BelowThreshold_NoAppend_ShadowEvents",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Flagged(0.40),
                    ExpectCalls = 2,
                    ExpectEventType = TypedDecisionRecorder.EventTypeShadow,
                    ExpectEventCount = 2,
                    ExpectAppended = false
                },
                new LintCase
                {
                    Name = "GateAboveThreshold_Appends_GatedEvents",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Flagged(0.95),
                    ExpectCalls = 2,
                    ExpectEventType = TypedDecisionRecorder.EventTypeGated,
                    ExpectEventCount = 2,
                    ExpectAppended = true
                },
                new LintCase
                {
                    Name = "ShadowMode_NoAppend_ShadowEvents",
                    GlobalMode = TypedDecisionModeEnum.Shadow,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Flagged(0.95),
                    ExpectCalls = 2,
                    ExpectEventType = TypedDecisionRecorder.EventTypeShadow,
                    ExpectEventCount = 2,
                    ExpectAppended = false
                }
            };

            foreach (LintCase testCase in cases)
            {
                await RunTest("CriteriaLint_" + testCase.Name, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(testCase.Result);
                    CriteriaLintAdapter adapter = BuildAdapter(testDb.Driver, client, testCase.GlobalMode, testCase.DecisionMode);

                    ObjectiveRefinementSummaryResponse summary = SeededSummary();
                    List<string> originalCriteria = new List<string>(summary.AcceptanceCriteria);
                    string originalSummary = summary.Summary;

                    await adapter.EvaluateAsync(summary, ObjectiveKindEnum.Feature, CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(testCase.ExpectCalls, client.Calls, "client call count");

                    // The adapter NEVER rewrites, reorders, or removes a criterion.
                    AssertTrue(summary.AcceptanceCriteria.SequenceEqual(originalCriteria), "the acceptance-criteria list is untouched");

                    if (testCase.ExpectAppended)
                    {
                        AssertTrue(summary.Summary.Contains(CriteriaLintAdapter.ReviewHeader, StringComparison.Ordinal),
                            "criteria_review lines are appended to the summary");
                        AssertTrue(summary.Summary.StartsWith(originalSummary, StringComparison.Ordinal),
                            "the original summary text is preserved before the appended lines");
                    }
                    else
                    {
                        AssertEqual(originalSummary, summary.Summary, "the summary is unchanged");
                    }

                    List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                    if (testCase.ExpectEventType == null)
                    {
                        AssertEqual(0, typed.Count, "no typed-decision event when the decision is off");
                    }
                    else
                    {
                        AssertEqual(testCase.ExpectEventCount, typed.Count, "typed-decision event count");
                        AssertTrue(typed.All(evt => evt.EventType == testCase.ExpectEventType), "every event is the expected type");
                    }
                });
            }

            await RunTest("CriteriaLint_ForwardsCallerTokenToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Flagged(0.10));
                CriteriaLintAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.EvaluateAsync(SeededSummary(), ObjectiveKindEnum.Feature, cts.Token).ConfigureAwait(false);

                AssertTrue(client.Calls >= 1, "the client was called");
                AssertTrue(client.LastToken.Equals(cts.Token), "the adapter forwards the caller's token so the client timeout applies");
            });

            await RunTest("CriteriaLint_ClientThrows_NeverThrows_SummaryStands", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                CriteriaLintAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                ObjectiveRefinementSummaryResponse summary = SeededSummary();
                string original = summary.Summary;

                // Must not throw.
                await adapter.EvaluateAsync(summary, ObjectiveKindEnum.Feature, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(original, summary.Summary, "a client fault appends nothing; the summary stands");
                List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertEqual(1, typed.Count, "a client fault records one unavailable event");
                AssertEqual(TypedDecisionRecorder.EventTypeUnavailable, typed[0].EventType, "the fault event is unavailable");
            });

            await RunTest("CriteriaLint_ForwardsRedactedStateAndQuestions", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Flagged(0.10));
                CriteriaLintAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                await adapter.EvaluateAsync(SeededSummary(), ObjectiveKindEnum.Feature, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(client.LastRequest != null, "the client received a request");
                AssertEqual(CriteriaLintAdapter.DecisionPoint, client.LastRequest!.DecisionPoint, "the request names the criteria_lint decision");
                AssertTrue(client.LastRequest.Questions.ContainsKey("presence_test"), "the presence-test noul is asked");
                AssertTrue(client.LastRequest.Questions.ContainsKey("pins_total"), "the pins-total noul is asked");
                AssertTrue(client.LastRequest.Questions.ContainsKey("empty_diff"), "the empty-diff noul is asked");
            });
        }

        private static CriteriaLintAdapter BuildAdapter(
            DatabaseDriver database,
            FakeTypedDecisionClient client,
            TypedDecisionModeEnum globalMode,
            TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions[CriteriaLintAdapter.DecisionPoint].Mode = decisionMode;
            settings.Decisions[CriteriaLintAdapter.DecisionPoint].GateThreshold = _Threshold;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new CriteriaLintAdapter(settings, client, recorder, new LoggingModule());
        }

        private static ObjectiveRefinementSummaryResponse SeededSummary()
        {
            return new ObjectiveRefinementSummaryResponse
            {
                Summary = "Port only the missing frame; consume the landed parameter seam.",
                AcceptanceCriteria = new List<string>(_Criteria)
            };
        }

        // Every criterion noul answered at the same value, so a case scores every criterion the same way.
        private static TypedDecisionResult Flagged(double value)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["presence_test"] = new TypedAnswer { Type = "noul", Noul = value, Confidence = value },
                ["pins_total"] = new TypedAnswer { Type = "noul", Noul = value, Confidence = value },
                ["not_observable"] = new TypedAnswer { Type = "noul", Noul = 0.0, Confidence = 0.0 },
                ["empty_diff"] = new TypedAnswer { Type = "noul", Noul = 0.0, Confidence = 0.0 },
                ["mixes_behaviours"] = new TypedAnswer { Type = "noul", Noul = 0.0, Confidence = 0.0 }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 12, OutputTokens = 6, LatencyMs = 15 };
        }

        private static async Task<List<ArmadaEvent>> AllTypedDecisionEventsAsync(DatabaseDriver database)
        {
            List<ArmadaEvent> all = new List<ArmadaEvent>();
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 50).ConfigureAwait(false));
            return all;
        }

        private sealed class LintCase
        {
            public required string Name { get; init; }
            public required TypedDecisionModeEnum GlobalMode { get; init; }
            public required TypedDecisionModeEnum DecisionMode { get; init; }
            public required TypedDecisionResult Result { get; init; }
            public required int ExpectCalls { get; init; }
            public required string? ExpectEventType { get; init; }
            public required int ExpectEventCount { get; init; }
            public required bool ExpectAppended { get; init; }
        }
    }
}
