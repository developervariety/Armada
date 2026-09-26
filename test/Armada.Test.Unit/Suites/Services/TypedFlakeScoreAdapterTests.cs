namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Table-driven tests for the D15 <c>flake_score</c> adapter and its gate integration. Off keeps the
    /// rule with no call; unavailable and shadow/below-threshold keep the rule and record an event; at or
    /// above threshold on a load or known-flaky level the adapter RECOMMENDS an isolated re-run — it
    /// never returns a passing verdict, so it never marks a red check green. The gate integration proves
    /// the re-run's real result is the truth: a passing isolated re-run clears the red, a failing one
    /// leaves it red. The caller token reaches the client, and a client fault never reaches the caller.
    /// </summary>
    public class TypedFlakeScoreAdapterTests : TestSuite
    {
        public override string Name => "Typed Flake Score Adapter (D15)";

        private const string _Decision = "flake_score";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.80)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static FlakeScoreDecisionInput BuildInput()
        {
            return new FlakeScoreDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Title = "port a decoder" },
                FailingTestNames = new List<string> { "Fleet.Core.Tests.SequenceRunnerTests.StepPauseMs_50" },
                AssertionLines = "Expected: Success, Actual: Timeout",
                TouchedFiles = new List<string> { "src/Fleet.Core/Decoder.cs" },
                SameTestFailedElsewhere24h = true,
                RuleClass = DefinitionOfDoneFailureClassEnum.TestFail
            };
        }

        // flake_likelihood score index + its confidence, outside_diff noul, optional isolation noul.
        private static TypedDecisionResult FlakeResult(double likelihoodIndex, double confidence, double outsideDiff, double isolation = 0.0)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["flake_likelihood"] = new TypedAnswer { Type = "score", Score = likelihoodIndex, Confidence = confidence },
                ["outside_diff"] = new TypedAnswer { Type = "noul", Noul = outsideDiff, Confidence = outsideDiff },
                ["passes_in_isolation"] = new TypedAnswer { Type = "noul", Noul = isolation }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedFlakeScoreAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedFlakeScoreAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsRule_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(2.0, 0.99, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.RerunRecommended, "Off must keep the rule (no re-run recommended)");
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "http_429" });
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.RerunRecommended, "unavailable must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(3.0, 0.99, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.RerunRecommended, "Shadow keeps the rule even when the model would recommend a re-run");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Deterministic_BelowAction_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // "deterministic" (index 0) proposes no action even with high confidence: the red stands.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(0.0, 0.99, 0.1));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.RerunRecommended, "a deterministic failure is never re-run");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("LikelyLoad_BelowThreshold_ReturnsRule", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // "likely load" (index 2) but the confidence is below threshold: no re-run.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(2.0, 0.50, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.RerunRecommended, "a below-threshold flake read does not trigger a re-run");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("LikelyLoad_AboveThreshold_RecommendsRerun_NeverGreen_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(2.0, 0.95, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.RerunRecommended, "a load flake at threshold recommends an isolated re-run");
                AssertEqual("likely load", result.Outcome);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("KnownFlakyFamily_AboveThreshold_RecommendsRerun", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(3.0, 0.99, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.RerunRecommended, "a known flaky family at threshold recommends an isolated re-run");
                AssertEqual("known flaky family", result.Outcome);
            }).ConfigureAwait(false);

            await RunTest("LikelyReal_HighIsolation_RecommendsRerun", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A wrong-value assertion the Score reads as "likely real" still proposes a re-run when
                // isolation is the finding: load-sensitive families fail with a wrong value under the suite.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(1.0, 0.92, 0.2, isolation: 0.96));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.RerunRecommended, "a high isolation noul recommends an isolated re-run even when the Score is likely-real");
                AssertEqual("passes in isolation", result.Outcome);
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                FlakeScoreVerdict result = await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.RerunRecommended, "a client fault never reaches the caller; the rule stands");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(2.0, 0.95, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), FlakeScoreVerdict.NoRerun(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);

            // Gate integration: the re-run's real result is the truth.

            await RunTest("Gate_RerunPasses_IsTruth_ClearsRed", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(2.0, 0.95, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                FlakeTestableGate gate = new FlakeTestableGate(db.Driver, adapter, DefinitionOfDoneResult.Pass());

                DefinitionOfDoneResult red = FailingTestResult();
                DefinitionOfDoneResult outcome = await gate.EvaluateFlakeRerunForTestAsync(
                    BuildInput().Mission!, "dotnet test src/Fleet.Core.Tests", "/tmp/dock", red, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(outcome.Passed, "a passing isolated re-run is the truth and clears the red");
                AssertEqual(1, gate.RerunCalls);
            }).ConfigureAwait(false);

            await RunTest("Gate_RerunFails_StaysRed_NeverGreen", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(3.0, 0.99, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DefinitionOfDoneResult rerunFail = DefinitionOfDoneResult.Fail("unit-test (flake re-run)", 1, "still failing", DefinitionOfDoneFailureClassEnum.TestFail);
                FlakeTestableGate gate = new FlakeTestableGate(db.Driver, adapter, rerunFail);

                DefinitionOfDoneResult outcome = await gate.EvaluateFlakeRerunForTestAsync(
                    BuildInput().Mission!, "dotnet test src/Fleet.Core.Tests", "/tmp/dock", FailingTestResult(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!outcome.Passed, "a failing isolated re-run leaves the red; the model never marks it green");
                AssertEqual(1, gate.RerunCalls);
            }).ConfigureAwait(false);

            await RunTest("Gate_NoRecommendation_NoRerun_RedStands", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Deterministic failure: the adapter recommends no re-run, so the gate never re-runs.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(0.0, 0.99, 0.1));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                FlakeTestableGate gate = new FlakeTestableGate(db.Driver, adapter, DefinitionOfDoneResult.Pass());

                DefinitionOfDoneResult red = FailingTestResult();
                DefinitionOfDoneResult outcome = await gate.EvaluateFlakeRerunForTestAsync(
                    BuildInput().Mission!, "dotnet test src/Fleet.Core.Tests", "/tmp/dock", red, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!outcome.Passed, "no recommendation means no re-run; the red stands");
                AssertEqual(0, gate.RerunCalls);
                AssertTrue(ReferenceEquals(outcome, red), "the original failing result is returned unchanged");
            }).ConfigureAwait(false);

            await RunTest("Gate_NonDotnetCommand_CannotIsolate_RedStands", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FlakeResult(2.0, 0.95, 0.9));
                TypedFlakeScoreAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                FlakeTestableGate gate = new FlakeTestableGate(db.Driver, adapter, DefinitionOfDoneResult.Pass());

                DefinitionOfDoneResult red = FailingTestResult();
                DefinitionOfDoneResult outcome = await gate.EvaluateFlakeRerunForTestAsync(
                    BuildInput().Mission!, "python3 -m unittest discover", "/tmp/dock", red, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!outcome.Passed, "an isolated command that cannot be formed means no re-run and the red stands");
                AssertEqual(0, gate.RerunCalls);
            }).ConfigureAwait(false);

            await RunTest("FlakeRerunCommand_DerivesClassAndBuildsDotnetFilter", () =>
            {
                IReadOnlyList<string> classes = FlakeRerunCommand.DeriveClassNames(new List<string>
                {
                    "Fleet.Core.Tests.SequenceRunnerTests.StepPauseMs_50",
                    "Fleet.Core.Tests.SequenceRunnerTests.Other",
                    "Fleet.Core.Tests.VinReaderTests.ReadAsync(args)"
                });
                AssertEqual(2, classes.Count);

                bool built = FlakeRerunCommand.TryBuild("dotnet test src/Fleet.Core.Tests", classes, out string command);
                AssertTrue(built, "a dotnet test command yields an isolated filter");
                AssertTrue(command.Contains("FullyQualifiedName~SequenceRunnerTests"), "filter names the failing class");
                AssertTrue(command.Contains("FullyQualifiedName~VinReaderTests"), "filter names the second class");

                AssertTrue(!FlakeRerunCommand.TryBuild("dotnet test --filter Existing", classes, out _), "a command with an unquoted existing filter is declined");
                AssertTrue(!FlakeRerunCommand.TryBuild("python3 -m unittest", classes, out _), "a non-dotnet command is declined");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("FlakeRerunCommand_NarrowsOneQuotedFilterInPlace_AndDeclinesUnnarrowableCompounds", () =>
            {
                IReadOnlyList<string> classes = new List<string> { "VinConfirmFlowTests" };
                string wrapped = "bash -c 'set -e; dotnet build Example.sln; dotnet test Example.sln --no-build --filter \"Category!=Integration\" --logger trx'; code=$?; exit $code";

                bool built = FlakeRerunCommand.TryBuild(wrapped, classes, out string command);
                AssertTrue(built, "a wrapped command with one quoted filter is narrowed");
                AssertEqual("bash -c 'set -e; dotnet build Example.sln; dotnet test Example.sln --no-build --filter \"(Category!=Integration)&(FullyQualifiedName~VinConfirmFlowTests)\" --logger trx'; code=$?; exit $code",
                    command, "the existing exclusion is kept and the class filter is added in the same place");

                AssertTrue(!FlakeRerunCommand.TryBuild("bash -c 'dotnet test Example.sln'; exit 0", classes, out _),
                    "a compound command with no filter to narrow is declined, since an appended filter would land on its last step");
                AssertTrue(!FlakeRerunCommand.TryBuild("dotnet test A --filter \"X\"; dotnet test B --filter \"Y\"", classes, out _),
                    "two filters cannot be narrowed exactly");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Gate_ConsumerSuiteFlake_RerunIsTruth_AndKeepsConsumerLabelWhenStillRed", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                string worktree = Path.Combine(Path.GetTempPath(), "armada_flake_consumer_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(worktree);
                try
                {
                    // The consumer suite prints one failing test and exits 1 before its dotnet test step runs;
                    // the dotnet test step is still there for the re-run builder to narrow.
                    string command = "bash -c 'echo \"  Failed Fleet.Core.Tests.SequenceRunnerTests.StepPauseMs_50 [1 ms]\"; "
                        + "echo \"Failed!  - Failed: 1, Passed: 0, Total: 1\"; exit 1; dotnet test Example.sln --filter \"Category!=Integration\"'";
                    Vessel consumer = new Vessel { Id = "vsl_consumer", Name = "ExampleConsumer" };
                    WorkflowProfile profile = new WorkflowProfile { UnitTestCommand = command };

                    FakeTypedDecisionClient passClient = new FakeTypedDecisionClient(FlakeResult(2.0, 0.95, 0.9));
                    FlakeTestableGate passing = new FlakeTestableGate(db.Driver,
                        BuildAdapter(db, passClient, BuildSettings(TypedDecisionModeEnum.Gate)), DefinitionOfDoneResult.Pass());
                    DefinitionOfDoneResult cleared = await passing.EvaluateConsumerTestsForTestAsync(
                        BuildInput().Mission!, consumer, profile, worktree, CancellationToken.None).ConfigureAwait(false);

                    AssertTrue(cleared.Passed, "a passing isolated re-run of the consumer's failing class clears the consumer red");
                    AssertEqual(1, passing.RerunCalls);
                    AssertContains("(Category!=Integration)&(FullyQualifiedName~SequenceRunnerTests)", passing.LastRerunCommand ?? "",
                        "the re-run narrows the consumer's own filter to the failing class");

                    FakeTypedDecisionClient redClient = new FakeTypedDecisionClient(FlakeResult(3.0, 0.99, 0.9));
                    FlakeTestableGate failing = new FlakeTestableGate(db.Driver,
                        BuildAdapter(db, redClient, BuildSettings(TypedDecisionModeEnum.Gate)),
                        DefinitionOfDoneResult.Fail("consumer-tests (ExampleConsumer) (flake re-run)", 1, "still failing", DefinitionOfDoneFailureClassEnum.TestFail));
                    DefinitionOfDoneResult red = await failing.EvaluateConsumerTestsForTestAsync(
                        BuildInput().Mission!, consumer, profile, worktree, CancellationToken.None).ConfigureAwait(false);

                    AssertTrue(!red.Passed, "a consumer red that fails its isolated re-run stays red");
                    AssertEqual("consumer_tests_failed: ExampleConsumer", red.CommandLabel, "a red consumer re-run keeps the consumer failure label");
                }
                finally
                {
                    try { Directory.Delete(worktree, true); } catch { }
                }
            }).ConfigureAwait(false);
        }

        private static DefinitionOfDoneResult FailingTestResult()
        {
            DefinitionOfDoneResult result = DefinitionOfDoneResult.Fail("unit-test", 1, "Expected: Success, Actual: Timeout", DefinitionOfDoneFailureClassEnum.TestFail);
            result.FailedTestNames = new List<string> { "Fleet.Core.Tests.SequenceRunnerTests.StepPauseMs_50" };
            result.FailedTestNamesOverflow = false;
            return result;
        }

        /// <summary>
        /// A gate that stubs the isolated re-run (returning a scripted result) and the cross-branch
        /// history lookup (returning false, no database), so the D15 re-run-is-truth path can be proved
        /// without executing a real test command.
        /// </summary>
        private sealed class FlakeTestableGate : DefinitionOfDoneGate
        {
            private readonly DefinitionOfDoneResult _RerunResult;

            public int RerunCalls { get; private set; }

            public string? LastRerunCommand { get; private set; }

            public FlakeTestableGate(Armada.Core.Database.DatabaseDriver database, TypedFlakeScoreAdapter adapter, DefinitionOfDoneResult rerunResult)
                : base(new DefinitionOfDoneSettings { Enabled = true }, database, new LoggingModule(), null, null, adapter)
            {
                _RerunResult = rerunResult;
            }

            protected override Task<DefinitionOfDoneResult> RunIsolatedRerunAsync(string label, string command, string worktreePath, CancellationToken token)
            {
                RerunCalls++;
                LastRerunCommand = command;
                return Task.FromResult(_RerunResult);
            }

            protected override Task<bool> HasRecentCrossBranchFailureAsync(Mission mission, IReadOnlyList<string> failingTestNames, CancellationToken token)
            {
                return Task.FromResult(false);
            }
        }
    }
}
