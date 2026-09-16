namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class TypedDecisionSettingsTests : TestSuite
    {
        public override string Name => "Typed Decision Settings";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Defaults_ShipGateForPhaseOneAndOffOtherwise", () =>
            {
                TypedDecisionSettings settings = new TypedDecisionSettings();
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Mode);
                AssertEqual("ARMADA_TYPESAFE_KEY", settings.ApiKeyEnv);
                AssertEqual("https://api.typesafe.ai", settings.BaseUrl);
                AssertEqual("jev-latest", settings.Model);
                AssertEqual(8000, settings.MaxStateChars);
                AssertEqual(10, settings.TimeoutSeconds);
                AssertFalse(settings.CaptainTool.Enabled, "captain tool disabled by default");
                AssertEqual(40, settings.CaptainTool.MaxCallsPerMission);

                // The six Phase-1 decisions ship in Gate with their thresholds.
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Decisions["failure_cause"].Mode);
                AssertEqual(0.90, settings.Decisions["failure_cause"].GateThreshold);
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Decisions["refusal"].Mode);
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Decisions["runtime_failure"].Mode);
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Decisions["review_substance"].Mode);
                AssertEqual(0.85, settings.Decisions["review_substance"].GateThreshold);
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Decisions["preflight"].Mode);
                AssertEqual(0.80, settings.Decisions["preflight"].GateThreshold);
                AssertEqual(TypedDecisionModeEnum.Gate, settings.Decisions["papercut_merge"].Mode);

                // Every other decision is Off until its adapter lane lands.
                AssertEqual(TypedDecisionModeEnum.Off, settings.Decisions["leak_hunk"].Mode);
                AssertEqual(TypedDecisionModeEnum.Off, settings.Decisions["stage_necessity"].Mode);
                AssertEqual(TypedDecisionModeEnum.Off, settings.Decisions["handoff_outcome"].Mode);
                AssertTrue(settings.Decisions.ContainsKey("prior_art"), "prior_art is in the decision map");
                AssertEqual(TypedDecisionModeEnum.Off, settings.Decisions["prior_art"].Mode);
                AssertTrue(settings.Decisions.Count >= 25, "the default decision map carries every named decision");
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
        }
    }
}
