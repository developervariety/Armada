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
    /// Table-driven tests for the D1 <c>failure_cause</c> adapter: Off keeps the rule with no call;
    /// unavailable keeps the rule and records an event; Shadow and below-threshold keep the rule and
    /// record a shadow event; at or above threshold the model may hold a rescue, but a rule hard-block
    /// always wins and a work-defect reading never manufactures a hold; the caller token reaches the
    /// client for timeout linking.
    /// </summary>
    public class TypedFailureCauseAdapterTests : TestSuite
    {
        public override string Name => "Typed Failure Cause Adapter (D1)";

        private const string _Decision = "failure_cause";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.90)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static FailureCauseDecisionInput BuildInput()
        {
            return new FailureCauseDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "port a decoder" },
                FailureReason = "definition-of-done gate failed",
                AgentOutputTail = "some captain output",
                DodClass = "TestFail",
                Persona = "Worker",
                MissionMode = "Implementation",
                RecoveryAttempts = 0,
                Checks = new List<FailureCauseCheckFact>
                {
                    new FailureCauseCheckFact { Label = "unit", Type = "UnitTest", Status = "Failed", CommitMatchesJudge = true, ExitCode = 1, Tail20 = "1 failed" }
                },
                ParentFailingTests = new List<string>()
            };
        }

        private static TypedDecisionResult CauseResult(string cause, double confidence, double repeatLikely = 0.0)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>
                {
                    ["cause"] = new TypedAnswer { Type = "choice", Choice = cause, Confidence = confidence },
                    ["repeat_likely"] = new TypedAnswer { Type = "noul", Noul = repeatLikely },
                    ["foreign_test"] = new TypedAnswer { Type = "noul", Noul = 0.1 }
                },
                InputTokens = 100,
                OutputTokens = 20,
                LatencyMs = 40
            };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedFailureCauseAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedFailureCauseAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsRule_NoCallNoEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("environmental", 0.99));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.DispatchRescue, "Off must keep the rule's rescue");
                AssertEqual(0, client.CallCount, "Off must not call the client");
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "timeout" });
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.DispatchRescue, "unavailable must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ClientThrows_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.DispatchRescue, "a throwing client must never break the caller");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("environmental", 0.99));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.DispatchRescue, "Shadow must keep the rule even at high confidence");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateBelowThreshold_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("environmental", 0.50));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.DispatchRescue, "below threshold must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateAboveThreshold_HoldsRescue_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("environmental", 0.95));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.DispatchRescue, "environmental at >= threshold must hold the rescue");
                AssertContains("typed_decision:environmental", result.Reason);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateAboveThreshold_RepeatLikely_HoldsRescue", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("work_defect", 0.99, repeatLikely: 0.95));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.DispatchRescue, "repeat_likely >= 0.9 must hold the rescue even for a work_defect cause");
                AssertContains("typed_decision:repeat_likely", result.Reason);
            }).ConfigureAwait(false);

            await RunTest("GateAboveThreshold_RuleHardBlockWins", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("environmental", 0.99));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                // The rule already hard-blocked (budget exhausted). The model reads a gate-worthy cause,
                // but Combine must keep the block: the model can only hold a rescue, never make one.
                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Blocked("mission recovery budget is exhausted");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.DispatchRescue, "a rule hard-block must stay blocked");
                AssertContains("budget is exhausted", result.Reason);
            }).ConfigureAwait(false);

            await RunTest("GateAboveThreshold_WorkDefect_KeepsRescue", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("work_defect", 0.99));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict rule = TypedRecoveryVerdict.Rescue("recoverable mission failure");
                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.DispatchRescue, "a work_defect reading must never manufacture a hold");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_ForTimeoutLinking", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("environmental", 0.95));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), TypedRecoveryVerdict.Rescue("x"), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token so the client links its timeout to it");
                AssertNotNull(client.LastRequest);
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
                AssertTrue(client.LastRequest.Questions.ContainsKey("cause"), "questions must include cause");
                AssertTrue(client.LastRequest.Questions.ContainsKey("repeat_likely"), "questions must include repeat_likely");
            }).ConfigureAwait(false);
        }
    }
}
