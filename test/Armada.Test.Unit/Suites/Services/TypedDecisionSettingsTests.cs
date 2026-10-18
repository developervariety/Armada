namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class TypedDecisionSettingsTests : TestSuite
    {
        public override string Name => "Typed Decision Settings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Defaults_ShipTheOwnerRuledDecisionsOff_AndTheRestInGate", () =>
            {
                TypedDecisionSettings settings = new TypedDecisionSettings();
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Mode);
                AssertEqual("ARMADA_TYPESAFE_KEY", settings.ApiKeyEnv);
                AssertEqual("https://api.typesafe.ai", settings.BaseUrl);
                AssertEqual("jev-latest", settings.Model);
                AssertEqual(60000, settings.MaxStateChars);
                AssertEqual(60000, settings.CaptainTool.MaxStateChars);
                AssertEqual(0, settings.EgressExcludedVesselIds.Count, "no vessel is excluded by the product: which vessels hold banned content is operator configuration");
                AssertEqual(10, settings.TimeoutSeconds);
                AssertTrue(settings.EvalOnModelChange, "a new model version is evaluated by default");
                AssertTrue(settings.CaptainTool.Enabled, "captain tool enabled by default");

                // Decisions with a stated threshold keep it.
                AssertEqual(0.90, settings.Decisions["failure_cause"].GateThreshold);
                AssertEqual(0.85, settings.Decisions["review_substance"].GateThreshold);
                AssertEqual(0.80, settings.Decisions["preflight"].GateThreshold);

                // The decisions the owner switched off ship Off; every other named decision ships in Gate.
                // Each keeps a real threshold; none is left at an unset zero.
                AssertTrue(settings.Decisions.Count >= 26, "the default decision map carries every named decision");
                foreach (string name in new[] { "prior_art", "memory_review", "stage_necessity", "handoff_outcome", "lint_finding", "criteria_lint", "dispatch_staleness" })
                    AssertTrue(settings.Decisions.ContainsKey(name), name + " is in the decision map");
                AssertFalse(settings.Decisions.ContainsKey("context_compaction"), "context_compaction is not a shipped decision");
                AssertFalse(settings.Decisions.ContainsKey("routing_hint"), "routing_hint is not a shipped decision");

                HashSet<string> shippedOff = new HashSet<string>(StringComparer.Ordinal)
                {
                    "inbox_triage", "stage_necessity", "prior_art", "revision_kind", "memory_record",
                    "memory_review", "memory_candidate", "flake_score", "owner_digest"
                };
                foreach (string name in shippedOff)
                {
                    AssertTrue(settings.Decisions.ContainsKey(name), name + " stays in the decision map");
                    AssertFalse(TypedDecisionWiring.UnwiredDecisions.ContainsKey(name), name + " is Off by mode, not unwired");
                }

                foreach (KeyValuePair<string, TypedDecisionRuleSettings> entry in settings.Decisions)
                {
                    TypedDecisionModeEnum shipped = shippedOff.Contains(entry.Key) ? TypedDecisionModeEnum.Off : TypedDecisionModeEnum.Gate;
                    AssertEqual(shipped, entry.Value.Mode, entry.Key + " ships in " + shipped);
                    AssertEqual(shipped, settings.For(entry.Key).Mode, entry.Key + " is effective in " + shipped);
                    AssertTrue(entry.Value.GateThreshold >= 0.80, entry.Key + " has a gate threshold at or above the norm");
                    AssertTrue(entry.Value.GateThreshold > 0.0, entry.Key + " is never left at an unset zero");
                }
            });

            await RunTest("EgressExclusion_RefusesOnlyTheListedVessels", () =>
            {
                TypedDecisionSettings settings = new TypedDecisionSettings();
                settings.EgressExcludedVesselIds = new List<string> { "vsl_banned" };
                AssertFalse(settings.AllowsEgress("vsl_banned"), "a listed vessel sends nothing");
                AssertTrue(settings.AllowsEgress("vsl_other"), "an unlisted vessel is unaffected");
                AssertTrue(settings.AllowsEgress(null), "a decision with no mission is not a vessel's content");
            });

            await RunTest("HotReloadSwap_CarriesEgressExclusions_AsACopy", () =>
            {
                // Adapters hold the live object, so a reload copies in place. The list is copied, not shared,
                // or the next file's edit would reach into the previous one.
                TypedDecisionSettings live = new TypedDecisionSettings();
                TypedDecisionSettings file = new TypedDecisionSettings { MaxStateChars = 42000 };
                file.EgressExcludedVesselIds = new List<string> { "vsl_banned" };
                live.CopyFrom(file);
                AssertFalse(live.AllowsEgress("vsl_banned"), "the exclusion reaches the live object on reload");
                AssertEqual(42000, live.MaxStateChars);
                file.EgressExcludedVesselIds.Add("vsl_later");
                AssertTrue(live.AllowsEgress("vsl_later"), "the live list is a copy, not the file's list");
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
