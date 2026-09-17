namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class TypedDecisionEvalTests : TestSuite
    {
        public override string Name => "Typed Decision Evaluation Set";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Catalog_EveryCaseTestsQuestionsItsDecisionActuallyAsks", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                List<TypedDecisionEvalCase> cases = TypedDecisionEvalCatalog.Build(
                    new TypedDecisionRecorder(db.Driver, new LoggingModule()), new TypedDecisionSettings(), new LoggingModule());

                HashSet<string> decisions = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (TypedDecisionEvalCase evalCase in cases)
                {
                    AssertTrue(ids.Add(evalCase.Id), "case ids are unique: " + evalCase.Id);
                    decisions.Add(evalCase.DecisionPoint);
                    AssertNotNull(evalCase.VariantB, evalCase.Id + " is a pair");
                    AssertTrue(evalCase.VariantA.State.Text.Length > 0, evalCase.Id + " has state");
                    AssertFalse(evalCase.VariantA.State.Text.Contains("msn_eval", StringComparison.Ordinal), evalCase.Id + " state is redacted like production");

                    if (evalCase.Kind == TypedDecisionEvalCaseKindEnum.Consistency)
                    {
                        AssertTrue(evalCase.ConsistentQuestionIds.Count > 0, evalCase.Id + " names answers that must agree");
                        foreach (string questionId in evalCase.ConsistentQuestionIds)
                            AssertTrue(evalCase.VariantA.Questions.ContainsKey(questionId), evalCase.Id + " asks " + questionId);
                    }
                    else
                    {
                        AssertTrue(evalCase.ExpectedA.Count > 0 && evalCase.ExpectedB.Count > 0, evalCase.Id + " has reference answers for both variants");
                        foreach (KeyValuePair<string, TypedDecisionExpectation> entry in evalCase.ExpectedA)
                            AssertExpectationFitsQuestion(evalCase.Id + " A", entry.Key, entry.Value, evalCase.VariantA.Questions);
                        foreach (KeyValuePair<string, TypedDecisionExpectation> entry in evalCase.ExpectedB)
                            AssertExpectationFitsQuestion(evalCase.Id + " B", entry.Key, entry.Value, evalCase.VariantB!.Questions);
                    }
                }

                foreach (string gated in new[] { "failure_cause", "refusal", "runtime_failure", "review_substance", "lint_finding" })
                    AssertTrue(decisions.Contains(gated), "the set covers " + gated);
            });

            await RunTest("Runner_ReferenceCase_PassesOnlyWhenEveryAnswerHolds", async () =>
            {
                TypedDecisionEvalCase evalCase = ReferenceCase();

                TypedDecisionEvalReport pass = await TypedDecisionEvalRunner.RunAsync(
                    new ScriptedClient(Choice("cause", "environmental"), Choice("cause", "work_defect")),
                    new List<TypedDecisionEvalCase> { evalCase }, "operator", CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, pass.Passed);
                AssertEqual("jev-test", pass.Model);
                AssertEqual("passed", pass.Cases[0].Outcome);

                TypedDecisionEvalReport fail = await TypedDecisionEvalRunner.RunAsync(
                    new ScriptedClient(Choice("cause", "environmental"), Choice("cause", "environmental")),
                    new List<TypedDecisionEvalCase> { evalCase }, "operator", CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, fail.Failed);
                AssertContains("B.cause: expected choice work_defect, got environmental", fail.Cases[0].Failures[0]);
            });

            await RunTest("Runner_ScoreLevelsProbability_SumsTheLowLevels", async () =>
            {
                TypedDecisionEvalCase evalCase = new TypedDecisionEvalCase
                {
                    Id = "test.levels",
                    DecisionPoint = "review_substance",
                    VariantA = Item("substantiated"),
                    ExpectedA = new Dictionary<string, TypedDecisionExpectation>
                    {
                        ["substantiated"] = new TypedDecisionExpectation { ScoreLevelsUpTo = 1, ScoreLevelsProbabilityAtLeast = 0.85 }
                    }
                };
                TypedDecisionResult split = new TypedDecisionResult
                {
                    Available = true,
                    Answers = new Dictionary<string, TypedAnswer>
                    {
                        ["substantiated"] = new TypedAnswer { Type = "score", Score = 1.06, Probabilities = new Dictionary<string, double> { ["0"] = 0.52, ["1"] = 0.18, ["2"] = 0.02, ["3"] = 0.28 } }
                    }
                };

                TypedDecisionEvalReport report = await TypedDecisionEvalRunner.RunAsync(
                    new ScriptedClient(split), new List<TypedDecisionEvalCase> { evalCase }, "operator", CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, report.Failed);
                AssertContains("expected P(level <= 1) >= 0.85, got 0.70", report.Cases[0].Failures[0]);
            });

            await RunTest("Runner_ConsistencyCase_FailsWhenAnIrrelevantChangeFlipsTheAnswer", async () =>
            {
                TypedDecisionEvalCase evalCase = new TypedDecisionEvalCase
                {
                    Id = "test.consistency",
                    DecisionPoint = "runtime_failure",
                    Kind = TypedDecisionEvalCaseKindEnum.Consistency,
                    VariantA = Item("kind"),
                    VariantB = Item("kind"),
                    ConsistentQuestionIds = new List<string> { "kind" }
                };

                TypedDecisionEvalReport agree = await TypedDecisionEvalRunner.RunAsync(
                    new ScriptedClient(Choice("kind", "usage_limit"), Choice("kind", "usage_limit")),
                    new List<TypedDecisionEvalCase> { evalCase }, "operator", CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, agree.Passed);

                TypedDecisionEvalReport flip = await TypedDecisionEvalRunner.RunAsync(
                    new ScriptedClient(Choice("kind", "usage_limit"), Choice("kind", "crash")),
                    new List<TypedDecisionEvalCase> { evalCase }, "operator", CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, flip.Failed);
                AssertContains("choice changed from usage_limit to crash", flip.Cases[0].Failures[0]);
            });

            await RunTest("Runner_Unavailable_IsNeitherPassedNorFailed", async () =>
            {
                TypedDecisionEvalReport report = await TypedDecisionEvalRunner.RunAsync(
                    new ScriptedClient(new TypedDecisionResult { Available = false, UnavailableReason = "http_429" }),
                    new List<TypedDecisionEvalCase> { ReferenceCase() }, "operator", CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, report.Unavailable);
                AssertEqual(0, report.Passed);
                AssertEqual(0, report.Failed);
                AssertEqual("http_429", report.Cases[0].UnavailableReason);
            });

            await RunTest("Service_NewModelVersion_RunsOnceAndRecordsEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CountingClient client = new CountingClient();
                TypedDecisionEvalService service = new TypedDecisionEvalService(
                    client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), new TypedDecisionSettings(), db.Driver, new LoggingModule());

                service.ObserveModel("jev-2.0.0");
                List<ArmadaEvent> events = await WaitForEvalEventsAsync(db, 1).ConfigureAwait(false);
                AssertEqual(1, events.Count, "a new model version runs the set once");
                AssertContains("model=jev-2.0.0", events[0].Message);
                int callsAfterFirstRun = client.Calls;
                AssertTrue(callsAfterFirstRun > 0, "the run called the provider");

                service.ObserveModel("jev-2.0.0");
                await Task.Delay(300).ConfigureAwait(false);
                AssertEqual(callsAfterFirstRun, client.Calls, "an already evaluated model version does not run again");
                AssertEqual(1, (await db.Driver.Events.EnumerateByTypeAsync(TypedDecisionEvalService.EventType, 10).ConfigureAwait(false)).Count);
            });

            await RunTest("Service_ModelChangeDisabled_DoesNotRun", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CountingClient client = new CountingClient();
                TypedDecisionEvalService service = new TypedDecisionEvalService(
                    client, new TypedDecisionRecorder(db.Driver, new LoggingModule()),
                    new TypedDecisionSettings { EvalOnModelChange = false }, db.Driver, new LoggingModule());

                service.ObserveModel("jev-2.0.0");
                await Task.Delay(300).ConfigureAwait(false);

                AssertEqual(0, client.Calls);
            });
        }

        private void AssertExpectationFitsQuestion(string label, string questionId, TypedDecisionExpectation expectation, IReadOnlyDictionary<string, TypedQuestion> questions)
        {
            AssertTrue(questions.TryGetValue(questionId, out TypedQuestion? question), label + " expects an answer to a question the decision asks: " + questionId);
            if (expectation.Choice != null)
            {
                AssertTrue(question is ChoiceQuestion, label + " " + questionId + " is a choice question");
                AssertTrue(((ChoiceQuestion)question!).Criteria.ContainsKey(expectation.Choice), label + " " + questionId + " offers " + expectation.Choice);
            }
            if (expectation.NoulAtLeast.HasValue || expectation.NoulAtMost.HasValue)
                AssertTrue(question is NoulQuestion, label + " " + questionId + " is a noul question");
            if (expectation.ScoreAtLeast.HasValue || expectation.ScoreAtMost.HasValue || expectation.ScoreLevelsProbabilityAtLeast.HasValue)
                AssertTrue(question is ScoreQuestion, label + " " + questionId + " is a score question");
        }

        private static async Task<List<ArmadaEvent>> WaitForEvalEventsAsync(TestDatabase db, int count)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<ArmadaEvent> events = new List<ArmadaEvent>();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                events = await db.Driver.Events.EnumerateByTypeAsync(TypedDecisionEvalService.EventType, 10).ConfigureAwait(false);
                if (events.Count >= count) break;
                await Task.Delay(50).ConfigureAwait(false);
            }
            return events;
        }

        private static TypedDecisionEvalCase ReferenceCase()
        {
            return new TypedDecisionEvalCase
            {
                Id = "test.reference",
                DecisionPoint = "failure_cause",
                VariantA = Item("cause"),
                VariantB = Item("cause"),
                ExpectedA = new Dictionary<string, TypedDecisionExpectation> { ["cause"] = new TypedDecisionExpectation { Choice = "environmental" } },
                ExpectedB = new Dictionary<string, TypedDecisionExpectation> { ["cause"] = new TypedDecisionExpectation { Choice = "work_defect" } }
            };
        }

        private static TypedDecisionBatchItem Item(string questionId)
        {
            return new TypedDecisionBatchItem(
                RedactedDecisionState.FromText("synthetic"),
                new Dictionary<string, TypedQuestion> { [questionId] = new NoulQuestion("placeholder") });
        }

        private static TypedDecisionResult Choice(string questionId, string choice)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Model = "jev-test",
                Answers = new Dictionary<string, TypedAnswer> { [questionId] = new TypedAnswer { Type = "choice", Choice = choice, Confidence = 0.9 } }
            };
        }

        private sealed class ScriptedClient : ITypedDecisionClient
        {
            private readonly Queue<TypedDecisionResult> _Results;
            private readonly TypedDecisionResult _Last;

            public ScriptedClient(params TypedDecisionResult[] results)
            {
                _Results = new Queue<TypedDecisionResult>(results);
                _Last = results[results.Length - 1];
            }

            public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
            {
                return Task.FromResult(_Results.Count > 0 ? _Results.Dequeue() : _Last);
            }
        }

        private sealed class CountingClient : ITypedDecisionClient
        {
            private int _Calls;

            public int Calls => _Calls;

            public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
            {
                Interlocked.Increment(ref _Calls);
                return Task.FromResult(new TypedDecisionResult
                {
                    Available = true,
                    Model = "jev-2.0.0",
                    Answers = new Dictionary<string, TypedAnswer>()
                });
            }
        }
    }
}
