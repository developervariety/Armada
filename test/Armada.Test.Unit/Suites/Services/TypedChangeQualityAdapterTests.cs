namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the change_quality adapter. Off makes no call and returns the deterministic weaknesses
    /// unchanged; unavailable and below-threshold keep the rule verdict and record the right event; a
    /// gate at or above threshold adds the model's informational weaknesses on top of the rule's, and a
    /// deterministic MustFix is never removed. The adapter never throws.
    /// </summary>
    public class TypedChangeQualityAdapterTests : TestSuite
    {
        public override string Name => "Typed Change Quality Adapter";

        private const double _Threshold = 0.90;

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off makes no call and returns the deterministic weaknesses", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AllWeak(0.99));
                TypedChangeQualityAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Off, TypedDecisionModeEnum.Gate);

                ChangeQualityVerdict rule = ChangeQualityVerdict.From(new List<ChangeQualityWeakness>
                {
                    new ChangeQualityWeakness { Dimension = ChangeQualityDimensions.CoreRule, Severity = ChangeQualitySeverity.MustFix, Source = ChangeQualitySource.Rule }
                });
                ChangeQualityVerdict outcome = await adapter.DecideAsync(Input(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, client.CallCount, "Off never calls the model");
                AssertEqual(1, outcome.Weaknesses.Count, "the rule verdict is returned unchanged");
                AssertEqual(ChangeQualityDimensions.CoreRule, outcome.Weaknesses[0].Dimension);
            });

            await RunTest("Unavailable keeps the rule verdict and records an unavailable event", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TypedDecisionResult.Exception());
                TypedChangeQualityAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                ChangeQualityVerdict rule = RuleOnly();
                ChangeQualityVerdict outcome = await adapter.DecideAsync(Input(), rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, outcome.Weaknesses.Count, "unavailable falls back to the rule verdict");
                List<ArmadaEvent> events = await TypedEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertTrue(events.Any(e => e.EventType == TypedDecisionRecorder.EventTypeUnavailable), "an unavailable event is recorded");
            });

            await RunTest("Below threshold records a shadow event and does not add model weaknesses", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AllWeak(0.40));
                TypedChangeQualityAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                ChangeQualityVerdict outcome = await adapter.DecideAsync(Input(), RuleOnly(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, outcome.Weaknesses.Count, "below threshold, only the rule weakness stands");
                List<ArmadaEvent> events = await TypedEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertTrue(events.Any(e => e.EventType == TypedDecisionRecorder.EventTypeShadow), "a shadow event is recorded below threshold");
            });

            await RunTest("Gate at threshold adds model weaknesses and keeps the deterministic MustFix", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Model flags dry and readability; complexity/modularity/maintainability clean.
                Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                {
                    [ChangeQualityDimensions.Dry + "_weak"] = new TypedAnswer { Type = "noul", Noul = 0.95, Confidence = 0.95 },
                    [ChangeQualityDimensions.Readability + "_weak"] = new TypedAnswer { Type = "noul", Noul = 0.93, Confidence = 0.93 },
                    [ChangeQualityDimensions.CognitiveComplexity + "_weak"] = new TypedAnswer { Type = "noul", Noul = 0.10, Confidence = 0.10 },
                    [ChangeQualityDimensions.Modularity + "_weak"] = new TypedAnswer { Type = "noul", Noul = 0.10, Confidence = 0.10 },
                    [ChangeQualityDimensions.Maintainability + "_weak"] = new TypedAnswer { Type = "noul", Noul = 0.10, Confidence = 0.10 }
                };
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = true, Answers = answers });
                TypedChangeQualityAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                ChangeQualityVerdict outcome = await adapter.DecideAsync(Input(), RuleOnly(), CancellationToken.None).ConfigureAwait(false);

                List<string> dims = outcome.Weaknesses.Select(w => w.Dimension).ToList();
                AssertTrue(dims.Contains(ChangeQualityDimensions.CoreRule), "the deterministic core_rule MustFix is kept");
                AssertTrue(dims.Contains(ChangeQualityDimensions.Dry), "the model dry weakness is added");
                AssertTrue(dims.Contains(ChangeQualityDimensions.Readability), "the model readability weakness is added");
                AssertFalse(dims.Contains(ChangeQualityDimensions.Modularity), "a clean dimension is not flagged");
                // The model dims are informational (ShouldFix); only the rule-backed one is routable.
                AssertEqual(1, outcome.RoutableWeaknesses.Count, "only the deterministic MustFix is routable");
                AssertEqual(ChangeQualityDimensions.CoreRule, outcome.RoutableWeaknesses[0].Dimension);
                List<ArmadaEvent> events = await TypedEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertTrue(events.Any(e => e.EventType == TypedDecisionRecorder.EventTypeGated), "a gated event is recorded");
            });
        }

        private static ChangeQualityInput Input()
            => new ChangeQualityInput { UnifiedDiff = "diff --git a/x b/x\n@@ -1 +1,2 @@\n a\n+b\n" };

        private static ChangeQualityVerdict RuleOnly()
            => ChangeQualityVerdict.From(new List<ChangeQualityWeakness>
            {
                new ChangeQualityWeakness { Dimension = ChangeQualityDimensions.CoreRule, Severity = ChangeQualitySeverity.MustFix, Source = ChangeQualitySource.Rule, Reason = "slop fail" }
            });

        private static TypedDecisionResult AllWeak(double value)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            foreach (string d in ChangeQualityDimensions.ModelDimensions)
                answers[d + "_weak"] = new TypedAnswer { Type = "noul", Noul = value, Confidence = value };
            return new TypedDecisionResult { Available = true, Answers = answers };
        }

        private static TypedChangeQualityAdapter BuildAdapter(DatabaseDriver database, FakeTypedDecisionClient client, TypedDecisionModeEnum globalMode, TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions["change_quality"].Mode = decisionMode;
            settings.Decisions["change_quality"].GateThreshold = _Threshold;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new TypedChangeQualityAdapter(client, recorder, settings, new LoggingModule());
        }

        private static async Task<List<ArmadaEvent>> TypedEventsAsync(DatabaseDriver database)
        {
            List<ArmadaEvent> all = new List<ArmadaEvent>();
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 50).ConfigureAwait(false));
            return all;
        }
    }
}
