namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    /// Table-driven tests for the D4 <c>review_substance</c> adapter. Off keeps the rule with no call;
    /// unavailable and shadow/below-threshold keep the rule and record an event; at or above threshold
    /// the model may HOLD a thin PASS the rule accepted (never failing it) or ACCEPT a heading-form-only
    /// rejection into the Check gate, but never overturns a rejection on a real ground (empty output or
    /// a too-short narrative) and never fails a PASS the rule accepted. The caller token reaches the
    /// client and a client fault never reaches the caller.
    /// </summary>
    public class TypedReviewSubstanceAdapterTests : TestSuite
    {
        public override string Name => "Typed Review Substance Adapter (D4)";

        private const string _Decision = "review_substance";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.85)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static ReviewSubstanceDecisionInput BuildInput()
        {
            return new ReviewSubstanceDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Title = "review a token port" },
                Narrative = "The review covers completeness, correctness, tests, and failure modes with specifics.",
                RequiredSections = new List<string> { "Completeness", "Correctness", "Tests", "Failure Modes" },
                DiffStat = "3 files, +40/-8",
                CheckSummary = "Build:Build:Passed; UnitTest:UnitTest:Passed"
            };
        }

        // section nouls (four), substantiated score index, score confidence.
        private static TypedDecisionResult SubstanceResult(double[] sectionNouls, double score, double scoreConfidence, Dictionary<string, double>? levelProbabilities = null)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            for (int i = 0; i < sectionNouls.Length; i++)
            {
                answers["section_" + (i + 1)] = new TypedAnswer { Type = "noul", Noul = sectionNouls[i], Confidence = sectionNouls[i] };
            }
            answers["substantiated"] = new TypedAnswer { Type = "score", Score = score, Confidence = scoreConfidence, Probabilities = levelProbabilities };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static double[] AllStrong() => new double[] { 0.95, 0.95, 0.95, 0.95 };

        private static ReviewSubstanceVerdict ValidRule() => ReviewSubstanceVerdict.Rule(true, ReviewSubstanceRuleCategory.Valid, null);
        private static ReviewSubstanceVerdict MissingSectionsRule() => ReviewSubstanceVerdict.Rule(false, ReviewSubstanceRuleCategory.MissingSections, "Judge PASS verdict missing required review sections: Tests");
        private static ReviewSubstanceVerdict ShortNarrativeRule() => ReviewSubstanceVerdict.Rule(false, ReviewSubstanceRuleCategory.ShortNarrative, "Judge PASS verdict review is too short to justify approval");
        private static ReviewSubstanceVerdict EmptyRule() => ReviewSubstanceVerdict.Rule(false, ReviewSubstanceRuleCategory.EmptyOutput, "Judge PASS verdict missing review output");

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedReviewSubstanceAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedReviewSubstanceAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsRule_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 3.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), MissingSectionsRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Validated, "Off must keep the rule (still rejected)");
                AssertEqual("rule", result.Outcome);
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "http_429" });
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), MissingSectionsRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Validated, "unavailable must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 3.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), MissingSectionsRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Validated, "Shadow must keep the rule even when the model would accept");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("GateBelowThreshold_OneWeakSection_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Score at "evidenced" but one section below threshold: the accept confidence is the
                // weakest section, so the gate does not fire and the rule stands.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(new double[] { 0.95, 0.60, 0.95, 0.95 }, 2.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), MissingSectionsRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Validated, "one weak section keeps the rule (still rejected)");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("HeadingFormOnly_Accept_AboveThreshold_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 2.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), MissingSectionsRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Validated, "a heading-form-only rejection with present substance is accepted");
                AssertEqual("heading_form_only", result.Outcome);
                AssertTrue(!result.Held, "an accepted heading-form-only PASS is not held");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("HeadingFormOnly_ThinScore_NotAccepted_RuleStands", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Headings present in substance but the whole review is only "partly evidenced" (Score 1):
                // Score >= 2 is required to accept, so the rejection stands.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 1.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), MissingSectionsRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Validated, "a thin (Score<=1) heading-form-only PASS is not accepted");
            }).ConfigureAwait(false);

            await RunTest("WeakPass_Hold_RecordsGated_NotFailed", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Rule validated the PASS (headings present), but substance is asserted only (Score 0).
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(new double[] { 0.30, 0.20, 0.10, 0.20 }, 0.0, 0.97));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ValidRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Validated, "a held PASS stays validated (the model never auto-fails a rule-accepted PASS)");
                AssertTrue(result.Held, "a weak PASS is held for operator review");
                AssertEqual("held_weak_pass", result.Outcome);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("WeakPass_LowScoreConfidence_NotHeld_RuleStands", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Score is thin (0) but the model's confidence in that score is below threshold: the
                // hold does not fire and the rule stands unchanged.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(new double[] { 0.30, 0.20, 0.10, 0.20 }, 0.0, 0.50));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ValidRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Validated && !result.Held, "a low-confidence thin score does not hold the PASS");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ThinPass_SplitScoreJustAboveLimit_HeldOnThinProbability", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The provider's reading of a headings-only review: the expected score lands just above
                // partly-evidenced and the score's own confidence is zero, because the mass is split
                // between the two lowest levels. Ninety percent of the mass says thin, so it is held.
                TypedDecisionResult split = SubstanceResult(new double[] { 0.20, 0.20, 0.20, 0.20 }, 1.06, 0.0,
                    new Dictionary<string, double> { ["0"] = 0.45, ["1"] = 0.45, ["2"] = 0.08, ["3"] = 0.02 });
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(split);
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ValidRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Validated && result.Held, "a review the model reads as thin with 0.90 probability is held");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("SplitPass_MostlyEvidenced_NotHeld_RuleStands", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionResult split = SubstanceResult(new double[] { 0.70, 0.70, 0.70, 0.70 }, 1.70, 0.4,
                    new Dictionary<string, double> { ["0"] = 0.10, ["1"] = 0.20, ["2"] = 0.60, ["3"] = 0.10 });
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(split);
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ValidRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Validated && !result.Held, "a review mostly read as evidenced is not held");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("StrongPass_RuleStands_NotHeld", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 3.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ValidRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Validated && !result.Held, "a strong validated PASS is left unchanged");
            }).ConfigureAwait(false);

            await RunTest("ShortNarrative_RealGround_NotOverturned", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Even with strong section nouls and a top score, a too-short narrative is a real ground:
                // the model never flips it. (Reaching Combine still records a gated event; the outcome is
                // unchanged.)
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 3.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ShortNarrativeRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Validated, "a too-short-narrative rejection is a real ground, never overturned");
                AssertEqual("rule", result.Outcome);
            }).ConfigureAwait(false);

            await RunTest("EmptyOutput_RealGround_NotOverturned", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 3.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), EmptyRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Validated, "an empty-output rejection is a real ground, never overturned");
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ReviewSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ValidRule(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Validated && !result.Held, "a client fault never reaches the caller; the rule stands");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult(AllStrong(), 2.0, 0.99));
                TypedReviewSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), MissingSectionsRule(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);
        }
    }
}
