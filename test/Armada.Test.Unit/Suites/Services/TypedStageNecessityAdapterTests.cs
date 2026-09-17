namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
    /// Table-driven tests for the D19 <c>stage_necessity</c> adapter. Off keeps every stage with no
    /// call; unavailable and shadow/below-threshold keep every stage and record an event; at or above
    /// the decision threshold a non-Judge stage is proposed OPTIONAL, and only at or above 0.95 is it
    /// proposed for AUTO-skip. The Judge is never a skip candidate and never marked, even when the model
    /// would skip everything. The caller token reaches the client and a client fault never reaches the
    /// caller.
    /// </summary>
    public class TypedStageNecessityAdapterTests : TestSuite
    {
        public override string Name => "Typed Stage Necessity Adapter (D19)";

        private const string _Decision = "stage_necessity";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.80)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        // A Worker -> TestEngineer -> Judge pipeline. Candidates (asked) are Worker (slot 1) and
        // TestEngineer (slot 2); the Judge is never a candidate.
        private static StageNecessityDecisionInput BuildInput()
        {
            return new StageNecessityDecisionInput
            {
                Title = "Port a token decoder",
                Description = "One-file protocol port; no UI, no new tests in scope.",
                AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." },
                Kind = "Chore",
                Stages = new List<StageNecessityStageResult>
                {
                    TypedStageNecessityAdapter.Stage(1, "Worker"),
                    TypedStageNecessityAdapter.Stage(2, "TestEngineer"),
                    TypedStageNecessityAdapter.Stage(3, "Judge")
                }
            };
        }

        // Build a result from adds_value nouls keyed by 1-based candidate slot. skipConfidence = 1 - addsValue.
        private static TypedDecisionResult AddsValueResult(params (int slot, double addsValue, string reason)[] answers)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            foreach ((int slot, double addsValue, string reason) in answers)
            {
                map["stage_" + slot + "_adds_value"] = new TypedAnswer { Type = "noul", Noul = addsValue, Confidence = addsValue };
                map["stage_" + slot + "_skip_reason"] = new TypedAnswer { Type = "choice", Choice = reason, Confidence = 1.0 - addsValue };
            }
            return new TypedDecisionResult { Available = true, Answers = map, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedStageNecessityAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedStageNecessityAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        private static StageNecessityStageResult? StageOf(StageNecessityVerdict verdict, string persona)
        {
            return verdict.Stages.FirstOrDefault(stage => String.Equals(stage.PersonaName, persona, StringComparison.Ordinal));
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsRule_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AddsValueResult((1, 0.03, "trivial_change"), (2, 0.03, "no_tests_in_scope")));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual("rule", result.Outcome);
                AssertEqual(0, result.OptionalStages.Count);
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual("rule", result.Outcome);
                AssertEqual(0, result.OptionalStages.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AddsValueResult((1, 0.03, "trivial_change"), (2, 0.03, "no_tests_in_scope")));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual("rule", result.Outcome);
                AssertEqual(0, result.OptionalStages.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("GateBelowThreshold_AllStagesAddValue_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Both stages add value strongly (skipConfidence 0.10), below the 0.80 threshold.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AddsValueResult((1, 0.90, "none"), (2, 0.90, "none")));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, result.OptionalStages.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("AboveThreshold_BelowAutoSkip_MarkedOptional_NotAutoSkip", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Worker skipConfidence 0.85 (>= 0.80 threshold, < 0.95): optional, not auto-skip.
                // TestEngineer skipConfidence 0.10: retained.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AddsValueResult((1, 0.15, "trivial_change"), (2, 0.90, "none")));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual("gated", result.Outcome);
                StageNecessityStageResult? worker = StageOf(result, "Worker");
                StageNecessityStageResult? testEngineer = StageOf(result, "TestEngineer");
                AssertTrue(worker != null && worker.ModelOptional, "Worker at 0.85 skip confidence is optional");
                AssertTrue(worker != null && !worker.ModelAutoSkip, "Worker below 0.95 is NOT auto-skip");
                AssertEqual("trivial_change", worker!.SkipReason);
                AssertTrue(testEngineer != null && !testEngineer.ModelOptional, "TestEngineer that adds value is retained");
                AssertEqual(1, result.OptionalStages.Count);
                AssertEqual(0, result.AutoSkipStages.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("AtOrAbove095_MarkedAutoSkip", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Worker skipConfidence 0.97 (>= 0.95): auto-skip, which also implies optional.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AddsValueResult((1, 0.03, "trivial_change"), (2, 0.90, "none")));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                StageNecessityStageResult? worker = StageOf(result, "Worker");
                AssertTrue(worker != null && worker.ModelAutoSkip, "Worker at 0.97 skip confidence is auto-skip");
                AssertTrue(worker != null && worker.ModelOptional, "an auto-skip stage is also optional");
                AssertEqual(1, result.AutoSkipStages.Count);
            }).ConfigureAwait(false);

            await RunTest("Judge_NeverSkipped_EvenWhenEverythingSkippable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Both non-Judge candidates auto-skippable at 0.99. The Judge is never a candidate and
                // must stay retained: not optional, not auto-skip.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AddsValueResult((1, 0.01, "trivial_change"), (2, 0.01, "no_tests_in_scope")));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                StageNecessityStageResult? judge = StageOf(result, "Judge");
                AssertTrue(judge != null, "the Judge stage is present in the verdict");
                AssertTrue(!judge!.ModelOptional, "the Judge is never proposed optional");
                AssertTrue(!judge.ModelAutoSkip, "the Judge is never proposed for auto-skip");
                AssertTrue(result.OptionalStages.All(stage => !stage.IsJudge), "no optional stage is the Judge");
                AssertTrue(result.AutoSkipStages.All(stage => !stage.IsJudge), "no auto-skip stage is the Judge");
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                StageNecessityVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual("rule", result.Outcome);
                AssertEqual(0, result.OptionalStages.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AddsValueResult((1, 0.15, "trivial_change"), (2, 0.90, "none")));
                TypedStageNecessityAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                StageNecessityVerdict rule = StageNecessityVerdict.Rule(BuildInput().Stages);
                await adapter.DecideAsync(BuildInput(), rule, cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);
        }
    }
}
