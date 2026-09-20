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
    /// Table-driven tests for the D21 <c>revision_kind</c> adapter. Off proceeds with no call;
    /// unavailable and shadow/below-threshold proceed and record an event. At or above threshold, an
    /// all-non-behavioural reading with no behavioural or test item BLOCKS the rescue for an operator
    /// landing (reason <c>revision_comment_only</c>); a single behavioural or test item forces the rule
    /// to stand (the rescue proceeds); and a <c>RescueRequired</c> rule hard-block is never overturned.
    /// The verdict only ever holds a rescue — it never lands. The caller token reaches the client and a
    /// client fault never reaches the caller.
    /// </summary>
    public class TypedRevisionKindAdapterTests : TestSuite
    {
        public override string Name => "Typed Revision Kind Adapter (D21)";

        private const string _Decision = "revision_kind";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.85)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static RevisionKindDecisionInput BuildInput()
        {
            return new RevisionKindDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "port a decoder", Persona = "Judge" },
                RevisionItems = new List<string> { "Reword the doc comment to name the rule.", "Fix a typo in the summary." },
                Symptom = "The decoder truncates the counter."
            };
        }

        private static TypedDecisionResult RevisionResult(double confidence, params (int idx, string kind)[] items)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            foreach ((int idx, string kind) in items)
            {
                map["item_" + idx] = new TypedAnswer { Type = "choice", Choice = kind, Confidence = confidence };
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

        private static TypedRevisionKindAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedRevisionKindAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsProceed_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.99, (1, "comment_only"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsProceed_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_AllNonBehavioural_ReturnsProceed_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.99, (1, "comment_only"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Gate_AllNonBehavioural_AboveThreshold_BlocksRescue_ForOperatorLanding", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.97, (1, "comment_only"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.BlockRescue, result.Action);
                AssertEqual(RevisionKindVerdict.RevisionCommentOnlyReason, result.Reason);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_ABehaviouralItem_ReturnsProceed_RecordsShadow_NeverBlocks", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A high-confidence wording Choice on one item and a behavioural Choice on another:
                // the reading forces zero confidence, so the gate does not fire and the rescue proceeds.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.99, (1, "behaviour"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_ATestItem_ReturnsProceed_NeverBlocks", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.99, (1, "comment_only"), (2, "test")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
            }).ConfigureAwait(false);

            await RunTest("Gate_BelowThreshold_ReturnsProceed_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.50, (1, "comment_only"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("RuleHardBlock_RescueRequired_IsNeverOverturned", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The deterministic rule already requires a rescue. Even a confident all-non-behavioural
                // reading may not turn it into a block: the rule hard-block wins.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.99, (1, "comment_only"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.RescueRequired(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
                AssertTrue(result.RuleRequiresRescue, "the rule hard-block is preserved");
            }).ConfigureAwait(false);

            await RunTest("HoldsRescueNeverLands_OnlyProceedOrBlock", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Across every kind mix the verdict is only ever Proceed or BlockRescue; neither lands or
                // dispatches. A block only holds the rescue for an operator landing.
                foreach ((int idx, string kind) item in new[] { (1, "comment_only"), (1, "doc_only"), (1, "boundary"), (1, "behaviour"), (1, "test") })
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.99, item));
                    TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                    RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(
                        result.Action == RevisionKindAction.Proceed || result.Action == RevisionKindAction.BlockRescue,
                        "kind '" + item.kind + "' yields only proceed or block, never a land or dispatch");
                }
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsProceed_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RevisionKindVerdict result = await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RevisionKindAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.97, (1, "comment_only"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);

            await RunTest("AsksOnlyAboutListedItems_NoSummaryNoul_NoEmptySlots", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(RevisionResult(0.97, (1, "comment_only"), (2, "doc_only")));
                TypedRevisionKindAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                await adapter.DecideAsync(BuildInput(), RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, client.LastRequest!.Questions.Count, "two listed items, one Choice each");
                AssertTrue(client.LastRequest.Questions.ContainsKey("item_2"), "the last listed item is asked");
                AssertFalse(client.LastRequest.Questions.ContainsKey("item_3"), "no slot beyond the listed items is asked");
                AssertFalse(client.LastRequest.Questions.ContainsKey("all_non_behavioural"), "the AND of the item kinds is computed in code");
                AssertTrue(client.LastRequest.Questions["item_1"].Instructions.Contains("`revision_items[0]`", StringComparison.Ordinal),
                    "the first question names the 0-based JSON path");
            }).ConfigureAwait(false);
        }
    }
}
