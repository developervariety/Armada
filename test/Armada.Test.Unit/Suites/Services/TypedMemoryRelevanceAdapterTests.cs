namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the <c>memory_relevance</c> adapter. It asks one Noul per ranked memory leaf and moves to
    /// reference only the leaves the model is confident do not apply; every other outcome (off,
    /// unavailable, shadow, below threshold, a client fault, a missing answer) keeps every leaf read-first.
    /// A sort never removes a leaf.
    /// </summary>
    public class TypedMemoryRelevanceAdapterTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Typed Memory Relevance Adapter";

        private const string _Decision = "memory_relevance";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.90)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static Mission BuildMission()
        {
            return new Mission { Id = "msn_test", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "Port the counter decoder", Persona = "Worker", Description = "Port the frame counter decoder and add a test." };
        }

        private static List<ContextChunk> BuildLeaves()
        {
            return new List<ContextChunk>
            {
                new ContextChunk { Topic = "leaf.decoder", Summary = "Decoder counters are bytes.", ReadWhen = "When the task touches a decoder.", Text = "Decode the counter as one byte." },
                new ContextChunk { Topic = "leaf.deploy", Summary = "How the admiral is deployed.", ReadWhen = "When deploying.", Text = "Rehearse on a restored copy first." },
                new ContextChunk { Topic = "leaf.release", Summary = "Release tagging.", ReadWhen = "When cutting a release.", Text = "Tag from main only." }
            };
        }

        private static TypedDecisionResult AppliesResult(params double[] applies)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            for (int i = 0; i < applies.Length; i++)
                map["applies_" + (i + 1)] = new TypedAnswer { Type = "noul", Noul = applies[i], Confidence = Math.Max(applies[i], 1.0 - applies[i]) };
            return new TypedDecisionResult { Available = true, Answers = map, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedMemoryRelevanceAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedMemoryRelevanceAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_KeepsEveryLeafReadFirst_NoCall", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(AppliesResult(0.99, 0.01, 0.01));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(0, sort.ReferenceTopics.Count, "Off moves no leaf");
                    AssertEqual(0, client.CallCount, "Off sends nothing");
                }
            }).ConfigureAwait(false);

            await RunTest("Gate_ConfidentlyIrrelevantLeaves_MoveToReference_RelevantLeafStays", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(AppliesResult(0.97, 0.03, 0.05));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(2, sort.ReferenceTopics.Count, "the two confidently irrelevant leaves move");
                    AssertTrue(sort.ReferenceTopics.Contains("leaf.deploy"), "the deploy leaf moves to reference");
                    AssertTrue(sort.ReferenceTopics.Contains("leaf.release"), "the release leaf moves to reference");
                    AssertFalse(sort.ReferenceTopics.Contains("leaf.decoder"), "the applying leaf stays read-first");
                    AssertEqual("reference:2", sort.Outcome, "the outcome names the moved count");
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("Gate_OnlyLeavesAtTheThresholdMove_AnUncertainLeafStays", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Leaf 2 is confidently irrelevant (0.04 applies); leaf 3 is only probably irrelevant (0.30).
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(AppliesResult(0.95, 0.04, 0.30));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(1, sort.ReferenceTopics.Count, "only the leaf at or above the threshold moves");
                    AssertTrue(sort.ReferenceTopics.Contains("leaf.deploy"), "the confident leaf moves");
                    AssertFalse(sort.ReferenceTopics.Contains("leaf.release"), "an uncertain leaf stays read-first");
                }
            }).ConfigureAwait(false);

            await RunTest("Gate_EveryLeafApplies_MovesNone_RecordsBelowThreshold", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(AppliesResult(0.95, 0.80, 0.70));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(0, sort.ReferenceTopics.Count, "no leaf moves when every leaf may apply");
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("Shadow_ConfidentlyIrrelevant_MovesNone_RecordsShadow", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(AppliesResult(0.97, 0.02, 0.02));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(0, sort.ReferenceTopics.Count, "shadow moves no leaf");
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                    AssertEqual(1, client.CallCount);
                }
            }).ConfigureAwait(false);

            await RunTest("Unavailable_KeepsEveryLeafReadFirst_RecordsUnavailable", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(0, sort.ReferenceTopics.Count, "unavailable moves no leaf");
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("ClientThrows_KeepsEveryLeafReadFirst_NeverThrows", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(0, sort.ReferenceTopics.Count, "a client fault moves no leaf");
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);

            await RunTest("MissingAnswer_ReadsAsApplies", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Only leaf 2 is answered; leaves 1 and 3 have no answer and must stay read-first.
                    Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["applies_2"] = new TypedAnswer { Type = "noul", Noul = 0.02, Confidence = 0.98 }
                    };
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = true, Answers = map });
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), BuildLeaves(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(1, sort.ReferenceTopics.Count, "only the answered, irrelevant leaf moves");
                    AssertTrue(sort.ReferenceTopics.Contains("leaf.deploy"), "the answered leaf moves");
                }
            }).ConfigureAwait(false);

            await RunTest("AsksOneQuestionPerLeaf_NamedByJsonPath_NoEmptySlots", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(AppliesResult(0.9, 0.9, 0.9));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    using (CancellationTokenSource cts = new CancellationTokenSource())
                    {
                        await adapter.SortAsync(BuildMission(), BuildLeaves(), cts.Token).ConfigureAwait(false);
                        AssertTrue(client.LastToken == cts.Token, "the caller token reaches the client");
                    }

                    AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
                    AssertEqual(3, client.LastRequest.Questions.Count, "one Noul per listed leaf");
                    AssertTrue(client.LastRequest.Questions["applies_1"].Instructions.Contains("`leaves[0]`", StringComparison.Ordinal),
                        "the first question names the 0-based JSON path");
                    AssertFalse(client.LastRequest.Questions.ContainsKey("applies_4"), "no slot beyond the listed leaves is asked");
                }
            }).ConfigureAwait(false);

            await RunTest("NoLeaves_SendsNothing", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(AppliesResult(0.1));
                    TypedMemoryRelevanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                    ContextLeafSort sort = await adapter.SortAsync(BuildMission(), new List<ContextChunk>(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(0, sort.ReferenceTopics.Count, "no leaves, nothing to move");
                    AssertEqual(0, client.CallCount, "no leaves, nothing sent");
                }
            }).ConfigureAwait(false);
        }
    }
}
