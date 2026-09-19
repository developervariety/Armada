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
    /// Tests for the <c>context_compaction</c> adapter. The deterministic compaction replaces the content
    /// of every older tool result, so the rule verdict spares NOTHING and the decision can only ever
    /// retain more of the captain's own history. Off asks nothing; unavailable, shadow and below-threshold
    /// all keep the rule and record an event; at or above the threshold the results the model reads as
    /// still load-bearing are spared and the rest stay compacted. An answer that cannot be read as a
    /// probability spares nothing, so an odd provider reply degrades to the deterministic pass rather
    /// than to a conversation that never shrinks.
    /// </summary>
    public class TypedContextCompactionAdapterTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Typed Context Compaction Adapter";

        private const string _Decision = "context_compaction";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.90)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        // Three candidates: a settled directory listing, a measured count, and a source read.
        private static ContextCompactionDecisionInput BuildInput()
        {
            return new ContextCompactionDecisionInput
            {
                Goal = "Replace the duplicated validation helpers and publish the count you measured.",
                Candidates = new List<ContextCompactionCandidate>
                {
                    new ContextCompactionCandidate("bash", "list the test directory", "test_one.py\ntest_two.py\n", 4096, 40),
                    new ContextCompactionCandidate("bash", "count the helper copies", "11 matches across 7 files\n", 2048, 30),
                    new ContextCompactionCandidate("read", "read the helper source", "def validate(value):\n", 65536, 20)
                }
            };
        }

        private static TypedDecisionResult LoadBearing(params (int slot, double noul)[] answers)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            foreach ((int slot, double noul) in answers)
                map["keep_result_" + slot] = new TypedAnswer { Type = "noul", Noul = noul };
            return new TypedDecisionResult { Available = true, Answers = map, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static TypedContextCompactionAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedContextCompactionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_SparesNothing_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.99), (2, 0.99), (3, 0.99)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertEqual(0, client.CallCount, "an Off decision asks nothing");
                AssertFalse(result.HasSpared, "and spares nothing, so the deterministic pass is unchanged");
            }).ConfigureAwait(false);

            await RunTest("Gated_SparesOnlyTheLoadBearingResults", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The measured count is load-bearing; the directory listing is settled; the file read is
                // recoverable by re-reading the file.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.05), (2, 0.96), (3, 0.20)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertEqual(1, result.SparedPositions.Count, "one result is spared");
                AssertEqual(1, result.SparedPositions[0], "and it is the measured count, at position 1");
                AssertEqual("spared:1", result.OutcomeLabel);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("BelowThreshold_SparesNothing_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Every reading is weak, so the strongest is below the gate and the rule stands whole.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.55), (2, 0.60), (3, 0.51)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertFalse(result.HasSpared, "a weak reading never spares");
                AssertEqual("compact_all", result.OutcomeLabel);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_SparesNothing_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.99), (2, 0.99), (3, 0.99)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertFalse(result.HasSpared, "Shadow records and changes nothing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Unavailable_SparesNothing_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertFalse(result.HasSpared, "an unavailable provider leaves the deterministic pass alone");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("TheRequestAsksTwoQuestionsPerCandidate", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.10)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                await adapter.DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None).ConfigureAwait(false);

                // Three candidates, two Nouls each. A fixed set would let the provider answer a slot with no
                // candidate behind it, and the verdict would then name a position the caller could not resolve.
                AssertEqual(6, client.LastRequest!.Questions.Count, "the request asks keep_call and keep_result for each candidate, and no more");
            }).ConfigureAwait(false);

            await RunTest("AMissingAnswer_SparesNothingForThatCandidate", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Only the second slot is answered. The unanswered ones keep the rule.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((2, 0.97)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertEqual(1, result.SparedPositions.Count, "only the answered candidate is considered");
                AssertEqual(1, result.SparedPositions[0]);
            }).ConfigureAwait(false);

            await RunTest("AConfidenceWithoutANoul_SparesNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A confidence is not the probability the statement is true. Read as one it would spare a
                // settled result, and a conversation that never shrinks loses the run.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    FakeTypedDecisionClient.NoulConfidenceOnly("keep_result_1", 0.99));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertFalse(result.HasSpared, "a confidence-only answer is not a load-bearing reading");
            }).ConfigureAwait(false);

            await RunTest("TheStateCarriesTheGoalAndTheCandidateHeads", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.10)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                await adapter.DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None).ConfigureAwait(false);

                string state = FakeTypedDecisionClient.StateText(client.LastRequest);
                AssertContains("publish the count you measured", state, "the goal reaches the model, or it cannot judge relevance");
                AssertContains("11 matches across 7 files", state, "the head of each candidate reaches the model");
                AssertContains("output_bytes", state, "and the size the pass would reclaim");
            }).ConfigureAwait(false);

            await RunTest("TheStateCarriesFittedHistoryAndDistinctiveLines", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.10)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionDecisionInput input = new ContextCompactionDecisionInput
                {
                    Goal = BuildInput().Goal,
                    HistoryExcerpt = "user: replace the helpers\ntool: FAILED: Assert.Equal expected 4 actual 5",
                    Candidates = new List<ContextCompactionCandidate>
                    {
                        new ContextCompactionCandidate("bash", "run the tests", "Starting test execution\n", 80000, 12,
                            "FAILED: Assert.Equal expected 4 actual 5")
                    }
                };
                await adapter.DecideAsync(input, ContextCompactionVerdict.SpareNone(), CancellationToken.None).ConfigureAwait(false);

                string state = FakeTypedDecisionClient.StateText(client.LastRequest);
                AssertContains("replace the helpers", state, "fitted history reaches the model");
                AssertContains("FAILED: Assert.Equal", state, "the distinctive assertion line reaches the model even when it is not in the head");
            }).ConfigureAwait(false);

            await RunTest("KeepCallAlone_DoesNotSpareTheResult", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                {
                    ["keep_call_1"] = new TypedAnswer { Type = "noul", Noul = 0.99 },
                    ["keep_result_1"] = new TypedAnswer { Type = "noul", Noul = 0.10 },
                    ["keep_call_2"] = new TypedAnswer { Type = "noul", Noul = 0.20 },
                    ["keep_result_2"] = new TypedAnswer { Type = "noul", Noul = 0.96 },
                    ["keep_call_3"] = new TypedAnswer { Type = "noul", Noul = 0.99 },
                    ["keep_result_3"] = new TypedAnswer { Type = "noul", Noul = 0.10 }
                };
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    new TypedDecisionResult { Available = true, Answers = map, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 });
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertEqual(1, result.SparedPositions.Count, "only keep_result at or above the floor spares");
                AssertEqual(1, result.SparedPositions[0], "the high keep_call on the other slots does not spare them");
            }).ConfigureAwait(false);

            await RunTest("TenCandidates_AskTwentyQuestions_AndAWeakTestFailureDoesNotSpare", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                List<ContextCompactionCandidate> candidates = new List<ContextCompactionCandidate>();
                Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
                for (int slot = 1; slot <= 10; slot++)
                {
                    bool failure = slot == 3 || slot == 7;
                    candidates.Add(new ContextCompactionCandidate(
                        "bash",
                        failure ? "run the tests" : "list files",
                        failure ? "Starting test execution, please wait...\n" : "ok\n",
                        65536,
                        40 - slot,
                        failure ? "FAILED: Assert.Equal expected 4 actual 5" : String.Empty));
                    // The live calibration put those two test-failure outputs at 0.41-0.44, below the
                    // spare floor and below the 0.55 gate. The adapter must not spare them; the
                    // deterministic diagnostic rule is what keeps the assertion text.
                    map["keep_result_" + slot] = new TypedAnswer { Type = "noul", Noul = failure ? 0.42 : 0.12 };
                }
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                    new TypedDecisionResult { Available = true, Answers = map, InputTokens = 20, OutputTokens = 10, LatencyMs = 12 });
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate, 0.55));

                ContextCompactionVerdict result = await adapter.DecideAsync(
                    new ContextCompactionDecisionInput { Goal = "fix the failing assertion", Candidates = candidates },
                    ContextCompactionVerdict.SpareNone(),
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(20, client.LastRequest!.Questions.Count, "ten candidates, two questions each");
                AssertFalse(result.HasSpared, "0.42 is below the 0.55 gate, so Jev spares nothing");
            }).ConfigureAwait(false);

            await RunTest("TheCallerToken_ReachesTheClient", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.10)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken.Equals(cts.Token), "the caller's token is forwarded so the timeout links to it");
            }).ConfigureAwait(false);

            await RunTest("AClientFault_NeverReachesTheCaller", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("provider exploded"));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter
                    .DecideAsync(BuildInput(), ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertFalse(result.HasSpared, "a client fault degrades to the deterministic compaction");
            }).ConfigureAwait(false);

            await RunTest("AnExcludedVessel_SendsNothing_KeepsTheRule_AndRecordsWhy", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.99), (2, 0.99), (3, 0.99)));
                TypedDecisionSettings settings = BuildSettings(TypedDecisionModeEnum.Gate);
                settings.EgressExcludedVesselIds = new List<string> { "vsl_banned" };
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, settings);

                ContextCompactionDecisionInput banned = new ContextCompactionDecisionInput
                {
                    Mission = new Mission { VesselId = "vsl_banned" },
                    Goal = BuildInput().Goal,
                    Candidates = BuildInput().Candidates
                };
                ContextCompactionVerdict result = await adapter
                    .DecideAsync(banned, ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);

                AssertEqual(0, client.CallCount, "nothing about a mission on an excluded vessel leaves the host");
                AssertFalse(result.HasSpared, "the rule stands");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false),
                    "the refusal is recorded, never silent");

                // Control: the same call about another vessel is sent and answered.
                ContextCompactionDecisionInput allowed = new ContextCompactionDecisionInput
                {
                    Mission = new Mission { VesselId = "vsl_other" },
                    Goal = BuildInput().Goal,
                    Candidates = BuildInput().Candidates
                };
                ContextCompactionVerdict answered = await adapter
                    .DecideAsync(allowed, ContextCompactionVerdict.SpareNone(), CancellationToken.None)
                    .ConfigureAwait(false);
                AssertEqual(1, client.CallCount, "an unlisted vessel is asked as before");
                AssertTrue(answered.HasSpared, "and its answer is used");
            }).ConfigureAwait(false);

            await RunTest("NoCandidates_AsksNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LoadBearing((1, 0.99)));
                TypedContextCompactionAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ContextCompactionVerdict result = await adapter.DecideAsync(
                    new ContextCompactionDecisionInput { Goal = "nothing to shed", Candidates = new List<ContextCompactionCandidate>() },
                    ContextCompactionVerdict.SpareNone(),
                    CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.HasSpared, "nothing to shed means nothing to spare");
                AssertEqual(0, client.CallCount, "an empty candidate list asks nothing, so the rule stands with no call");
            }).ConfigureAwait(false);
        }
    }
}
