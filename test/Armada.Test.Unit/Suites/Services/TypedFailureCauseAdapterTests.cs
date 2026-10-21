namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
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
    /// record a rule-kept event; at or above threshold the model may hold a rescue, but a rule hard-block
    /// always wins and a work-defect reading never manufactures a hold; verdict validity, evidence
    /// availability and code defect are separate questions that code combines; the caller token reaches
    /// the client for timeout linking.
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
                    new FailureCauseCheckFact { Label = "unit", Type = "UnitTest", Status = "Failed", CommitMatchesJudge = true, ExitCode = 1, Diagnostics = "1 failed" }
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

        // One answer set for the separate questions, as the model returns them.
        private static TypedDecisionResult SplitResult(string cause, double causeConfidence, double evidenceUnavailable, double codeDefect, double? verdictValid)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>
            {
                ["cause"] = new TypedAnswer { Type = "choice", Choice = cause, Confidence = causeConfidence },
                ["evidence_unavailable"] = new TypedAnswer { Type = "noul", Noul = evidenceUnavailable },
                ["code_defect"] = new TypedAnswer { Type = "noul", Noul = codeDefect },
                ["repeat_likely"] = new TypedAnswer { Type = "noul", Noul = 0.2 },
                ["foreign_test"] = new TypedAnswer { Type = "noul", Noul = 0.05 }
            };
            if (verdictValid.HasValue) answers["verdict_valid"] = new TypedAnswer { Type = "noul", Noul = verdictValid.Value };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 100, OutputTokens = 20, LatencyMs = 40 };
        }

        // A Judge failure that returned a valid NEEDS_REVISION verdict and reported that external evidence was unavailable.
        private static FailureCauseDecisionInput JudgeEvidenceGapInput()
        {
            return new FailureCauseDecisionInput
            {
                Mission = new Mission { Id = "msn_judge", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "review the port" },
                FailureReason = "Judge verdict NEEDS_REVISION",
                AgentOutputTail = "[ARMADA:VERDICT] NEEDS_REVISION\nThe acceptance criteria cite a capture that is not present in the dock, so the parity claim cannot be verified.",
                Persona = "Judge",
                MissionMode = "Review",
                Checks = new List<FailureCauseCheckFact>(),
                ParentFailingTests = new List<string>()
            };
        }

        private sealed class RuleAnswerKeys
        {
            [JsonPropertyName("rule_answers")]
            public Dictionary<string, object?>? RuleAnswers { get; set; }
        }

        private sealed class RuleAnswerPayload
        {
            [JsonPropertyName("rule_answers")]
            public FailureCauseRuleAnswers? RuleAnswers { get; set; }
        }

        private sealed class FailureCauseRuleAnswers
        {
            [JsonPropertyName("cause")]
            public string? Cause { get; set; }

            [JsonPropertyName("code_defect")]
            public bool? CodeDefect { get; set; }

            [JsonPropertyName("repeat_likely")]
            public bool? RepeatLikely { get; set; }

            [JsonPropertyName("verdict_valid")]
            public bool? VerdictValid { get; set; }

            [JsonPropertyName("evidence_unavailable")]
            public bool? EvidenceUnavailable { get; set; }
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
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeRuleKept).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
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
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeRuleKept).ConfigureAwait(false));
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

            await RunTest("ValidJudgeVerdict_WithEvidenceGap_HoldsAsEnvironmental_NotVerdictForm", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The model reads the verdict as well formed and the evidence as missing: the cause is the
                // environment, so the hold names environmental, never verdict_form.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SplitResult("unclear", 0.55, evidenceUnavailable: 0.92, codeDefect: 0.05, verdictValid: 0.96));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict result = await adapter.DecideAsync(JudgeEvidenceGapInput(), TypedRecoveryVerdict.Rescue("recoverable mission failure"), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.DispatchRescue, "unavailable evidence at >= threshold holds the rescue");
                AssertContains("typed_decision:environmental", result.Reason);
                AssertFalse(result.Reason.Contains("verdict_form", StringComparison.Ordinal), "a valid verdict never reads as a verdict-form fault");
                EnumerationResult<ArmadaEvent> gated = await db.Driver.Events
                    .EnumerateAsync(new EnumerationQuery { EventType = TypedDecisionRecorder.EventTypeGated, PageNumber = 1, PageSize = 10 }).ConfigureAwait(false);
                AssertEqual(1, gated.Objects.Count);
                AssertContains("model=environmental", gated.Objects[0].Message ?? String.Empty, "the recorded model verdict is environmental");

                ChoiceQuestion cause = (ChoiceQuestion)client.LastRequest!.Questions["cause"];
                AssertFalse(cause.Criteria.ContainsKey("verdict_form"), "verdict form is not a cause option; verdict validity is its own question");
                AssertTrue(client.LastRequest.Questions.ContainsKey("verdict_valid"), "a Judge failure is asked whether its verdict is valid");
                AssertTrue(client.LastRequest.Questions.ContainsKey("evidence_unavailable"), "evidence availability is its own question");
                AssertTrue(client.LastRequest.Questions.ContainsKey("code_defect"), "code defect is its own question");
            }).ConfigureAwait(false);

            await RunTest("MalformedJudgeVerdict_HoldsAsVerdictForm", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SplitResult("unclear", 0.5, evidenceUnavailable: 0.05, codeDefect: 0.1, verdictValid: 0.04));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict result = await adapter.DecideAsync(JudgeEvidenceGapInput(), TypedRecoveryVerdict.Rescue("recoverable mission failure"), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.DispatchRescue, "a confidently malformed Judge verdict holds the rescue");
                AssertContains("typed_decision:verdict_form", result.Reason);
            }).ConfigureAwait(false);

            await RunTest("CodeDefect_SuppressesEveryHold", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SplitResult("environmental", 0.99, evidenceUnavailable: 0.95, codeDefect: 0.9, verdictValid: 0.02));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict result = await adapter.DecideAsync(JudgeEvidenceGapInput(), TypedRecoveryVerdict.Rescue("recoverable mission failure"), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.DispatchRescue, "a defect a rescue can repair keeps the rule's rescue");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeRuleKept).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("NonJudgeFailure_IsNotAskedVerdictValidity_AndIgnoresItsAbsence", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SplitResult("work_defect", 0.97, evidenceUnavailable: 0.1, codeDefect: 0.3, verdictValid: null));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                TypedRecoveryVerdict result = await adapter.DecideAsync(BuildInput(), TypedRecoveryVerdict.Rescue("recoverable mission failure"), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(client.LastRequest!.Questions.ContainsKey("verdict_valid"), "only a Judge writes a verdict");
                AssertTrue(result.DispatchRescue, "an unasked verdict question never reads as a malformed verdict");
            }).ConfigureAwait(false);

            await RunTest("RuleAnswers_AreRecordedPerQuestion", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SplitResult("work_defect", 0.5, evidenceUnavailable: 0.1, codeDefect: 0.2, verdictValid: 0.9));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                await adapter.DecideAsync(JudgeEvidenceGapInput(), TypedRecoveryVerdict.Rescue("recoverable mission failure"), CancellationToken.None).ConfigureAwait(false);

                EnumerationResult<ArmadaEvent> kept = await db.Driver.Events
                    .EnumerateAsync(new EnumerationQuery { EventType = TypedDecisionRecorder.EventTypeRuleKept, PageNumber = 1, PageSize = 10 }).ConfigureAwait(false);
                AssertEqual(1, kept.Objects.Count, "the below-threshold call is recorded");
                RuleAnswerKeys keys = JsonSerializer.Deserialize<RuleAnswerKeys>(kept.Objects[0].Payload!)!;
                AssertNotNull(keys.RuleAnswers, "the rule's answers are recorded");
                AssertEqual(
                    String.Join(",", client.LastRequest!.Questions.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                    String.Join(",", keys.RuleAnswers!.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                    "one rule answer per question asked");
                FailureCauseRuleAnswers answers = JsonSerializer.Deserialize<RuleAnswerPayload>(kept.Objects[0].Payload!)!.RuleAnswers!;
                AssertEqual("work_defect", answers.Cause, "a rule rescue answers work_defect");
                AssertEqual(true, answers.CodeDefect, "a rule rescue answers code_defect true");
                AssertEqual(false, answers.RepeatLikely, "a rule rescue answers repeat_likely false");
                AssertNull(answers.VerdictValid, "a question the rule cannot answer records null");
                AssertNull(answers.EvidenceUnavailable, "a question the rule cannot answer records null");
            }).ConfigureAwait(false);

            await RunTest("OutputEvidence_FailingCheck_KeepsHeadAndTail_WithinBudget", () =>
            {
                string output = "FATAL: no space left on device during setup\n" + new string('x', 20000) + "\nassertion failed: expected 3, got 4";
                string evidence = TypedFailureCauseAdapter.OutputEvidence(output, failed: true, maxChars: 6000);

                AssertTrue(evidence.Length <= 6060, "failing evidence stays within its budget plus the elision marker");
                AssertTrue(evidence.Contains("no space left on device", StringComparison.Ordinal), "the infra fault at the head survives");
                AssertTrue(evidence.Contains("assertion failed: expected 3, got 4", StringComparison.Ordinal), "the final error at the tail survives");
            }).ConfigureAwait(false);

            await RunTest("OutputEvidence_PassingCheck_KeepsShortTailOnly", () =>
            {
                string output = "HEAD-MARKER\n" + new string('y', 7000) + "\nOK done";
                string evidence = TypedFailureCauseAdapter.OutputEvidence(output, failed: false, maxChars: 6000);

                AssertTrue(evidence.Length <= 800, "a passing check keeps only a short tail");
                AssertTrue(evidence.Contains("OK done", StringComparison.Ordinal), "the tail is kept");
                AssertTrue(!evidence.Contains("HEAD-MARKER", StringComparison.Ordinal), "a passing check's head is not the evidence");
            }).ConfigureAwait(false);

            await RunTest("State_CarriesFailingCheckEvidence_WithinBudget", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CauseResult("environmental", 0.95));
                TypedFailureCauseAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                List<FailureCauseCheckFact> checks = new List<FailureCauseCheckFact>
                {
                    new FailureCauseCheckFact { Label = "build", Type = "Build", Status = "Failed", ExitCode = 1, Diagnostics = "INFRA-DISK-FULL at setup\n" + new string('d', 4000) }
                };
                // A full page of passing checks (the production read pages at 50): with each kept to a
                // short tail, the failing check's fuller evidence still fits the state budget.
                for (int i = 0; i < 50; i++)
                    checks.Add(new FailureCauseCheckFact { Label = "t" + i, Type = "UnitTest", Status = "Passed", Diagnostics = new string('p', 400) });

                FailureCauseDecisionInput input = new FailureCauseDecisionInput
                {
                    Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "port a decoder" },
                    FailureReason = "Build Check failed.",
                    Checks = checks,
                    ParentFailingTests = new List<string>()
                };

                TypedDecisionBatchItem? item = adapter.DescribeRequest(input);
                AssertNotNull(item);
                string state = item!.State.Text;
                AssertTrue(state.Length <= new TypedDecisionSettings().MaxStateChars, "the state stays within the budget");
                AssertTrue(state.Contains("INFRA-DISK-FULL", StringComparison.Ordinal), "the failing check's evidence survives budget truncation");
            }).ConfigureAwait(false);
        }
    }
}
