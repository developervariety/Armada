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
    /// Table-driven tests for the D17 <c>change_substance</c> adapter. Off keeps the rule with no call;
    /// unavailable and shadow/below-threshold keep the rule and record an event; at or above threshold
    /// the model may RAISE a documentation-only (or empty) extension read to Substantive when it reads
    /// the added hunks as behaviour, and may add a risky escalation reason — but it NEVER lowers a
    /// Substantive reading. The caller token reaches the client, and a client fault never reaches the
    /// caller.
    /// </summary>
    public class TypedChangeSubstanceAdapterTests : TestSuite
    {
        public override string Name => "Typed Change Substance Adapter (D17)";

        private const string _Decision = "change_substance";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.80)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static ChangeSubstanceDecisionInput BuildInput()
        {
            return new ChangeSubstanceDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Title = "rescue a decoder fix" },
                ChangedPaths = new List<string> { "docs/notes.md" },
                UnifiedDiff = "+++ b/docs/notes.md\n@@\n+some prose\n+++ b/src/Decoder.cs\n@@\n+if (guard) Disable();",
                VesselPublicName = "ExampleVessel"
            };
        }

        // substance choice + confidence, risky noul.
        private static TypedDecisionResult SubstanceResult(string substance, double confidence, double risky)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["substance"] = new TypedAnswer { Type = "choice", Choice = substance, Confidence = confidence },
                ["risky"] = new TypedAnswer { Type = "noul", Noul = risky, Confidence = risky }
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

        private static TypedChangeSubstanceAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedChangeSubstanceAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsRule_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.99, 0.99));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.DocumentationOnly, result.Substance);
                AssertEqual("rule", result.Outcome);
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "http_422" });
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.DocumentationOnly, result.Substance);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.99, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.DocumentationOnly, result.Substance);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("BelowThreshold_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.50, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.DocumentationOnly, result.Substance);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("RaisesDocsToBehaviour_AboveThreshold_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.95, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.Substantive, result.Substance);
                AssertEqual("raised_behaviour", result.Outcome);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("State_LabelsEachHunkWithItsOwnQuotedFile_AndKeepsPlusPlusLines", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.95, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceDecisionInput input = new ChangeSubstanceDecisionInput
                {
                    Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Title = "rescue a decoder fix" },
                    ChangedPaths = new List<string> { "docs/notes.md", "src/Café.cs" },
                    UnifiedDiff = string.Join("\n", new[]
                    {
                        "diff --git a/docs/notes.md b/docs/notes.md",
                        "index 1111111..2222222 100644",
                        "--- a/docs/notes.md",
                        "+++ b/docs/notes.md",
                        "@@ -1 +1,2 @@",
                        " prose",
                        "+more prose",
                        "diff --git \"a/src/Caf\\303\\251.cs\" \"b/src/Caf\\303\\251.cs\"",
                        "index 7898192..422c2b7 100644",
                        "--- \"a/src/Caf\\303\\251.cs\"",
                        "+++ \"b/src/Caf\\303\\251.cs\"",
                        "@@ -1 +1,3 @@",
                        " a",
                        "+b",
                        "+++counter;",
                        ""
                    }),
                    VesselPublicName = "ExampleVessel"
                };

                await adapter.DecideAsync(input, ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.Substantive), CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(client.LastRequest, "the model was asked");
                System.Text.Json.Nodes.JsonObject state = (System.Text.Json.Nodes.JsonObject)client.LastRequest!.State;
                System.Text.Json.Nodes.JsonArray hunks = state["hunks"]!.AsArray();
                AssertEqual(2, hunks.Count, "one hunk per file");
                AssertEqual("docs/notes.md", hunks[0]!["file"]!.GetValue<string>());
                AssertEqual("more prose", hunks[0]!["added"]!.GetValue<string>());
                AssertEqual("src/Caf\u00e9.cs", hunks[1]!["file"]!.GetValue<string>(), "the code hunk is labelled with its own file");
                AssertEqual("b\n++counter;", hunks[1]!["added"]!.GetValue<string>(), "an added line starting with ++ stays in its hunk");
            }).ConfigureAwait(false);

            await RunTest("NeverLowers_SubstantiveRule_ModelDocsOnly_StaysSubstantive", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The model reads docs_only with high confidence, but the rule already saw a behaviour
                // file: a Substantive reading is NEVER lowered.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("docs_only", 0.99, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.Substantive), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.Substantive, result.Substance);
            }).ConfigureAwait(false);

            await RunTest("NeverLowers_SubstantiveRule_ModelBehaviour_StaysSubstantive", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.99, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.Substantive), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.Substantive, result.Substance);
                AssertTrue(!String.Equals(result.Outcome, "raised_behaviour", StringComparison.Ordinal), "an already-Substantive reading is not a raise");
            }).ConfigureAwait(false);

            await RunTest("DocsRule_ModelDocsOnly_NotRaised", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("docs_only", 0.99, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.DocumentationOnly, result.Substance);
                AssertEqual("rule", result.Outcome);
            }).ConfigureAwait(false);

            await RunTest("RiskyAboveThreshold_AddsEscalation", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Substantive rule already, docs choice (no raise), but risky high: adds an escalation.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("test_only", 0.2, 0.95));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.Substantive), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.RiskyEscalation, "a risky noul at threshold adds a critical-trigger escalation reason");
                AssertTrue(!String.IsNullOrEmpty(result.RiskyReason), "the escalation carries a reason");
                AssertEqual(ChangeSubstanceEnum.Substantive, result.Substance);
                AssertEqual("risky_escalation", result.Outcome);
            }).ConfigureAwait(false);

            await RunTest("RaiseAndRisky_BothApply", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.95, 0.95));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.None), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.Substantive, result.Substance);
                AssertTrue(result.RiskyEscalation, "risky escalation applies alongside a raise");
                AssertEqual("raised_and_risky", result.Outcome);
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                ChangeSubstanceVerdict result = await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(ChangeSubstanceEnum.DocumentationOnly, result.Substance);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(SubstanceResult("behaviour", 0.95, 0.1));
                TypedChangeSubstanceAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);

            await RunTest("RescueEffectiveness_RaisedSubstance_IsNotIneffective", () =>
            {
                // The consumer-side guarantee: an ineffective-rescue decision on a BehaviorChange
                // requirement flips from ineffective (docs-only) to effective (substantive) once D17
                // raises the reading.
                RescueEffectivenessAssessment docsOnly = RescueEffectivenessEvaluator.Assess(
                    ChangeSubstanceEnum.DocumentationOnly, RescueChangeRequirementEnum.BehaviorChange);
                AssertTrue(docsOnly.IsIneffective, "a documentation-only rescue is ineffective for a behaviour requirement");

                RescueEffectivenessAssessment raised = RescueEffectivenessEvaluator.Assess(
                    ChangeSubstanceEnum.Substantive, RescueChangeRequirementEnum.BehaviorChange);
                AssertTrue(!raised.IsIneffective, "a raised (Substantive) reading is effective");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
