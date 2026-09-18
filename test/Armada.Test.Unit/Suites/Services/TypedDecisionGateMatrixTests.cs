namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The behavioural contract of the shared typed-decision gate, run as one matrix so every adapter
    /// answers the same mode x availability x confidence cell the same way. It has two halves. First it
    /// drives <see cref="TypedDecisionGate.Classify"/> across the whole matrix, because that one function
    /// now decides the mode, threshold and fallback rule for every adapter. Second it drives a probe
    /// adapter built on <see cref="TypedDecisionAdapterBase{TInput,TVerdict,TModel}"/> across the same
    /// matrix and checks the outcome each cell produces: which verdict is returned, which event is
    /// recorded, and that the model never makes an outcome less conservative than the rule. This is a
    /// behavioural check, not a source-text guard: it executes the gate and reads the results.
    /// </summary>
    public class TypedDecisionGateMatrixTests : TestSuite
    {
        public override string Name => "Typed Decision Gate (shared matrix)";

        private const string _ProbeDecision = "matrix_probe";
        private const double _Threshold = 0.90;

        #region Probe-Adapter

        private sealed class ProbeInput
        {
            public Mission? Mission { get; init; }
        }

        private sealed class ProbeVerdict
        {
            public bool Held { get; }

            public ProbeVerdict(bool held)
            {
                Held = held;
            }
        }

        private sealed class ProbeReading : TypedModelReading
        {
            private readonly double _Confidence;

            public ProbeReading(double confidence)
            {
                _Confidence = confidence;
            }

            public override double Confidence => _Confidence;

            public override string Label => "hold";
        }

        /// <summary>
        /// A minimal adapter on the shared base. Its model proposes one action — hold — and its Combine
        /// only ever adds a hold, never clears one, so a rule hard-block (an already-held verdict) can
        /// never be turned into a less conservative outcome by the model.
        /// </summary>
        private sealed class ProbeAdapter : TypedDecisionAdapterBase<ProbeInput, ProbeVerdict, ProbeReading>
        {
            public ProbeAdapter(ITypedDecisionClient client, TypedDecisionRecorder recorder, TypedDecisionSettings settings, LoggingModule logging)
                : base(client, recorder, settings, logging)
            {
            }

            protected override string DecisionPoint => _ProbeDecision;

            protected override string _Header => "[ProbeAdapter] ";

            protected override object BuildState(ProbeInput input)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["probe"] = "state" };
            }

            protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
            {
                return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
                {
                    ["hold"] = new NoulQuestion("Should the rule outcome be held?", TrueMeaning: "hold", FalseMeaning: "allow")
                };
            }

            protected override ProbeReading Interpret(TypedDecisionResult result)
            {
                return new ProbeReading(TypedAnswerReader.ReadNoul(result, "hold"));
            }

            protected override ProbeVerdict Combine(ProbeVerdict ruleVerdict, ProbeReading model)
            {
                // The model may only add a hold; it can never clear the rule's hold. This is the
                // conservative-direction guarantee every adapter's Combine must keep.
                return new ProbeVerdict(ruleVerdict.Held || true);
            }

            protected override string RuleLabel(ProbeVerdict ruleVerdict) => ruleVerdict.Held ? "held" : "allow";

            protected override Mission? MissionOf(ProbeInput input) => input.Mission;
        }

        #endregion

        #region Helpers

        private static ResolvedTypedDecision Resolved(TypedDecisionModeEnum mode)
        {
            return new ResolvedTypedDecision(mode, _Threshold);
        }

        private static TypedDecisionSettings Settings(TypedDecisionModeEnum global, TypedDecisionModeEnum decision)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = global };
            settings.Decisions[_ProbeDecision] = new TypedDecisionRuleSettings { Mode = decision, GateThreshold = _Threshold };
            return settings;
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        #endregion

        protected override async Task RunTestsAsync()
        {
            await RunClassifyMatrixAsync().ConfigureAwait(false);
            await RunBaseApplicationMatrixAsync().ConfigureAwait(false);
            await RunConservativeDirectionAsync().ConfigureAwait(false);
            await RunGlobalCapAsync().ConfigureAwait(false);
        }

        // The pure gate rule: the one function every adapter routes through, checked over every cell.
        private async Task RunClassifyMatrixAsync()
        {
            await RunTest("Classify_Off_IsOff_ForEveryAvailabilityAndConfidence", () =>
            {
                foreach (bool available in new[] { true, false })
                    foreach (double confidence in new[] { 0.0, 0.5, 1.0 })
                        AssertEqual(TypedGateOutcome.Off, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Off), available, confidence),
                            "Off must stay Off regardless of availability or confidence");
                AssertEqual(TypedGateOutcome.Off, TypedDecisionGate.Classify(null!, true, 1.0), "a null config is Off, never a call");
            });

            await RunTest("Classify_Unavailable_KeepsRule_WhenNotOff", () =>
            {
                AssertEqual(TypedGateOutcome.Unavailable, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Shadow), false, 1.0));
                AssertEqual(TypedGateOutcome.Unavailable, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Gate), false, 1.0));
            });

            await RunTest("Classify_Shadow_IsShadow_ForEveryConfidence", () =>
            {
                foreach (double confidence in new[] { 0.0, _Threshold, 0.99 })
                    AssertEqual(TypedGateOutcome.Shadow, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Shadow), true, confidence),
                        "Shadow never gates, whatever the confidence");
            });

            await RunTest("Classify_Gate_BelowThreshold_KeepsRule", () =>
            {
                AssertEqual(TypedGateOutcome.BelowThreshold, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Gate), true, 0.0));
                AssertEqual(TypedGateOutcome.BelowThreshold, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Gate), true, _Threshold - 0.01),
                    "just under the threshold keeps the rule");
            });

            await RunTest("Classify_Gate_AtOrAboveThreshold_Gates", () =>
            {
                AssertEqual(TypedGateOutcome.Gated, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Gate), true, _Threshold),
                    "at the threshold gates");
                AssertEqual(TypedGateOutcome.Gated, TypedDecisionGate.Classify(Resolved(TypedDecisionModeEnum.Gate), true, 1.0));
            });

            await RunTest("Classify_ShadowOutcomeLabel_MatchesTheKeptRulePath", () =>
            {
                AssertEqual("shadow_mode", TypedDecisionGate.ShadowOutcomeLabel(TypedGateOutcome.Shadow));
                AssertEqual("below_threshold", TypedDecisionGate.ShadowOutcomeLabel(TypedGateOutcome.BelowThreshold));
            });
        }

        // The base applies each cell's outcome: which verdict it returns, and which event it records.
        private async Task RunBaseApplicationMatrixAsync()
        {
            await RunTest("Base_Off_KeepsRule_NoCall_NoEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", 0.99));
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Off), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(false), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Held, "Off must keep the rule (allow)");
                AssertEqual(0, client.CallCount, "Off must not call the model");
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            });

            foreach (TypedDecisionModeEnum mode in new[] { TypedDecisionModeEnum.Shadow, TypedDecisionModeEnum.Gate })
            {
                TypedDecisionModeEnum captured = mode;
                await RunTest("Base_Unavailable_KeepsRule_RecordsUnavailable_" + captured, async () =>
                {
                    using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                    ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Gate, captured), new LoggingModule());

                    ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(false), CancellationToken.None).ConfigureAwait(false);

                    AssertTrue(!result.Held, "an unavailable model must keep the rule");
                    AssertEqual(1, client.CallCount, "the model was consulted");
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
                    AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
                });
            }

            await RunTest("Base_Shadow_Available_KeepsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", 0.99));
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Shadow), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(false), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Held, "Shadow keeps the rule even at high confidence");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            });

            await RunTest("Base_Gate_BelowThreshold_KeepsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", _Threshold - 0.10));
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(false), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Held, "a below-threshold confidence keeps the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            });

            await RunTest("Base_Gate_AtOrAboveThreshold_Combines_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", 0.99));
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(false), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Held, "at or above threshold the model may hold");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            });
        }

        // The model never makes an outcome less conservative than the rule.
        private async Task RunConservativeDirectionAsync()
        {
            await RunTest("Base_RuleHardBlock_StaysHeld_WhenModelWouldNotHold", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The model reports low confidence, so the gate keeps the rule; the rule already holds.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", 0.0));
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(true), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Held, "a rule hard-block is never cleared by a below-threshold model");
            });

            await RunTest("Base_RuleHardBlock_StaysHeld_WhenModelGates", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", 0.99));
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(true), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Held, "combine only ever narrows: a held rule stays held");
            });
        }

        // The global mode caps every decision: it is the kill switch.
        private async Task RunGlobalCapAsync()
        {
            await RunTest("Base_GlobalShadow_CapsADecisionInGate_ToShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", 0.99));
                // The decision asks for Gate, but the global cap is Shadow, so nothing gates.
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Shadow, TypedDecisionModeEnum.Gate), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(false), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Held, "the global cap holds the decision in Shadow");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            });

            await RunTest("Base_GlobalOff_CapsEveryDecision_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("hold", 0.99));
                ProbeAdapter adapter = new ProbeAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(TypedDecisionModeEnum.Off, TypedDecisionModeEnum.Gate), new LoggingModule());

                ProbeVerdict result = await adapter.DecideAsync(new ProbeInput(), new ProbeVerdict(false), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Held, "the global kill switch keeps every rule");
                AssertEqual(0, client.CallCount, "a global Off makes no call");
            });
        }
    }
}
