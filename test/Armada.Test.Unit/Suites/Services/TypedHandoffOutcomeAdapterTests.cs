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
    /// Table-driven tests for the D20 <c>handoff_outcome</c> adapter. Off proceeds with no call;
    /// unavailable and shadow/below-threshold proceed and record an event; an <c>achieved</c> or
    /// <c>unclear</c> outcome proceeds. At or above threshold a <c>blocked_*</c> or <c>off_premise</c>
    /// outcome HALTS (branch preserved, owner note only for the owner question), and a <c>partial</c>
    /// outcome MAILS the next stage the unmet criteria without halting. No outcome ever approves, lands,
    /// or bypasses the Judge. The caller token reaches the client and a client fault never reaches the
    /// caller.
    /// </summary>
    public class TypedHandoffOutcomeAdapterTests : TestSuite
    {
        public override string Name => "Typed Handoff Outcome Adapter (D20)";

        private const string _Decision = "handoff_outcome";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.85)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static HandoffOutcomeDecisionInput BuildInput()
        {
            return new HandoffOutcomeDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "port a decoder", Persona = "Worker" },
                OutputTail = "The catalogue the port needs is not provisioned in this dock.",
                DiffStat = "0 files, +0/-0",
                AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame.", "A test fails without the fix." },
                Persona = "Worker",
                MarkerPresent = true
            };
        }

        private static TypedDecisionResult OutcomeResult(string outcome, double confidence, double nextStageUseful, params (int idx, double noul)[] gaps)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["outcome"] = new TypedAnswer { Type = "choice", Choice = outcome, Confidence = confidence },
                ["next_stage_useful"] = new TypedAnswer { Type = "noul", Noul = nextStageUseful, Confidence = nextStageUseful }
            };
            foreach ((int idx, double noul) in gaps)
            {
                map["gap_" + idx] = new TypedAnswer { Type = "noul", Noul = noul, Confidence = noul };
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

        private static TypedHandoffOutcomeAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedHandoffOutcomeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsProceed_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("off_premise", 0.99, 0.05));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Proceed, result.Action);
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsProceed_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_Blocked_ReturnsProceed_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("blocked_missing_context", 0.99, 0.05));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Achieved_ReturnsProceed_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // achieved is non-actionable: its confidence is treated as zero, so the rule proceeds
                // and a shadow event records the handoff outcome.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("achieved", 0.99, 0.95));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("BlockedMissingContext_AboveThreshold_Halts_PreservesBranch_NoOwnerNote", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("blocked_missing_context", 0.97, 0.05));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Halt, result.Action);
                AssertEqual("handoff_blocked:blocked_missing_context", result.HaltReason);
                AssertTrue(result.PreserveBranch, "a halt preserves the finished stage's branch");
                AssertTrue(!result.OwnerNote, "a missing-context halt posts no owner note");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("BlockedOwnerQuestion_Halts_WithOwnerNote", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("blocked_owner_question", 0.97, 0.05));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Halt, result.Action);
                AssertEqual("handoff_blocked:blocked_owner_question", result.HaltReason);
                AssertTrue(result.OwnerNote, "an owner-question halt posts an owner-addressed board note");
                AssertTrue(result.PreserveBranch, "a halt preserves the branch");
            }).ConfigureAwait(false);

            await RunTest("OffPremise_AboveThreshold_Halts", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("off_premise", 0.90, 0.05));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Halt, result.Action);
                AssertEqual("handoff_blocked:off_premise", result.HaltReason);
                AssertTrue(!result.OwnerNote, "an off-premise halt posts no owner note");
            }).ConfigureAwait(false);

            await RunTest("Partial_AboveThreshold_MailsUnmetCriteria_DoesNotHalt", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // partial at 0.90 with criterion 2 unmet (gap noul 0.80 >= 0.5 floor); criterion 1 met.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("partial", 0.90, 0.80, (1, 0.10), (2, 0.80)));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.MailPartial, result.Action);
                AssertTrue(!result.PreserveBranch, "a partial Mail does not halt or preserve-and-stop");
                AssertEqual(1, result.UnmetCriterionIndices.Count);
                AssertEqual(2, result.UnmetCriterionIndices.First());
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Blocked_BelowThreshold_ReturnsProceed_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A blocked outcome the model is not confident about (0.50 < 0.85): the rule proceeds.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("blocked_missing_context", 0.50, 0.05));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("NeverBypassesJudge_HaltNeverApprovesOrLands", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Across every actionable outcome the verdict is only ever Proceed, Halt, or MailPartial;
                // none carries an approve/land action, so the Judge is never bypassed.
                foreach (string outcome in new[] { "blocked_missing_context", "blocked_owner_question", "off_premise", "partial", "achieved", "unclear" })
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult(outcome, 0.99, 0.5, (1, 0.9)));
                    TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                    HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(
                        result.Action == HandoffOutcomeAction.Proceed
                        || result.Action == HandoffOutcomeAction.Halt
                        || result.Action == HandoffOutcomeAction.MailPartial,
                        "outcome '" + outcome + "' yields only proceed/halt/mail, never an approve or land");
                }
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsProceed_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                HandoffOutcomeVerdict result = await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(HandoffOutcomeAction.Proceed, result.Action);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("partial", 0.90, 0.80, (2, 0.80)));
                TypedHandoffOutcomeAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), HandoffOutcomeVerdict.Proceed(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);
        }
    }
}
