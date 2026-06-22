namespace Armada.Test.Unit
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for the M1 config-driven capability routing feature: the
    /// ModelCapabilityProfile value type, the ModelTierSettings.ModelCapabilityProfiles
    /// and CapabilityHintDimensionMap config dictionaries, the
    /// PreferredModelTierSelector capability-hint constants/helpers, and the optional
    /// capabilityHint parameter on SelectModel that orders eligible idle captains within
    /// their already-chosen tier by best fit for a hint while degrading gracefully when
    /// no usable hint, profile, or dimension is present.
    /// </summary>
    public class CapabilityRoutingTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Capability Routing";

        private static Captain MakeCaptain(string model, string? allowedPersonas = null)
        {
            Captain c = new Captain("test-captain");
            c.Model = model;
            c.AllowedPersonas = allowedPersonas;
            c.State = CaptainStateEnum.Idle;
            return c;
        }

        // Profile with the two dimensions the hint map exercises; telemetry/cost stay at
        // their neutral 50 default so the tests pin only the values that drive selection.
        private static ModelCapabilityProfile Profile(int auditReasoningFit, int mechanicalThroughput)
        {
            ModelCapabilityProfile p = new ModelCapabilityProfile();
            p.AuditReasoningFit = auditReasoningFit;
            p.MechanicalThroughput = mechanicalThroughput;
            return p;
        }

        // Settings carrying explicit profiles so selection outcomes do not depend on the
        // exact seeded default numbers. Tier membership and the hint->dimension map keep
        // their built-in defaults (all models below are in the default mid-tier list).
        private static ModelTierSettings SettingsWith(Dictionary<string, ModelCapabilityProfile> profiles)
        {
            ModelTierSettings s = new ModelTierSettings();
            s.ModelCapabilityProfiles = profiles;
            return s;
        }

        private static Dictionary<string, ModelCapabilityProfile> MidProfiles()
        {
            return new Dictionary<string, ModelCapabilityProfile>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "claude-sonnet-4-6", Profile(80, 60) },
                { "composer-2.5", Profile(30, 80) },
                { "gemini-3.5-pro", Profile(60, 60) },
                { "opencode-go/kimi-k2.7-code", Profile(25, 85) }
            };
        }

        private static IReadOnlyDictionary<string, List<string>> DefaultMidOrder()
        {
            return new ModelTierSettings().WithinTierPreferenceOrder;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            // --- Hinted selection: highest-scoring profiled model wins ---

            await RunTest("SelectModel_AuditHint_PicksHighestAuditModel_OverPreferenceOrder", () =>
            {
                // Default mid preference order lists K2.7 first, so a no-hint call would pick it.
                // The audit hint maps to AuditReasoningFit, where sonnet (80) outranks every other
                // idle model, so the hint must override the preference-order pick.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "audit");
                AssertEqual("claude-sonnet-4-6", selected, "audit hint must pick the highest AuditReasoningFit model, overriding the preference order");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_ReasoningHeavyHint_MapsToAuditDimension", () =>
            {
                // reasoning-heavy shares the AuditReasoningFit dimension with audit.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("gemini-3.5-pro")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "reasoning-heavy");
                AssertEqual("claude-sonnet-4-6", selected, "reasoning-heavy maps to AuditReasoningFit, so the highest-AR model wins");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MechanicalHint_PicksHighestThroughputModel_OverPreferenceOrder", () =>
            {
                // No K2.7 idle. Default preference order lists sonnet ahead of composer, so a
                // no-hint call would return sonnet. The mechanical hint maps to MechanicalThroughput,
                // where composer (80) beats sonnet (60) and gemini (60), proving a different
                // dimension drives a different pick.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("gemini-3.5-pro")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "mechanical");
                AssertEqual("composer-2.5", selected, "mechanical hint must pick the highest MechanicalThroughput model");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_DocOnlyHint_MapsToMechanicalDimension", () =>
            {
                // doc-only shares the MechanicalThroughput dimension with mechanical (cheap/fast).
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("gemini-3.5-pro")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "doc-only");
                AssertEqual("composer-2.5", selected, "doc-only maps to MechanicalThroughput, so the highest-throughput model wins");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_CaseInsensitiveHint_BehavesLikeCanonical", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "AuDiT");
                AssertEqual("claude-sonnet-4-6", selected, "hint matching is case-insensitive");
                return Task.CompletedTask;
            });

            // --- Fallback when the best-fit captain is not idle ---

            await RunTest("SelectModel_BestFitBusy_NextBestIdleProfiledModelWins", () =>
            {
                // sonnet (top AuditReasoningFit) is busy and therefore absent from the idle list.
                // gemini (60) must win over composer (30) for an audit hint -- the required
                // "fallback when best-fit is busy" behavior.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("gemini-3.5-pro")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "audit");
                AssertEqual("gemini-3.5-pro", selected, "with the top audit model busy, the next-best idle profiled model is chosen");
                return Task.CompletedTask;
            });

            // --- No-hint backward compatibility ---

            await RunTest("SelectModel_NoHint_MatchesPreferenceOrderResult", () =>
            {
                // The new trailing parameter must be backward compatible: a null hint (and an
                // omitted hint) returns exactly what the within-tier preference order would.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                string? omitted = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()));
                string? nullHint = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), null);

                AssertEqual("opencode-go/kimi-k2.7-code", omitted, "no-hint call follows the preference order (K2.7 first)");
                AssertEqual(omitted, nullHint, "an explicit null hint matches the omitted-parameter result");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_UnknownAndEmptyHint_DegradeToNoHintWithoutThrowing", () =>
            {
                // Unrecognized, empty, and whitespace hints are treated as "no hint" and must not
                // throw -- selection degrades to the preference-order path.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                string? unknown = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "totally-unknown-hint");
                string? empty = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "");
                string? whitespace = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "   ");

                AssertEqual("opencode-go/kimi-k2.7-code", unknown, "an unknown hint degrades to preference-order selection");
                AssertEqual("opencode-go/kimi-k2.7-code", empty, "an empty hint degrades to preference-order selection");
                AssertEqual("opencode-go/kimi-k2.7-code", whitespace, "a whitespace hint degrades to preference-order selection");
                return Task.CompletedTask;
            });

            // --- Tier + overflow + specialist interaction with a hint present ---

            await RunTest("SelectModel_MidHint_NoMidIdle_FallsUpDownPerTierOrder", () =>
            {
                // A mid request with only low and high captains idle still walks the non-specialist
                // [mid, low, high] order: low must be chosen before high, and the hint only orders
                // within the chosen tier (here a single low model), never breaking the tier walk.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("kimi-k2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "audit");
                AssertEqual("kimi-k2.5", selected, "a hinted mid request with no mid idle still tries low before high");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_SpecialistPersona_WithHint_StillReservedForHigh", () =>
            {
                // A specialist persona is reserved for high regardless of the hint: even with an
                // idle mid captain and a mechanical hint, only the high-tier captain is eligible.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Judge", _ => 0, null, DefaultMidOrder(), SettingsWith(MidProfiles()), "mechanical");
                AssertEqual("claude-opus-4-7", selected, "specialist reservation to high tier is unchanged by a capability hint");
                return Task.CompletedTask;
            });

            // --- Negative / edge paths the Worker need not have covered ---

            await RunTest("SelectModel_EmptyProfiles_DegradesToPreferenceOrder", () =>
            {
                // No profiles configured at all: every eligible model scores as unprofiled, so the
                // hint cannot reorder and selection falls back to the preference order.
                ModelTierSettings settings = SettingsWith(new Dictionary<string, ModelCapabilityProfile>(System.StringComparer.OrdinalIgnoreCase));
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), settings, "audit");
                AssertEqual("opencode-go/kimi-k2.7-code", selected, "an empty profile map degrades the hint to preference-order selection");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_DimensionAbsentForEveryIdleModel_TrailsViaPreference", () =>
            {
                // The hint resolves to a dimension, but none of the idle models has a profile entry
                // (only a non-idle model is profiled). Unprofiled models sort last / tie, so the
                // preference order decides: sonnet is listed ahead of composer.
                Dictionary<string, ModelCapabilityProfile> profiles = new Dictionary<string, ModelCapabilityProfile>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "gpt-5.5", Profile(90, 90) }
                };
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), SettingsWith(profiles), "audit");
                AssertEqual("claude-sonnet-4-6", selected, "when no idle model is profiled for the dimension, the preference order resolves it");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_HintMapMissingDimension_DegradesToPreferenceOrder", () =>
            {
                // The hint is recognized but the CapabilityHintDimensionMap has no entry for it
                // (operator cleared the map), so no dimension is resolved and selection degrades.
                ModelTierSettings settings = SettingsWith(MidProfiles());
                settings.CapabilityHintDimensionMap = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), settings, "audit");
                AssertEqual("opencode-go/kimi-k2.7-code", selected, "a hint with no mapped dimension degrades to preference-order selection");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_TiedScores_ResolvedDeterministicallyByPreference", () =>
            {
                // Two models with identical dimension scores must resolve by the configured
                // within-tier preference order, deterministically (flipping the order flips the pick).
                Dictionary<string, ModelCapabilityProfile> tied = new Dictionary<string, ModelCapabilityProfile>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "claude-sonnet-4-6", Profile(50, 50) },
                    { "composer-2.5", Profile(50, 50) }
                };
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6")
                };

                Dictionary<string, List<string>> sonnetFirst = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "mid", new List<string> { "claude-sonnet-4-6", "composer-2.5" } }
                };
                Dictionary<string, List<string>> composerFirst = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "mid", new List<string> { "composer-2.5", "claude-sonnet-4-6" } }
                };

                string? pickA = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, sonnetFirst, SettingsWith(tied), "audit");
                string? pickB = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, composerFirst, SettingsWith(tied), "audit");

                AssertEqual("claude-sonnet-4-6", pickA, "tie resolves to the model listed first in the preference order");
                AssertEqual("composer-2.5", pickB, "flipping the preference order flips the tie winner -- resolution is deterministic");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_SeededDefaults_AuditPrefersRichModel", () =>
            {
                // Guard the shipped seed direction: with the built-in default profiles, an audit
                // hint must prefer the rich-telemetry/high-audit sonnet over the throughput-tuned
                // composer. Uses default settings (no custom profiles) to pin the seed values.
                ModelTierSettings defaults = new ModelTierSettings();
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, defaults.WithinTierPreferenceOrder, defaults, "audit");
                AssertEqual("claude-sonnet-4-6", selected, "default seeds give sonnet a higher AuditReasoningFit than composer");
                return Task.CompletedTask;
            });

            // --- Public hint helpers ---

            await RunTest("IsCapabilityHint_RecognizedAndCaseInsensitive_ReturnsTrue", () =>
            {
                AssertTrue(PreferredModelTierSelector.IsCapabilityHint("audit"), "audit is a hint");
                AssertTrue(PreferredModelTierSelector.IsCapabilityHint("reasoning-heavy"), "reasoning-heavy is a hint");
                AssertTrue(PreferredModelTierSelector.IsCapabilityHint("mechanical"), "mechanical is a hint");
                AssertTrue(PreferredModelTierSelector.IsCapabilityHint("doc-only"), "doc-only is a hint");
                AssertTrue(PreferredModelTierSelector.IsCapabilityHint("AUDIT"), "hint recognition is case-insensitive");
                return Task.CompletedTask;
            });

            await RunTest("IsCapabilityHint_NullEmptyOrUnknown_ReturnsFalse", () =>
            {
                AssertFalse(PreferredModelTierSelector.IsCapabilityHint(null), "null is not a hint");
                AssertFalse(PreferredModelTierSelector.IsCapabilityHint(""), "empty is not a hint");
                AssertFalse(PreferredModelTierSelector.IsCapabilityHint("   "), "whitespace is not a hint");
                AssertFalse(PreferredModelTierSelector.IsCapabilityHint("mid"), "a tier selector is not a capability hint");
                AssertFalse(PreferredModelTierSelector.IsCapabilityHint("bogus"), "an unknown value is not a hint");
                return Task.CompletedTask;
            });

            await RunTest("NormalizeCapabilityHint_Canonicalizes_OrReturnsNull", () =>
            {
                AssertEqual(PreferredModelTierSelector.AuditHint, PreferredModelTierSelector.NormalizeCapabilityHint("AUDIT"), "AUDIT normalizes to canonical audit");
                AssertEqual(PreferredModelTierSelector.ReasoningHeavyHint, PreferredModelTierSelector.NormalizeCapabilityHint("Reasoning-Heavy"), "mixed case reasoning-heavy normalizes");
                AssertEqual(PreferredModelTierSelector.MechanicalHint, PreferredModelTierSelector.NormalizeCapabilityHint("mechanical"), "mechanical normalizes to itself");
                AssertEqual(PreferredModelTierSelector.DocOnlyHint, PreferredModelTierSelector.NormalizeCapabilityHint("DOC-ONLY"), "DOC-ONLY normalizes to canonical doc-only");
                AssertNull(PreferredModelTierSelector.NormalizeCapabilityHint(null), "null normalizes to null");
                AssertNull(PreferredModelTierSelector.NormalizeCapabilityHint(""), "empty normalizes to null");
                AssertNull(PreferredModelTierSelector.NormalizeCapabilityHint("unknown"), "an unknown hint normalizes to null (graceful degradation)");
                return Task.CompletedTask;
            });

            // --- ModelCapabilityProfile value type ---

            await RunTest("ModelCapabilityProfile_DefaultScores_AreNeutralFifty", () =>
            {
                ModelCapabilityProfile p = new ModelCapabilityProfile();
                AssertEqual(50, p.TelemetryRichness, "default TelemetryRichness is 50");
                AssertEqual(50, p.AuditReasoningFit, "default AuditReasoningFit is 50");
                AssertEqual(50, p.MechanicalThroughput, "default MechanicalThroughput is 50");
                AssertEqual(50, p.Cost, "default Cost is 50");
                return Task.CompletedTask;
            });

            await RunTest("ModelCapabilityProfile_Setters_ClampToZeroHundred", () =>
            {
                ModelCapabilityProfile p = new ModelCapabilityProfile();
                p.TelemetryRichness = 150;
                p.AuditReasoningFit = -10;
                p.MechanicalThroughput = 100;
                p.Cost = 0;
                AssertEqual(100, p.TelemetryRichness, "over-max TelemetryRichness clamps to 100");
                AssertEqual(0, p.AuditReasoningFit, "under-min AuditReasoningFit clamps to 0");
                AssertEqual(100, p.MechanicalThroughput, "boundary 100 is kept");
                AssertEqual(0, p.Cost, "boundary 0 is kept");

                p.AuditReasoningFit = 73;
                AssertEqual(73, p.AuditReasoningFit, "an in-range value passes through unchanged");
                return Task.CompletedTask;
            });

            await RunTest("ModelCapabilityProfile_GetDimensionScore_IsCaseInsensitive_AndMinusOneOnUnknown", () =>
            {
                ModelCapabilityProfile p = Profile(20, 30);
                p.TelemetryRichness = 10;
                p.Cost = 40;

                AssertEqual(10, p.GetDimensionScore("TelemetryRichness"), "telemetry dimension resolves");
                AssertEqual(20, p.GetDimensionScore("auditreasoningfit"), "audit dimension resolves case-insensitively");
                AssertEqual(30, p.GetDimensionScore("MechanicalThroughput"), "throughput dimension resolves");
                AssertEqual(40, p.GetDimensionScore("cost"), "cost dimension resolves case-insensitively");
                AssertEqual(-1, p.GetDimensionScore("NotADimension"), "an unknown dimension returns -1");
                AssertEqual(-1, p.GetDimensionScore(null), "a null dimension returns -1");
                AssertEqual(-1, p.GetDimensionScore("   "), "a whitespace dimension returns -1");
                return Task.CompletedTask;
            });

            // --- ModelTierSettings config dictionaries: defaults + null reset ---

            await RunTest("ModelTierSettings_ModelCapabilityProfiles_DefaultsAndNullReset", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertTrue(defaults.ModelCapabilityProfiles.ContainsKey("claude-opus-4-7"), "default profiles include a seeded high-tier model");
                AssertTrue(defaults.ModelCapabilityProfiles.ContainsKey("opencode-go/kimi-k2.7-code"), "default profiles include a seeded mid-tier model");

                ModelTierSettings custom = new ModelTierSettings();
                custom.ModelCapabilityProfiles = new Dictionary<string, ModelCapabilityProfile>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "house-model", Profile(10, 10) }
                };
                AssertTrue(custom.ModelCapabilityProfiles.ContainsKey("house-model"), "custom profiles replace the defaults");
                AssertFalse(custom.ModelCapabilityProfiles.ContainsKey("claude-opus-4-7"), "default seeds do not leak into a custom profile map");

                custom.ModelCapabilityProfiles = null!;
                AssertTrue(custom.ModelCapabilityProfiles.ContainsKey("claude-opus-4-7"), "null setter restores the built-in default profiles");
                return Task.CompletedTask;
            });

            await RunTest("ModelTierSettings_CapabilityHintDimensionMap_DefaultsAndNullReset", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertEqual("AuditReasoningFit", defaults.CapabilityHintDimensionMap["audit"], "audit maps to AuditReasoningFit by default");
                AssertEqual("AuditReasoningFit", defaults.CapabilityHintDimensionMap["reasoning-heavy"], "reasoning-heavy maps to AuditReasoningFit by default");
                AssertEqual("MechanicalThroughput", defaults.CapabilityHintDimensionMap["mechanical"], "mechanical maps to MechanicalThroughput by default");
                AssertEqual("MechanicalThroughput", defaults.CapabilityHintDimensionMap["doc-only"], "doc-only maps to MechanicalThroughput by default");

                ModelTierSettings custom = new ModelTierSettings();
                custom.CapabilityHintDimensionMap = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "audit", "Cost" }
                };
                AssertEqual("Cost", custom.CapabilityHintDimensionMap["audit"], "operators can remap a hint to a different dimension");
                AssertFalse(custom.CapabilityHintDimensionMap.ContainsKey("mechanical"), "a custom map replaces the defaults wholesale");

                custom.CapabilityHintDimensionMap = null!;
                AssertEqual("MechanicalThroughput", custom.CapabilityHintDimensionMap["mechanical"], "null setter restores the built-in default hint map");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_RemappedHintDimension_FollowsConfig", () =>
            {
                // Operators retune routing without code: remap the audit hint to MechanicalThroughput,
                // and the audit hint must now pick the highest-throughput model (composer) instead of
                // the highest-audit model (sonnet) -- proving the dimension is read from config by key.
                ModelTierSettings settings = SettingsWith(MidProfiles());
                settings.CapabilityHintDimensionMap = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "audit", "MechanicalThroughput" }
                };
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("gemini-3.5-pro")
                };

                string? selected = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, DefaultMidOrder(), settings, "audit");
                AssertEqual("composer-2.5", selected, "remapping audit to MechanicalThroughput makes the audit hint pick the throughput leader");
                return Task.CompletedTask;
            });
        }
    }
}
