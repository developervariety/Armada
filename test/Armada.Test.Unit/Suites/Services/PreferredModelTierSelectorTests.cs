namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for PreferredModelTierSelector: tier recognition, model selection,
    /// persona eligibility filtering, upward fallback, and literal model passthrough.
    /// </summary>
    public class PreferredModelTierSelectorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Preferred Model Tier Selector";

        private static Captain MakeCaptain(string model, string? allowedPersonas = null)
        {
            Captain c = new Captain("test-captain");
            c.Model = model;
            c.AllowedPersonas = allowedPersonas;
            c.State = CaptainStateEnum.Idle;
            return c;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("IsTierSelector_LowMidHigh_ReturnsTrue", () =>
            {
                AssertTrue(PreferredModelTierSelector.IsTierSelector("low"), "low should be a tier selector");
                AssertTrue(PreferredModelTierSelector.IsTierSelector("mid"), "mid should be a tier selector");
                AssertTrue(PreferredModelTierSelector.IsTierSelector("high"), "high should be a tier selector");
                return Task.CompletedTask;
            });

            await RunTest("IsTierSelector_CaseInsensitive_ReturnsTrue", () =>
            {
                AssertTrue(PreferredModelTierSelector.IsTierSelector("Low"), "Low (title case) should be a tier selector");
                AssertTrue(PreferredModelTierSelector.IsTierSelector("MID"), "MID (upper case) should be a tier selector");
                AssertTrue(PreferredModelTierSelector.IsTierSelector("High"), "High (title case) should be a tier selector");
                return Task.CompletedTask;
            });

            await RunTest("IsTierSelector_Aliases_ReturnsTrue", () =>
            {
                AssertTrue(PreferredModelTierSelector.IsTierSelector("quick"), "quick alias should be recognized");
                AssertTrue(PreferredModelTierSelector.IsTierSelector("medium"), "medium alias should be recognized");
                return Task.CompletedTask;
            });

            await RunTest("IsTierSelector_LiteralModelName_ReturnsFalse", () =>
            {
                AssertFalse(PreferredModelTierSelector.IsTierSelector("claude-opus-4-7"), "literal model name should not be a tier selector");
                AssertFalse(PreferredModelTierSelector.IsTierSelector("zyloo/gpt-5.6-luna"), "literal model name should not be a tier selector");
                AssertFalse(PreferredModelTierSelector.IsTierSelector("composer-2.5"), "literal model name should not be a tier selector");
                return Task.CompletedTask;
            });

            await RunTest("IsTierSelector_NullOrEmpty_ReturnsFalse", () =>
            {
                AssertFalse(PreferredModelTierSelector.IsTierSelector(null), "null should not be a tier selector");
                AssertFalse(PreferredModelTierSelector.IsTierSelector(""), "empty string should not be a tier selector");
                AssertFalse(PreferredModelTierSelector.IsTierSelector("   "), "whitespace should not be a tier selector");
                return Task.CompletedTask;
            });

            await RunTest("NormalizeTier_Aliases_MapToCanonical", () =>
            {
                AssertEqual(PreferredModelTierSelector.LowTier, PreferredModelTierSelector.NormalizeTier("quick"), "quick should normalize to low");
                AssertEqual(PreferredModelTierSelector.MidTier, PreferredModelTierSelector.NormalizeTier("medium"), "medium should normalize to mid");
                return Task.CompletedTask;
            });

            await RunTest("GetTierAndAboveModels_LowTier_IncludesAllTiers", () =>
            {
                IReadOnlyList<string> models = PreferredModelTierSelector.GetTierAndAboveModels("low");
                AssertTrue(models.Count > 0, "Should have models in low tier and above");
                bool hasLow = false;
                bool hasMid = false;
                bool hasHigh = false;
                foreach (string m in models)
                {
                    if (m == "opencode-go/deepseek-v4-flash") hasLow = true;
                    if (m == "zyloo/claude-opus-4-7") hasMid = true;
                    if (m == "zyloo/claude-fable-5") hasHigh = true;
                }
                AssertTrue(hasLow, "Low tier model opencode-go/deepseek-v4-flash should be included");
                AssertTrue(hasMid, "Mid tier model zyloo/claude-opus-4-7 should be included");
                AssertTrue(hasHigh, "High tier model zyloo/claude-fable-5 should be included");
                return Task.CompletedTask;
            });

            await RunTest("GetTierAndAboveModels_HighTier_IncludesOnlyHigh", () =>
            {
                IReadOnlyList<string> lowModels = PreferredModelTierSelector.GetTierModels("low");
                IReadOnlyList<string> midModels = PreferredModelTierSelector.GetTierModels("mid");
                IReadOnlyList<string> highModels = PreferredModelTierSelector.GetTierAndAboveModels("high");

                foreach (string m in lowModels)
                {
                    bool found = false;
                    foreach (string hm in highModels) { if (hm == m) { found = true; break; } }
                    AssertFalse(found, "Low tier model " + m + " should NOT be in high-and-above");
                }
                foreach (string m in midModels)
                {
                    bool found = false;
                    foreach (string hm in highModels) { if (hm == m) { found = true; break; } }
                    AssertFalse(found, "Mid tier model " + m + " should NOT be in high-and-above");
                }
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_PreferenceOrderSelectsFirstListed", () =>
            {
                // The default mid-tier ranking leads with the Zyloo Opus captain, then the native
                // Opus entry. With several models idle, the first listed one wins.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("grok-4.5"),
                    MakeCaptain("zyloo/claude-opus-4-7")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0, null, defaultOrder);
                AssertNotNull(selected, "Should select a model when mid-tier captains are available");
                AssertEqual("zyloo/claude-opus-4-7", selected, "Should prefer the first listed mid-tier model");
                return Task.CompletedTask;
            });

            await RunTest("ZylooQualifiedModels_ClassifyIntoConfiguredTiers", () =>
            {
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("opencode-go/deepseek-v4-flash"), "opencode-go DeepSeek Flash must participate in low-tier routing");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/claude-opus-4-7"), "Zyloo Opus 4.7 must participate in mid-tier routing");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/claude-opus-4-8"), "Zyloo Opus 4.8 must participate in mid-tier routing");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna"), "Zyloo GPT 5.6 Luna must participate in mid-tier routing");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-sol"), "Zyloo GPT 5.6 Sol must participate in high-tier routing");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("zyloo/claude-opus-5"), "Zyloo Opus 5 must participate in high-tier routing");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("zyloo/claude-fable-5"), "Zyloo Fable must participate in high-tier routing");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_PrefersZylooOpusPrimary", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("zyloo/claude-opus-4-7")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0, null, defaultOrder);

                AssertEqual("zyloo/claude-opus-4-7", selected, "The Zyloo Opus primary must be preferred over the other idle mid captain");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_DuplicatedCaptains_PreferenceOrderWins", () =>
            {
                // Many composer captains and one Zyloo Opus primary. The default mid ranking lists
                // zyloo/claude-opus-4-7 ahead of composer, so the primary wins even though it has
                // fewer idle instances -- preference is not a popularity contest.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("grok-4.5"),
                    MakeCaptain("zyloo/claude-opus-4-7")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0, null, defaultOrder);

                AssertEqual("zyloo/claude-opus-4-7", selected, "Preference order should select the Zyloo Opus primary ahead of the duplicated composer models");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_FiltersByPersonaEligibility", () =>
            {
                // Two high-tier captains, only one allows the Judge specialist persona.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/claude-opus-5", "[\"Worker\"]"),
                    MakeCaptain("zyloo/gpt-5.6-sol", "[\"Worker\",\"Judge\"]")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, "Judge", _ => 0);
                AssertNotNull(selected, "Should find a model eligible for Judge persona");
                AssertEqual("zyloo/gpt-5.6-sol", selected, "Only the zyloo/gpt-5.6-sol captain allows Judge persona");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_UpgradesLowToMid_WhenLowHasNoEligible", () =>
            {
                // No low-tier captains, but mid-tier captains are available
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("grok-4.5"),
                    MakeCaptain("composer-2.5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("low", captains, null, _ => 0);
                AssertNotNull(selected, "Should upgrade to mid when low has no eligible captains");

                IReadOnlyList<string> midModels = PreferredModelTierSelector.GetTierModels("mid");
                bool isMidModel = false;
                foreach (string m in midModels) { if (m == selected) { isMidModel = true; break; } }
                AssertTrue(isMidModel, "Upgraded selection should be a mid-tier model");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_UpgradesMidToHigh_WhenMidHasNoEligible", () =>
            {
                // No mid-tier captains, but high-tier captains are available
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/claude-fable-5"),
                    MakeCaptain("zyloo/claude-opus-5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Should upgrade to high when mid has no eligible captains");

                IReadOnlyList<string> highModels = PreferredModelTierSelector.GetTierModels("high");
                bool isHighModel = false;
                foreach (string m in highModels) { if (m == selected) { isHighModel = true; break; } }
                AssertTrue(isHighModel, "Upgraded selection should be a high-tier model");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_HighNeverDowngrades", () =>
            {
                // Only low and mid captains available; high-tier request should return null
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("opencode-go/deepseek-v4-flash"),
                    MakeCaptain("composer-2.5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, null, _ => 0);
                AssertNull(selected, "High tier should never downgrade -- should return null when no high captains available");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_High_SelectsCaptainWithZylooOpus5", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/claude-opus-5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, null, _ => 0);
                AssertNotNull(selected, "High tier should match the Zyloo Opus 5 captain");
                AssertEqual("zyloo/claude-opus-5", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_SelectsZylooGpt56Luna", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/gpt-5.6-luna")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Mid tier should match the Zyloo GPT 5.6 Luna captain");
                AssertEqual("zyloo/gpt-5.6-luna", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_SelectsZylooOpus48", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/claude-opus-4-8")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Mid tier should match the Zyloo Opus 4.8 captain");
                AssertEqual("zyloo/claude-opus-4-8", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_SelectsComposer25", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Mid tier should match the composer-2.5 captain");
                AssertEqual("composer-2.5", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_High_DoesNotFuzzyMatchUnlistedVariants", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/gpt-5.6-sol-max")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, null, _ => 0);
                AssertNull(selected, "High tier should not select an unlisted variant of a high model");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_DoesNotFuzzyMatchUnlistedVariants", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/gpt-5.6-luna-max"),
                    MakeCaptain("zyloo/claude-opus-4-8-max")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNull(selected, "Mid tier should not select unlisted variants of mid models");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_ReturnsNull_WhenNoEligibleCaptains", () =>
            {
                List<Captain> captains = new List<Captain>();
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNull(selected, "Should return null when no captains are available");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_LiteralModelCaptain_NotFoundByTier", () =>
            {
                // Captain has a literal model name that is not in any tier
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("some-custom-model")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNull(selected, "Captain with a non-tier model should not be selected by tier dispatch");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_NullPersona_AcceptsAllCaptains", () =>
            {
                // Captain with AllowedPersonas restriction should still be picked when persona is null
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/gpt-5.6-luna", "[\"Worker\"]")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Null persona should accept captains with any AllowedPersonas");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_CaptainNullAllowedPersonas_AcceptsAnyPersona", () =>
            {
                // Captain with null AllowedPersonas should be eligible for any persona. Judge is a
                // specialist persona that resolves on high tier, so the captain carries a high model.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-opus-4-7", null)
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, "Judge", _ => 0);
                AssertNotNull(selected, "Captain with null AllowedPersonas should serve any persona including Judge");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_CuratedAndCanonicalFamilies_MapToExpectedTier", () =>
            {
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7"), "canonical opus is high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-5"), "canonical opus bump is high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-fable-5"), "canonical fable is high");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/claude-opus-4-7"), "curated Zyloo opus 4-7 is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna"), "curated Zyloo luna is mid");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("opencode-go/deepseek-v4-flash"), "curated opencode-go deepseek is low");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_OpencodeRegisteredModels_MapToCuratedTier", () =>
            {
                // The opencode-* model names are slash-prefixed (opencode/, opencode-go/) so
                // none of them match a bare family fallback. The low-tier curated array holds
                // exactly one opencode model, and only that exact entry counts -- a sibling
                // opencode/deepseek-v4-flash with the other prefix is NOT registered and must
                // stay unclassified. This test fails if a future edit drops the entry from
                // _LowModels or adds an unlisted sibling to the curated arrays.
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("opencode-go/deepseek-v4-flash"), "opencode-go/deepseek-v4-flash is curated low");
                AssertNull(PreferredModelTierSelector.ClassifyModel("opencode/deepseek-v4-flash"), "opencode/deepseek-v4-flash is not registered -- only the opencode-go/ curated entry counts");

                // Critical ordering guard: opencode-go/deepseek-v4-flash contains "deepseek" but
                // does NOT start with a bare family token, so no fallback catches it. Only the
                // curated _LowModels entry can classify it; an unlisted sibling variant is
                // unregistered and must NOT be absorbed.
                AssertNull(PreferredModelTierSelector.ClassifyModel("opencode-go/deepseek-v4-flash-lite"), "an unlisted sibling variant is not registered");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_OpencodeUnregisteredVariant_IsNotRecognized", () =>
            {
                // A sibling opencode model that was NOT registered must stay null: the slash
                // prefix keeps it out of the bare family fallbacks. Proves the curated
                // registration -- not a pattern -- is what makes opencode-go/deepseek-v4-flash count.
                AssertNull(PreferredModelTierSelector.ClassifyModel("opencode/deepseek-v4-flash"), "unregistered opencode deepseek prefix is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("opencode-go/deepseek-v5"), "unregistered opencode deepseek variant is not classified");
                return Task.CompletedTask;
            });

            await RunTest("GetTierModels_ContainsRegisteredOpencodeModels", () =>
            {
                IReadOnlyList<string> lowModels = PreferredModelTierSelector.GetTierModels("low");
                IReadOnlyList<string> midModels = PreferredModelTierSelector.GetTierModels("mid");
                AssertTrue(lowModels.Contains("opencode-go/deepseek-v4-flash"), "low tier must list opencode-go/deepseek-v4-flash");
                AssertFalse(midModels.Contains("opencode-go/deepseek-v4-flash"), "the low opencode model must not leak into mid tier");
                return Task.CompletedTask;
            });

            await RunTest("ModelMatchesTierOrAbove_UpwardFallback_SatisfiesLowPin", () =>
            {
                // A mid model must satisfy a low-tier pin (upward fallback) but a low
                // opencode model must NOT satisfy a mid-tier pin.
                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("zyloo/claude-opus-4-7", "low"), "mid model satisfies low pin via upward fallback");
                AssertFalse(PreferredModelTierSelector.ModelMatchesTierOrAbove("opencode-go/deepseek-v4-flash", "mid"), "low opencode model must not satisfy a mid pin");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_FutureVersionBumps_AutoRegisterByFamily", () =>
            {
                // The bug this guards: an Opus version bump (4-7 -> 4-8 -> 5) must classify high
                // WITHOUT being added to the curated array, and a Fable bump registers the same way.
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-8"), "opus 4-8 auto-registers high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-5"), "opus 5 auto-registers high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-fable-6"), "fable bump auto-registers high");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("gemini-4.0-pro"), "gemini pro bump auto-registers mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("composer-3"), "composer bump auto-registers mid");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_VariantSuffixes_AreNotRecognized", () =>
            {
                // Anchored family patterns must not absorb unlisted suffix variants.
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-sol-preview"), "unlisted sol preview is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna-preview"), "unlisted luna preview is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/claude-opus-5-preview"), "unlisted opus preview is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("some-custom-model"), "unknown model is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel(null), "null is not classified");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_High_AutoRegistersUpgradedOpusCaptain", () =>
            {
                // Regression: claude-opus-4-8 captains were invisible to a "high" tier request
                // because the curated high list only knew claude-opus-4-7.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-opus-4-8", "[\"MemoryConsolidator\"]")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, "MemoryConsolidator", _ => 0);
                AssertEqual("claude-opus-4-8", selected, "Upgraded Opus captain should be selectable for a high-tier MemoryConsolidator mission");
                return Task.CompletedTask;
            });

            await RunTest("ModelMatchesTierOrAbove_RespectsUpwardChain", () =>
            {
                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("claude-opus-4-8", "high"), "opus 4-8 satisfies high");
                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("claude-opus-4-8", "mid"), "high model satisfies a mid pin (upward chain)");
                AssertFalse(PreferredModelTierSelector.ModelMatchesTierOrAbove("zyloo/claude-opus-4-7", "high"), "mid model does not satisfy a high pin");
                AssertFalse(PreferredModelTierSelector.ModelMatchesTierOrAbove("some-custom-model", "low"), "unclassified model satisfies no tier pin");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_NonSpecialistWithIdleMid_ReturnsMidNotHigh", () =>
            {
                // A mid AND a high captain are idle. A non-specialist persona must take the mid
                // captain and leave the high captain free.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0);
                AssertEqual("composer-2.5", selected, "Non-specialist work should take the idle mid captain, not the high one");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_NonSpecialistAllMidLowBusy_FallsUpToHigh", () =>
            {
                // No mid or low captains are idle -- only a high one. High is the last resort, so
                // a non-specialist mission may use it rather than stay pending.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0);
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel(selected), "High is selected as a last resort when no mid/low captain is idle");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_NonSpecialistMid_TriesLowBeforeHigh", () =>
            {
                // A low AND a high captain are idle but no mid. The non-specialist order is
                // [mid, low, high], so low must win over high.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("opencode-go/deepseek-v4-flash"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0);
                AssertEqual("opencode-go/deepseek-v4-flash", selected, "A non-specialist mid request must try low before high");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_SpecialistPersona_ReturnsHigh", () =>
            {
                // A mid AND a high captain are idle. A specialist persona is reserved for high.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Judge", _ => 0);
                AssertEqual("claude-opus-4-7", selected, "Specialist persona must resolve to the high-tier captain only");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_PrimaryFirst_WhenIdle", () =>
            {
                // The default mid ranking leads with the Zyloo Opus captain. When it is idle it
                // must win over the lower-ranked mid-tier captains.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("grok-4.5"),
                    MakeCaptain("zyloo/claude-opus-4-7")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaultOrder);
                AssertEqual("zyloo/claude-opus-4-7", selected, "Idle primary captain should be selected first for Worker mid work");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_FallsBackToNextRanked_WhenPrimaryBusy", () =>
            {
                // The primary (Zyloo Opus 4.7) captains are busy and absent from the idle list, so the
                // selector must fall to the next ranked mid model that has an idle captain.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("grok-4.5"),
                    MakeCaptain("zyloo/claude-opus-4-8")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaultOrder);
                AssertEqual("zyloo/claude-opus-4-8", selected, "Should fall back to the next ranked Opus captain when the primary Zyloo Opus captain is busy");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_FallsBackToComposer_WhenHigherRankedBusy", () =>
            {
                // Only composer and grok are idle. Preference order lists composer ahead of grok,
                // so composer wins even though grok appears first in the idle captain list.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("grok-4.5"),
                    MakeCaptain("composer-2.5")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaultOrder);
                AssertEqual("composer-2.5", selected, "Should fall back to composer when every higher-ranked mid captain is busy");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_ConfigurablePreferenceOrder_OverridesDefault", () =>
            {
                // Operator-configurable preference order flips the default so composer is first.
                Dictionary<string, List<string>> customOrder = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "mid", new List<string> { "composer-2.5", "zyloo/claude-opus-4-7", "zyloo/gpt-5.6-luna" } }
                };

                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/gpt-5.6-luna"),
                    MakeCaptain("zyloo/claude-opus-4-7"),
                    MakeCaptain("composer-2.5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, customOrder);
                AssertEqual("composer-2.5", selected, "Custom preference order should place composer ahead of the Zyloo mid models");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_UnknownPreferenceModel_SkipsToNext", () =>
            {
                // A preference list can contain models that are not currently idle. Those are
                // skipped and the first idle preferred model is selected.
                Dictionary<string, List<string>> customOrder = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "mid", new List<string> { "zyloo/claude-opus-4-8", "zyloo/gpt-5.6-luna", "composer-2.5" } }
                };

                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, customOrder);
                AssertEqual("composer-2.5", selected, "Should skip missing Opus and Luna captains and land on composer");
                return Task.CompletedTask;
            });

            await RunTest("ModelTierSettings_WithinTierPreferenceOrder_DefaultsAndRestores", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertTrue(defaults.WithinTierPreferenceOrder.ContainsKey("mid"), "default preference order contains mid tier");
                List<string> midOrder = defaults.WithinTierPreferenceOrder["mid"];
                AssertEqual(5, midOrder.Count, "default mid preference order lists the five current mid-tier models");
                AssertEqual("zyloo/claude-opus-4-7", midOrder[0], "starts with the Zyloo Opus 4.7 captain, the designated primary");
                AssertEqual("zyloo/claude-opus-4-8", midOrder[1], "Zyloo Opus 4.8 follows the primary");
                AssertEqual("zyloo/gpt-5.6-luna", midOrder[2], "Zyloo GPT 5.6 Luna is third");
                AssertEqual("composer-2.5", midOrder[3], "composer-2.5 is fourth");
                AssertEqual("grok-4.5", midOrder[4], "grok-4.5 closes the list");

                ModelTierSettings custom = new ModelTierSettings();
                custom.WithinTierPreferenceOrder = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "low", new List<string> { "opencode-go/deepseek-v4-flash" } }
                };
                AssertFalse(custom.WithinTierPreferenceOrder.ContainsKey("mid"), "custom preference order replaces the default mid entry");
                AssertTrue(custom.WithinTierPreferenceOrder.ContainsKey("low"), "custom preference order contains the operator-supplied low entry");

                custom.WithinTierPreferenceOrder = null!;
                AssertTrue(custom.WithinTierPreferenceOrder.ContainsKey("mid"), "null setter restores the built-in default preference order");
                return Task.CompletedTask;
            });

            await RunTest("IsSpecialistPersona_ConfigurableViaSettings", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertTrue(defaults.IsSpecialistPersona("Judge"), "Judge is a default specialist");
                AssertTrue(defaults.IsSpecialistPersona("memoryconsolidator"), "specialist match is case-insensitive");
                AssertFalse(defaults.IsSpecialistPersona("Worker"), "Worker is not a specialist");
                AssertFalse(defaults.IsSpecialistPersona(null), "null persona is not a specialist");
                AssertEqual(10, defaults.SpecialistPersonas.Count, "default specialist set has the 10 reserved personas");

                ModelTierSettings custom = new ModelTierSettings();
                custom.SpecialistPersonas = new List<string> { "Curator" };
                AssertTrue(custom.IsSpecialistPersona("Curator"), "custom persona is reclassified as a specialist");
                AssertFalse(custom.IsSpecialistPersona("Judge"), "Judge is no longer a specialist under a custom set");
                AssertTrue(PreferredModelTierSelector.RequiresHighTier("Curator", custom.SpecialistPersonas), "selector honors the custom specialist set");
                AssertFalse(PreferredModelTierSelector.RequiresHighTier("Judge", custom.SpecialistPersonas), "selector excludes Judge under the custom set");

                custom.SpecialistPersonas = null!;
                AssertTrue(custom.IsSpecialistPersona("Judge"), "null setter restores the built-in default specialists");
                return Task.CompletedTask;
            });

            await RunTest("EnforceHighTierForPersona_NonSpecialist_PassesTierThroughUnchanged", () =>
            {
                // Create-time enforcement must NOT upgrade non-specialist work. A Worker mission
                // that asked for mid keeps mid; the last-resort fall-up happens later at dispatch.
                AssertEqual("mid", PreferredModelTierSelector.EnforceHighTierForPersona("mid", "Worker"), "non-specialist mid request is preserved at create time");
                AssertEqual("low", PreferredModelTierSelector.EnforceHighTierForPersona("low", "Worker"), "non-specialist low request is preserved at create time");
                AssertNull(PreferredModelTierSelector.EnforceHighTierForPersona(null, "Worker"), "non-specialist with no preferred model is left unset, not forced to high");
                AssertNull(PreferredModelTierSelector.EnforceHighTierForPersona(null, null), "null persona is non-specialist and is left unset");
                return Task.CompletedTask;
            });

            await RunTest("EnforceHighTierForPersona_Specialist_UpgradesBelowHighToHigh", () =>
            {
                // Specialist personas are reserved for high: any sub-high tier selector (or an
                // unset preferred model) is forced up to high at create time.
                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona("mid", "Judge"), "specialist mid request is upgraded to high");
                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona("low", "Architect"), "specialist low request is upgraded to high");
                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona(null, "TestEngineer"), "specialist with no preferred model defaults to high");
                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona("high", "Judge"), "specialist that already asked for high stays high");
                return Task.CompletedTask;
            });

            await RunTest("EnforceHighTierForPersona_SpecialistLiteralModel_PassesThroughUnchanged", () =>
            {
                // An operator-pinned literal model name is honored verbatim even for a specialist;
                // the runtime tier-fallback handles the case where no matching captain is idle.
                AssertEqual("zyloo/claude-opus-4-7", PreferredModelTierSelector.EnforceHighTierForPersona("zyloo/claude-opus-4-7", "Judge"), "specialist literal pin is not rewritten to a tier selector");
                return Task.CompletedTask;
            });

            await RunTest("EnforceHighTierForPersona_ConfigurableViaSettings", () =>
            {
                // Reclassifying personas through settings must flow through create-time enforcement,
                // not just the boolean predicate: a custom specialist is upgraded and a former
                // default specialist is no longer upgraded -- all without a code change.
                ModelTierSettings custom = new ModelTierSettings();
                custom.SpecialistPersonas = new List<string> { "Curator" };

                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona("mid", "Curator", custom.SpecialistPersonas), "custom specialist is upgraded to high at create time");
                AssertEqual("mid", PreferredModelTierSelector.EnforceHighTierForPersona("mid", "Judge", custom.SpecialistPersonas), "Judge is no longer a specialist under the custom set, so its tier is preserved");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_NonSpecialistLow_TriesMidBeforeHigh", () =>
            {
                // A mid AND a high captain are idle but no low. The non-specialist order for a low
                // request is [low, mid, high], so the mid captain must win over the high one.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("low", captains, "Worker", _ => 0);
                AssertEqual("composer-2.5", selected, "A non-specialist low request must try mid before falling up to high");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_NonSpecialistExplicitHigh_HonoredWithoutDowngrade", () =>
            {
                // A non-specialist that explicitly asks for high is honored: high is not silently
                // downgraded to the idle mid captain (the operator asked for high deliberately).
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, "Worker", _ => 0);
                AssertEqual("claude-opus-4-7", selected, "An explicit high request by a non-specialist resolves to the high captain, not the idle mid one");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_ConfigDrivenTierMembership_FollowsModelTierSettings", () =>
            {
                // Tier membership is sourced from ModelTierSettings, not hard-coded arrays.
                // The configured lists win over canonical family patterns and over default
                // tier assignments, so moving a model between tiers is a settings change.
                ModelTierSettings custom = new ModelTierSettings();
                custom.LowTierModels = new List<string> { "custom-low", "claude-opus-4-7" };
                custom.MidTierModels = new List<string> { "custom-mid", "opencode-go/deepseek-v4-flash" };
                custom.HighTierModels = new List<string> { "custom-high" };

                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("custom-low", custom), "custom low-tier model classifies low");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("custom-mid", custom), "custom mid-tier model classifies mid");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("custom-high", custom), "custom high-tier model classifies high");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("opencode-go/deepseek-v4-flash", custom), "a default low model moved to mid config classifies mid");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7", custom), "configured low-tier membership overrides the canonical opus high pattern");
                AssertNull(PreferredModelTierSelector.ClassifyModel("not-in-any-list-and-no-pattern-match", custom), "model not in custom lists and not matching a family pattern is not classified");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_Gpt56Sol_ExplicitEntryOnlyClassifiesHigh", () =>
            {
                // zyloo/gpt-5.6-sol must classify high through its explicit curated entry, not a fragile
                // regex or prefix fallback. Nearby variants that are not explicitly listed must remain
                // unclassified.
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-sol"), "zyloo/gpt-5.6-sol is explicitly high");
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-sol-max"), "no gpt prefix fallback absorbs variants");
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-sol-lite"), "no gpt prefix fallback absorbs sibling names");
                AssertNull(PreferredModelTierSelector.ClassifyModel("gpt-5.6-sol"), "bare gpt-5.6-sol is not curated and matches no family pattern");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_Gpt56Luna_HardensToMid", () =>
            {
                // Zyloo GPT 5.6 Luna must reliably resolve to the mid tier through its explicit
                // curated entry, while the bare form (which matches no family pattern) and unlisted
                // variants stay unclassified.
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna"), "zyloo gpt-5.6-luna is mid");
                AssertNull(PreferredModelTierSelector.ClassifyModel("gpt-5.6-luna"), "bare gpt-5.6-luna matches no family pattern and is not curated");
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna-max"), "an unlisted luna variant stays unclassified");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_SpecialistPersona_MidDispatch_ForcedHigh", () =>
            {
                // A mid-tier dispatch for a specialist persona must resolve to the high tier
                // even when an idle mid-tier captain is available.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "TestEngineer", _ => 0);
                AssertEqual("claude-opus-4-7", selected, "Specialist mid dispatch is forced to high-tier captain");
                return Task.CompletedTask;
            });

            await RunTest("ModelTierSettings_WithinTierPreferenceOrder_ZylooOpusFirstPreserved", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertTrue(defaults.WithinTierPreferenceOrder.ContainsKey("mid"), "default contains mid preference order");
                List<string> midOrder = defaults.WithinTierPreferenceOrder["mid"];
                AssertEqual("zyloo/claude-opus-4-7", midOrder[0], "default mid order starts with Zyloo Opus");
                AssertEqual("zyloo/claude-opus-4-8", midOrder[1], "Zyloo Opus 4.8 follows the primary");
                AssertEqual("zyloo/gpt-5.6-luna", midOrder[2], "Zyloo GPT 5.6 Luna follows the Opus block");
                AssertEqual("composer-2.5", midOrder[3], "composer-2.5 is fourth");
                AssertEqual("grok-4.5", midOrder[4], "grok-4.5 closes the list");

                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("grok-4.5"),
                    MakeCaptain("zyloo/claude-opus-4-8"),
                    MakeCaptain("zyloo/claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaults.WithinTierPreferenceOrder, defaults);
                AssertEqual("zyloo/claude-opus-4-7", selected, "Zyloo-Opus-first preference is preserved when all mid captains are idle");
                return Task.CompletedTask;
            });

            await RunTest("ModelTierSettings_LoadedSettingsFile_OverridesBuiltInRanking", () =>
            {
                // The deployed settings.json is the sole source of truth: a modelTier block in the
                // file must replace the built-in tier lists and ranking, not merge with them.
                string json = "{\"modelTier\":{"
                    + "\"midTierModels\":[\"composer-2.5\",\"zyloo/claude-opus-4-7\"],"
                    + "\"withinTierPreferenceOrder\":{\"mid\":[\"composer-2.5\",\"zyloo/claude-opus-4-7\"]}}}";
                JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                ArmadaSettings? loaded = JsonSerializer.Deserialize<ArmadaSettings>(json, options);

                AssertNotNull(loaded, "settings JSON must deserialize");

                ModelTierSettings fileTier = loaded!.ModelTier;
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("zyloo/claude-opus-4-7"),
                    MakeCaptain("composer-2.5")
                };

                string? builtIn = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, new ModelTierSettings().WithinTierPreferenceOrder, new ModelTierSettings());
                AssertEqual("zyloo/claude-opus-4-7", builtIn, "the built-in ranking prefers the Zyloo Opus captain");
                string? fromFile = PreferredModelTierSelector.SelectModel(
                    "mid", captains, "Worker", _ => 0, null, fileTier.WithinTierPreferenceOrder, fileTier);
                AssertEqual("composer-2.5", fromFile, "the loaded settings file must win over the built-in ranking");

                AssertEqual(2, fileTier.MidTierModels.Count, "the file's mid membership replaces the built-in list");
                AssertNull(
                    PreferredModelTierSelector.ClassifyModel("opencode-go/kimi-k3", fileTier),
                    "a model the file omits classifies to no tier -- the file does not merge with the built-in list");
                return Task.CompletedTask;
            });

            await RunTest("GetTierModels_CustomSettings_ReturnsConfiguredMembership", () =>
            {
                // The config-driven read path: GetTierModels must return the supplied settings'
                // lists verbatim, not the built-in defaults. This is the read-side proof that
                // tier membership is sourced from ModelTierSettings.
                ModelTierSettings custom = new ModelTierSettings();
                custom.LowTierModels = new List<string> { "alpha-low" };
                custom.MidTierModels = new List<string> { "beta-mid", "gamma-mid" };
                custom.HighTierModels = new List<string> { "delta-high" };

                IReadOnlyList<string> low = PreferredModelTierSelector.GetTierModels("low", custom);
                IReadOnlyList<string> mid = PreferredModelTierSelector.GetTierModels("mid", custom);
                IReadOnlyList<string> high = PreferredModelTierSelector.GetTierModels("high", custom);

                AssertEqual(1, low.Count, "custom low tier has exactly the one configured model");
                AssertEqual("alpha-low", low[0], "custom low tier returns the configured model");
                AssertEqual(2, mid.Count, "custom mid tier returns both configured models");
                AssertTrue(mid.Contains("beta-mid") && mid.Contains("gamma-mid"), "custom mid tier returns the configured members");
                AssertEqual(1, high.Count, "custom high tier has exactly the one configured model");
                AssertEqual("delta-high", high[0], "custom high tier returns the configured model");

                // The default membership must NOT leak through when custom settings are supplied.
                AssertFalse(low.Contains("opencode-go/deepseek-v4-flash"), "default low model must not appear under custom low settings");
                AssertFalse(high.Contains("zyloo/claude-fable-5"), "default high model must not appear under custom high settings");
                return Task.CompletedTask;
            });

            await RunTest("GetTierModels_EmptyConfiguredList_ReturnsEmpty", () =>
            {
                // An operator who clears a tier list gets an empty membership list back -- the
                // empty list is honored (it is not null, so the setter does not restore defaults).
                ModelTierSettings custom = new ModelTierSettings();
                custom.MidTierModels = new List<string>();

                IReadOnlyList<string> mid = PreferredModelTierSelector.GetTierModels("mid", custom);
                AssertEqual(0, mid.Count, "an explicitly emptied mid list returns no configured members");
                return Task.CompletedTask;
            });

            await RunTest("GetTierAndAboveModels_CustomSettings_MidComposesMidAndHigh", () =>
            {
                // mid-and-above must concatenate the configured mid and high lists (and exclude
                // low) when custom settings are supplied -- the upward chain is config-driven too.
                ModelTierSettings custom = new ModelTierSettings();
                custom.LowTierModels = new List<string> { "alpha-low" };
                custom.MidTierModels = new List<string> { "beta-mid" };
                custom.HighTierModels = new List<string> { "delta-high" };

                IReadOnlyList<string> midAndAbove = PreferredModelTierSelector.GetTierAndAboveModels("mid", custom);
                AssertTrue(midAndAbove.Contains("beta-mid"), "mid-and-above includes the configured mid model");
                AssertTrue(midAndAbove.Contains("delta-high"), "mid-and-above includes the configured high model");
                AssertFalse(midAndAbove.Contains("alpha-low"), "mid-and-above must exclude the configured low model");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_ListPrecedence_HighWinsOverLowWhenModelInBoth", () =>
            {
                // ClassifyModel checks High, then Mid, then Low. A (misconfigured) model present
                // in more than one list resolves to the highest list it appears in -- this pins
                // the documented check order so a future reorder is caught.
                ModelTierSettings custom = new ModelTierSettings();
                custom.LowTierModels = new List<string> { "dual-listed" };
                custom.MidTierModels = new List<string> { "dual-listed" };
                custom.HighTierModels = new List<string> { "dual-listed" };

                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("dual-listed", custom), "a model in multiple lists classifies into the highest (high checked first)");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_EmptyHighList_OverridesBuiltInFamilyInference", () =>
            {
                ModelTierSettings custom = new ModelTierSettings();
                custom.HighTierModels = new List<string>();

                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/claude-fable-5", custom), "explicit-only zyloo/claude-fable-5 is unclassified once the high list is emptied");
                AssertNull(PreferredModelTierSelector.ClassifyModel("claude-opus-4-7", custom), "an empty configured high list must override the built-in Opus family inference");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_EmptyMidList_OverridesBuiltInFamilyInference", () =>
            {
                ModelTierSettings custom = new ModelTierSettings();
                custom.MidTierModels = new List<string>();

                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/claude-opus-4-7", custom), "an empty configured mid list must override the built-in mid membership");
                AssertNull(PreferredModelTierSelector.ClassifyModel("composer-2.5", custom), "an empty configured mid list must override the built-in Composer family inference");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_LunaCuratedEntry_AnchoringBoundaries", () =>
            {
                // The Zyloo GPT 5.6 Luna entry is exact-match only: only the curated string
                // classifies mid. Adjacent or suffixed variants (bare luna, luna-max, luna-2)
                // must NOT be absorbed -- they match no family pattern and are not curated.
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna"), "the exact curated luna entry is mid");
                AssertNull(PreferredModelTierSelector.ClassifyModel("gpt-5.6-luna"), "bare luna is NOT the curated entry -- it stays unclassified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna-max"), "a suffixed luna variant is NOT absorbed");
                AssertNull(PreferredModelTierSelector.ClassifyModel("zyloo/gpt-5.6-luna-2"), "a versioned luna variant is NOT absorbed");
                return Task.CompletedTask;
            });

            await RunTest("ModelMatchesTierOrAbove_CustomSettings_FollowsConfiguredMembership", () =>
            {
                // The pin-validation upward chain is config-driven: a model reclassified to high by
                // settings now satisfies a low pin (upward fallback) and a high pin, while a model
                // moved down to low no longer satisfies a mid pin.
                ModelTierSettings custom = new ModelTierSettings();
                custom.LowTierModels = new List<string> { "claude-opus-4-7" };
                custom.HighTierModels = new List<string> { "opencode-go/deepseek-v4-flash" };

                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("opencode-go/deepseek-v4-flash", "low", custom), "a model promoted to high via config satisfies a low pin");
                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("opencode-go/deepseek-v4-flash", "high", custom), "a model promoted to high via config satisfies a high pin");
                AssertFalse(PreferredModelTierSelector.ModelMatchesTierOrAbove("claude-opus-4-7", "mid", custom), "a model demoted to low via config no longer satisfies a mid pin");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_CustomSettings_SelectsModelClassifiedByConfigOnly", () =>
            {
                // A captain whose model is unknown to the defaults (would classify null and be
                // unselectable) becomes selectable for a mid request once config adds it to the
                // mid list -- proving SelectModel threads modelTierSettings through to ClassifyModel.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("house-model-x")
                };

                string? withoutConfig = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0);
                AssertNull(withoutConfig, "an unclassified model is not selectable for a mid request under defaults");

                ModelTierSettings custom = new ModelTierSettings();
                custom.MidTierModels = new List<string> { "house-model-x" };
                string? withConfig = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, null, custom);
                AssertEqual("house-model-x", withConfig, "config adding the model to the mid list makes its captain selectable for mid work");
                return Task.CompletedTask;
            });

            await RunTest("NormalizeTier_UnknownSelector_Throws", () =>
            {
                // Defensive contract: NormalizeTier must reject values that are neither canonical
                // tiers nor known aliases rather than silently coercing them.
                AssertThrows<System.ArgumentException>(() => PreferredModelTierSelector.NormalizeTier("ultra"), "unknown tier selector throws");
                AssertThrows<System.ArgumentException>(() => PreferredModelTierSelector.NormalizeTier(""), "empty tier selector throws");
                return Task.CompletedTask;
            });

            await RunTest("CaptainSatisfiesPreferredRouting_TierPin_HonorsConfiguredMembership", () =>
            {
                // The MissionService hard-pin/stage-pin gate must honor config-driven tier
                // membership: a captain whose model the defaults treat as mid is rejected for a
                // high pin, but accepted once config promotes that model to high.
                Captain captain = MakeCaptain("zyloo/claude-opus-4-7", "[\"Worker\"]");

                AssertFalse(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "high"), "a default mid-tier captain does not satisfy a high tier pin");

                ModelTierSettings custom = new ModelTierSettings();
                custom.HighTierModels = new List<string> { "zyloo/claude-opus-4-7" };
                AssertTrue(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "high", custom), "config promoting the model to high lets the captain satisfy a high tier pin");
                return Task.CompletedTask;
            });

            await RunTest("CaptainSatisfiesPreferredRouting_LiteralPinAndPersona_AreEnforced", () =>
            {
                // Literal model pins must match exactly (tier config is irrelevant), and the
                // persona allow-list is enforced independently of the model pin.
                Captain captain = MakeCaptain("claude-opus-4-7", "[\"Worker\",\"Judge\"]");

                AssertTrue(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "claude-opus-4-7"), "exact literal model pin is satisfied");
                AssertFalse(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "zyloo/gpt-5.6-sol"), "a non-matching literal model pin is rejected");
                AssertTrue(MissionService.CaptainSatisfiesPreferredRouting(captain, "Judge", null), "an allowed persona with no model pin is satisfied");
                AssertFalse(MissionService.CaptainSatisfiesPreferredRouting(captain, "Architect", null), "a persona absent from the allow-list is rejected");
                return Task.CompletedTask;
            });
        }
    }
}
