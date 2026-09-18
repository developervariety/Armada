namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class TypedDecisionSettingsTests : TestSuite
    {
        public override string Name => "Typed Decision Settings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Defaults_ShipEveryDecisionInGate", () =>
            {
                TypedDecisionSettings settings = new TypedDecisionSettings();
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Mode);
                AssertEqual("ARMADA_TYPESAFE_KEY", settings.ApiKeyEnv);
                AssertEqual("https://api.typesafe.ai", settings.BaseUrl);
                AssertEqual("jev-latest", settings.Model);
                AssertEqual(8000, settings.MaxStateChars);
                AssertEqual(10, settings.TimeoutSeconds);
                AssertTrue(settings.EvalOnModelChange, "a new model version is evaluated by default");
                AssertTrue(settings.CaptainTool.Enabled, "captain tool enabled by default");

                // Decisions with a stated threshold keep it.
                AssertEqual(0.90, settings.Decisions["failure_cause"].GateThreshold);
                AssertEqual(0.85, settings.Decisions["review_substance"].GateThreshold);
                AssertEqual(0.80, settings.Decisions["preflight"].GateThreshold);

                // Every named decision ships in Gate with a real threshold; none is left at an unset zero.
                AssertTrue(settings.Decisions.Count >= 26, "the default decision map carries every named decision");
                foreach (string name in new[] { "prior_art", "memory_review", "stage_necessity", "handoff_outcome", "lint_finding", "criteria_lint" })
                    AssertTrue(settings.Decisions.ContainsKey(name), name + " is in the decision map");
                // A threshold below the 0.80 norm must be DELIBERATE, so each one is named here with the
                // measurement behind it. The guard is that no decision reaches a low gate by accident or by
                // an unset zero; it is not that every decision suits a high threshold. A decision whose
                // readings never approach 0.90 would otherwise ship a gate that can never fire while its
                // mode still reads as enforced.
                Dictionary<string, double> belowNorm = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    // Measured across ten real candidates: settled outputs read 0.09-0.15, load-bearing ones
                    // 0.60-0.67. Nothing reaches 0.90, so 0.90 would spare nothing.
                    ["context_compaction"] = 0.55
                };

                foreach (KeyValuePair<string, TypedDecisionRuleSettings> entry in settings.Decisions)
                {
                    AssertEqual(TypedDecisionModeEnum.Gate, entry.Value.Mode, entry.Key + " ships in Gate");
                    if (belowNorm.TryGetValue(entry.Key, out double documented))
                        AssertEqual(documented, entry.Value.GateThreshold, entry.Key + " keeps its documented below-norm threshold");
                    else
                        AssertTrue(entry.Value.GateThreshold >= 0.80, entry.Key + " has a gate threshold at or above the norm, or a documented exception");
                    AssertTrue(entry.Value.GateThreshold > 0.0, entry.Key + " is never left at an unset zero");
                    AssertEqual(TypedDecisionModeEnum.Gate, settings.For(entry.Key).Mode, entry.Key + " is effective in Gate");
                }

                foreach (string name in belowNorm.Keys)
                    AssertTrue(settings.Decisions.ContainsKey(name), name + " still ships; delete its below-norm entry when it does not");
            });

            await RunTest("For_EffectiveModeIsMinOfGlobalAndDecision", () =>
            {
                TypedDecisionSettings settings = new TypedDecisionSettings();

                // Global Off caps everything to Off even when a decision is Gate.
                settings.Mode = TypedDecisionModeEnum.Off;
                settings.Decisions["failure_cause"].Mode = TypedDecisionModeEnum.Gate;
                AssertEqual(TypedDecisionModeEnum.Off, settings.For("failure_cause").Mode, "global Off caps decision Gate");

                // Global Gate but decision Off stays Off.
                settings.Mode = TypedDecisionModeEnum.Gate;
                settings.Decisions["refusal"].Mode = TypedDecisionModeEnum.Off;
                AssertEqual(TypedDecisionModeEnum.Off, settings.For("refusal").Mode, "decision Off stays Off under global Gate");

                // Both Gate -> Gate, with the decision's threshold.
                settings.Decisions["failure_cause"].Mode = TypedDecisionModeEnum.Gate;
                ResolvedTypedDecision resolved = settings.For("failure_cause");
                AssertEqual(TypedDecisionModeEnum.Gate, resolved.Mode);
                AssertEqual(0.90, resolved.GateThreshold);

                // Global Gate with a decision in Shadow -> Shadow (the lower of the two).
                settings.Decisions["failure_cause"].Mode = TypedDecisionModeEnum.Shadow;
                AssertEqual(TypedDecisionModeEnum.Shadow, settings.For("failure_cause").Mode, "min(Gate, Shadow) is Shadow");
            });

            await RunTest("For_UnknownDecision_IsOff", () =>
            {
                TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
                AssertEqual(TypedDecisionModeEnum.Off, settings.For("no_such_decision").Mode);
            });

            await RunTest("HotReloadSwap_ReplacesTypedDecisionsSection", () =>
            {
                // The reference-swap in ApplyHotReloadableFrom must flip the mode live so an MCP
                // settings write cannot clobber it.
                ArmadaSettings current = new ArmadaSettings();
                AssertEqual(TypedDecisionModeEnum.Gate, current.TypedDecisions.Mode);

                ArmadaSettings incoming = new ArmadaSettings();
                incoming.TypedDecisions.Mode = TypedDecisionModeEnum.Shadow;

                current.ApplyHotReloadableFrom(incoming);

                AssertEqual(TypedDecisionModeEnum.Shadow, current.TypedDecisions.Mode, "the section is in the hot-reload swap list");
            });

            await RunTest("HotReloadSwap_CarriesRetentionSettings", () =>
            {
                // Retention is read per call from the live object, so a value the copy drops reads as
                // the old one forever: enabling retention in settings would silently retain nothing.
                ArmadaSettings current = new ArmadaSettings();
                AssertFalse(current.TypedDecisions.Retention.Enabled, "retention is off by default");

                ArmadaSettings incoming = new ArmadaSettings();
                incoming.TypedDecisions.Retention.Enabled = true;
                incoming.TypedDecisions.Retention.RetentionDays = 45;
                incoming.TypedDecisions.Retention.MinimumSamplesPerDecision = 25;

                current.ApplyHotReloadableFrom(incoming);

                AssertTrue(current.TypedDecisions.Retention.Enabled, "a hot reload carries the retention switch");
                AssertEqual(45, current.TypedDecisions.Retention.RetentionDays, "and the retention window");
                AssertEqual(25, current.TypedDecisions.Retention.MinimumSamplesPerDecision, "and the minimum sample count");
            });
        }
    }
}
