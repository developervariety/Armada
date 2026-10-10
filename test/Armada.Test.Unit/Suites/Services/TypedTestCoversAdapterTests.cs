namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
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
    /// Table-driven tests for the D22 <c>test_covers</c> adapter. Off adds no instruction with no call;
    /// unavailable and shadow/below-threshold add none and record an event. At or above threshold, an
    /// added test the model doubts (it would not fail before the fix, does not cover the symptom, or
    /// only asserts source text) becomes a Judge INSTRUCTION; a well-covered test adds none. The verdict
    /// NEVER fails the stage — it only ever carries instructions. The caller token reaches the client and
    /// a client fault never reaches the caller.
    /// </summary>
    public class TypedTestCoversAdapterTests : TestSuite
    {
        public override string Name => "Typed Test Covers Adapter (D22)";

        private const string _Decision = "test_covers";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.85)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static TestCoversDecisionInput BuildInput()
        {
            return new TestCoversDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "port a decoder", Persona = "Test Engineer" },
                AddedTests = new List<TestCoversMethod>
                {
                    new TestCoversMethod("Decode_TruncatesCounter_ReturnsByte", "AssertEqual(0x08, decoder.Decode(frame));"),
                    new TestCoversMethod("Decode_LongFrame_Handled", "AssertTrue(decoder.Decode(frame) != null);")
                },
                Symptom = "The decoder truncates the counter."
            };
        }

        private static TypedDecisionResult TestResult(params (int idx, double covers, double asserts, double wouldFail)[] tests)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            foreach ((int idx, double covers, double asserts, double wouldFail) in tests)
            {
                map["covers_symptom_" + idx] = new TypedAnswer { Type = "noul", Noul = covers, Confidence = covers };
                map["asserts_source_text_" + idx] = new TypedAnswer { Type = "noul", Noul = asserts, Confidence = asserts };
                map["would_fail_before_fix_" + idx] = new TypedAnswer { Type = "noul", Noul = wouldFail, Confidence = wouldFail };
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

        private static TypedTestCoversAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedTestCoversAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("ExtractAddedTestMethods_ExcludesUnannotatedHelpers", () =>
            {
                const string diff = "@@ -1,2 +1,31 @@\n"
                    + " [Fact]\n"
                    + " public void ExistingTest_IsContextOnly() { }\n"
                    + "+[TestCategory(\"helpers\")]\n"
                    + "+private async Task ArrangeAsync() { await Task.Yield(); }\n"
                    + "+private void Helper01() { }\n"
                    + "+private void Helper02() { }\n"
                    + "+private void Helper03() { }\n"
                    + "+private void Helper04() { }\n"
                    + "+private void Helper05() { }\n"
                    + "+private void Helper06() { }\n"
                    + "+private void Helper07() { }\n"
                    + "+private void Helper08() { }\n"
                    + "+private void Helper09() { }\n"
                    + "+private void Helper10() { }\n"
                    + "+private void Helper11() { }\n"
                    + "+private void Helper12() { }\n"
                    + "+[Xunit.Fact] public void Decode_TruncatesCounter_ReturnsByte()\n"
                    + "+{\n"
                    + "+    Assert.Equal(8, Decode(frame));\n"
                    + "+}\n"
                    + "+[Theory]\n"
                    + "+public void Decode_HandlesBoundary(byte value)\n"
                    + "+{\n"
                    + "+    Assert.Equal(value, Decode(value));\n"
                    + "+}\n"
                    + "+[Theory, InlineData(5, 6)]\n"
                    + "+public void Decode_HandlesCombinedAttributes(byte value) { }\n"
                    + "+await RunTest(\"Registration_Name_IsTheEvidenceLabel\", async () =>\n"
                    + "+{\n"
                    + "+    Decode(frame);\n"
                    + "+});";

                MethodInfo extractor = typeof(MissionService).GetMethod(
                    "ExtractAddedTestMethods", BindingFlags.NonPublic | BindingFlags.Static)!;
                IReadOnlyList<TestCoversMethod> methods = (IReadOnlyList<TestCoversMethod>)extractor.Invoke(null, new object[] { diff })!;

                AssertEqual(4, methods.Count, "only attribute-marked methods and named Armada runner registrations are passed to test_covers");
                AssertEqual("Decode_TruncatesCounter_ReturnsByte", methods[0].Name);
                AssertEqual("Decode_HandlesBoundary", methods[1].Name);
                AssertEqual("Decode_HandlesCombinedAttributes", methods[2].Name);
                AssertEqual("Registration_Name_IsTheEvidenceLabel", methods[3].Name);
            });

            await RunTest("Off_ReturnsNoInstructions_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.0, 0.0, 0.0)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasInstructions, "Off adds no instruction");
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsNoInstructions_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasInstructions, "unavailable adds no instruction");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_Doubted_ReturnsNoInstructions_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.2, 0.1, 0.05)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasInstructions, "shadow adds no instruction");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Gate_DoubtedTest_AboveThreshold_AddsJudgeInstruction", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Test 1 would NOT fail before the fix (0.05): strong concern (1-0.05 = 0.95). Test 2 is fine.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.9, 0.05, 0.05), (2, 0.95, 0.05, 0.95)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.HasInstructions, "a doubted test yields a Judge instruction");
                AssertEqual(1, result.JudgeInstructions.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_WellCoveredTests_AddsNoInstruction_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Both tests cover the symptom, do not assert source text, and would fail before the fix:
                // no concern, so the confidence is zero and the rule stands.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.95, 0.05, 0.95), (2, 0.9, 0.1, 0.9)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasInstructions, "well-covered tests add no instruction");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_AssertsSourceText_AboveThreshold_AddsInstruction", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A test that only asserts source text (0.95) is a concern even if it would fail before the fix.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.9, 0.95, 0.9)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.HasInstructions, "a source-text-only test yields an instruction");
            }).ConfigureAwait(false);

            await RunTest("NeverFailsStage_OnlyEverInstructions", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Whatever the reading, the verdict only ever carries instructions; there is no fail or
                // block action on the verdict, so the stage is never failed by this decision.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.0, 0.99, 0.0)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.HasInstructions, "the strongest concern yields instructions, not a stage failure");
                AssertTrue(result.JudgeInstructions.Count >= 1, "instructions are advisory content only");
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsNoInstructions_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TestCoversVerdict result = await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasInstructions, "a client fault leaves the rule standing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.9, 0.05, 0.05)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);

            await RunTest("AsksOnlyAboutListedTests_NoEmptySlots", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TestResult((1, 0.9, 0.05, 0.9), (2, 0.9, 0.05, 0.9)));
                TypedTestCoversAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                await adapter.DecideAsync(BuildInput(), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(8, client.LastRequest!.Questions.Count, "two listed tests ask four Nouls each, no empty slots");
                AssertTrue(client.LastRequest.Questions.ContainsKey("covers_symptom_2"), "the last listed test is asked");
                AssertFalse(client.LastRequest.Questions.ContainsKey("covers_symptom_3"), "no slot beyond the listed tests is asked");
                AssertTrue(client.LastRequest.Questions["covers_symptom_1"].Instructions.Contains("`added_tests[0]`", StringComparison.Ordinal),
                    "the first question names the 0-based JSON path");
                AssertTrue(client.LastRequest.Questions.ContainsKey("fails_without_change_1"), "fail-before is a separate Noul");
                AssertTrue(client.LastRequest.Questions.ContainsKey("passes_with_change_1"), "pass-after is a separate Noul");
                AssertFalse(client.LastRequest.Questions.ContainsKey("would_fail_before_fix_1"), "the compound fail-and-pass Noul is not sent");
            }).ConfigureAwait(false);
        }
    }
}
