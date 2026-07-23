namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
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
                AssertFalse(PreferredModelTierSelector.IsTierSelector("claude-sonnet-4-6"), "literal model name should not be a tier selector");
                AssertFalse(PreferredModelTierSelector.IsTierSelector("claude-opus-4-7"), "literal model name should not be a tier selector");
                AssertFalse(PreferredModelTierSelector.IsTierSelector("gpt-5.5"), "literal model name should not be a tier selector");
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
                    if (m == "kimi-k2.5") hasLow = true;
                    if (m == "gpt-5.3-codex") hasMid = true;
                    if (m == "claude-opus-4-7") hasHigh = true;
                }
                AssertTrue(hasLow, "Low tier model kimi-k2.5 should be included");
                AssertTrue(hasMid, "Mid tier model gpt-5.3-codex should be included");
                AssertTrue(hasHigh, "High tier model claude-opus-4-7 should be included");
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
                // The default mid-tier preference order is K2.7, sonnet, composer. With all
                // three models idle, the selector must pick the first listed preference.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0, null, defaultOrder);
                AssertNotNull(selected, "Should select a model when mid-tier captains are available");
                AssertEqual("opencode-go/kimi-k2.7-code", selected, "Should prefer the first listed mid-tier model");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_DuplicatedCaptains_PreferenceOrderWins", () =>
            {
                // Many composer captains and one sonnet captain. The default mid preference
                // order lists sonnet before composer, so sonnet wins even though it has
                // fewer idle instances -- preference is not a popularity contest.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("gemini-3.5-pro")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0, null, defaultOrder);

                AssertEqual("claude-sonnet-4-6", selected, "Preference order should select sonnet ahead of the duplicated composer models");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_FiltersByPersonaEligibility", () =>
            {
                // Two high-tier captains, only one allows the Judge specialist persona.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-opus-4-7", "[\"Worker\"]"),
                    MakeCaptain("gpt-5.5", "[\"Worker\",\"Judge\"]")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, "Judge", _ => 0);
                AssertNotNull(selected, "Should find a model eligible for Judge persona");
                AssertEqual("gpt-5.5", selected, "Only gpt-5.5 captain allows Judge persona");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_UpgradesLowToMid_WhenLowHasNoEligible", () =>
            {
                // No low-tier captains, but mid-tier captains are available
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-sonnet-4-6"),
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
                    MakeCaptain("claude-opus-4-7"),
                    MakeCaptain("gpt-5.5")
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
                    MakeCaptain("kimi-k2.5"),
                    MakeCaptain("claude-sonnet-4-6")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, null, _ => 0);
                AssertNull(selected, "High tier should never downgrade -- should return null when no high captains available");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_High_SelectsCaptainWithClaude46OpusHigh", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-4.6-opus-high")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, null, _ => 0);
                AssertNotNull(selected, "High tier should match Claude 4.6 opus high alias");
                AssertEqual("claude-4.6-opus-high", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_SelectsClaude46SonnetMedium", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-4.6-sonnet-medium")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Mid tier should match Cursor sonnet medium alias");
                AssertEqual("claude-4.6-sonnet-medium", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_SelectsGemini31Pro", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("gemini-3.1-pro")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Mid tier should match Gemini 3.1 pro alias");
                AssertEqual("gemini-3.1-pro", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_SelectsGpt53Codex", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("gpt-5.3-codex")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(selected, "Mid tier should match gpt-5.3-codex");
                AssertEqual("gpt-5.3-codex", selected, "Exact model string should round-trip");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_High_DoesNotFuzzyMatchCursorOpusAliases", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-4.6-opus-high-thinking-preview")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, null, _ => 0);
                AssertNull(selected, "High tier should not select Cursor opus alias-like names that are not exact matches");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_Mid_DoesNotFuzzyMatchCursorSonnetGeminiAliases", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-4.6-sonnet-medium-preview"),
                    MakeCaptain("gemini-3.1-pro-preview")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNull(selected, "Mid tier should not select Cursor sonnet or Gemini alias-like names that are not exact matches");
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
                    MakeCaptain("claude-sonnet-4-6", "[\"Worker\"]")
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
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7"), "curated opus is high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("gpt-5.5"), "curated gpt-5.5 is high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("gpt-5.6-sol"), "curated gpt-5.6-sol is high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-4.6-opus-high"), "curated cursor opus alias is high");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("claude-sonnet-4-6"), "curated sonnet is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("gpt-5.3-codex"), "curated gpt codex is mid");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("kimi-k2.5"), "curated kimi is low");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_OpencodeRegisteredModels_MapToCuratedTier", () =>
            {
                // The opencode-* model names are slash-prefixed (opencode/, opencode-go/) so
                // none of them match the bare "kimi-" StartsWith fallback. They only classify
                // because they were added to the curated tier arrays -- this test fails if a
                // future edit drops them from _LowModels / _MidModels.
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("opencode/kimi-k2.6"), "opencode/kimi-k2.6 is curated low");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("opencode-go/kimi-k2.6"), "opencode-go/kimi-k2.6 is curated low");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("opencode/deepseek-v4-flash"), "opencode/deepseek-v4-flash is curated low");

                // Critical ordering guard: opencode-go/kimi-k2.7-code contains "kimi" but does
                // NOT start with "kimi-", so the low-tier StartsWith fallback must not catch it.
                // The curated _MidModels entry must win and classify it mid, not low.
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("opencode-go/kimi-k2.7-code"), "opencode-go/kimi-k2.7-code is curated mid, not low");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_OpencodeUnregisteredVariant_IsNotRecognized", () =>
            {
                // A sibling opencode model that was NOT registered must stay null: the slash
                // prefix keeps it out of the kimi-/composer-/family fallbacks. Proves the
                // curated registration -- not a pattern -- is what makes the four models count.
                AssertNull(PreferredModelTierSelector.ClassifyModel("opencode/kimi-k2.9"), "unregistered opencode kimi variant is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("opencode-go/deepseek-v5"), "unregistered opencode deepseek variant is not classified");
                return Task.CompletedTask;
            });

            await RunTest("GetTierModels_ContainsRegisteredOpencodeModels", () =>
            {
                IReadOnlyList<string> lowModels = PreferredModelTierSelector.GetTierModels("low");
                IReadOnlyList<string> midModels = PreferredModelTierSelector.GetTierModels("mid");
                AssertTrue(lowModels.Contains("opencode/kimi-k2.6"), "low tier must list opencode/kimi-k2.6");
                AssertTrue(lowModels.Contains("opencode-go/kimi-k2.6"), "low tier must list opencode-go/kimi-k2.6");
                AssertTrue(lowModels.Contains("opencode/deepseek-v4-flash"), "low tier must list opencode/deepseek-v4-flash");
                AssertTrue(midModels.Contains("opencode-go/kimi-k2.7-code"), "mid tier must list opencode-go/kimi-k2.7-code");
                AssertFalse(midModels.Contains("opencode/kimi-k2.6"), "opencode/kimi-k2.6 must not leak into mid tier");
                return Task.CompletedTask;
            });

            await RunTest("ModelMatchesTierOrAbove_OpencodeMidModel_SatisfiesLowPin", () =>
            {
                // A mid opencode model must satisfy a low-tier pin (upward fallback) but a low
                // opencode model must NOT satisfy a mid-tier pin.
                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("opencode-go/kimi-k2.7-code", "low"), "mid opencode model satisfies low pin via upward fallback");
                AssertFalse(PreferredModelTierSelector.ModelMatchesTierOrAbove("opencode/kimi-k2.6", "mid"), "low opencode model must not satisfy a mid pin");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_FutureVersionBumps_AutoRegisterByFamily", () =>
            {
                // The bug this guards: an Opus version bump (4-7 -> 4-8) must classify high
                // WITHOUT being added to the curated array.
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-8"), "opus 4-8 auto-registers high");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-5"), "opus 5 auto-registers high");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("claude-sonnet-4-7"), "sonnet 4-7 auto-registers mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("gemini-4.0-pro"), "gemini pro bump auto-registers mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("composer-3"), "composer bump auto-registers mid");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_AliasPreviewVariants_AreNotRecognized", () =>
            {
                // Anchored family patterns must not absorb preview/experimental suffixes.
                AssertNull(PreferredModelTierSelector.ClassifyModel("claude-4.6-opus-high-thinking-preview"), "opus preview alias is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("claude-4.6-sonnet-medium-preview"), "sonnet preview alias is not classified");
                AssertNull(PreferredModelTierSelector.ClassifyModel("gemini-3.1-pro-preview"), "gemini pro preview is not classified");
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
                AssertFalse(PreferredModelTierSelector.ModelMatchesTierOrAbove("claude-sonnet-4-6", "high"), "mid model does not satisfy a high pin");
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
                    MakeCaptain("kimi-k2.5"),
                    MakeCaptain("claude-opus-4-7")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0);
                AssertEqual("kimi-k2.5", selected, "A non-specialist mid request must try low before high");
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

            await RunTest("SelectModel_MidTier_K2_7First_WhenIdle", () =>
            {
                // Default mid preference order lists K2.7 first. When a K2.7 captain is idle it
                // must win over other mid-tier captains.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaultOrder);
                AssertEqual("opencode-go/kimi-k2.7-code", selected, "Idle K2.7 captain should be selected first for Worker mid work");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_FallsBackToSonnet_WhenK2_7Busy", () =>
            {
                // Only sonnet and composer are idle; K2.7 captains are busy and not in the
                // idle list. The selector should fall back to sonnet, the next preferred mid model.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("gemini-3.5-pro")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaultOrder);
                AssertEqual("claude-sonnet-4-6", selected, "Should fall back to sonnet when all K2.7 captains are busy");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_FallsBackToComposer_WhenK2_7AndSonnetBusy", () =>
            {
                // Only composer and gemini are idle. Preference order lists composer before
                // unlisted models, so composer wins even though gemini appears first in the
                // idle captain list.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("gemini-3.5-pro"),
                    MakeCaptain("composer-2.5")
                };

                IReadOnlyDictionary<string, List<string>> defaultOrder = new ModelTierSettings().WithinTierPreferenceOrder;
                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaultOrder);
                AssertEqual("composer-2.5", selected, "Should fall back to composer when K2.7 and sonnet are busy");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_ConfigurablePreferenceOrder_OverridesDefault", () =>
            {
                // Operator-configurable preference order flips the default so composer is first.
                Dictionary<string, List<string>> customOrder = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "mid", new List<string> { "composer-2.5", "claude-sonnet-4-6", "opencode-go/kimi-k2.7-code" } }
                };

                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("opencode-go/kimi-k2.7-code"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("composer-2.5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, customOrder);
                AssertEqual("composer-2.5", selected, "Custom preference order should place composer ahead of K2.7 and sonnet");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_UnknownPreferenceModel_SkipsToNext", () =>
            {
                // A preference list can contain models that are not currently idle. Those are
                // skipped and the first idle preferred model is selected.
                Dictionary<string, List<string>> customOrder = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "mid", new List<string> { "opencode-go/kimi-k2.7-code", "claude-sonnet-4-6", "composer-2.5" } }
                };

                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, customOrder);
                AssertEqual("composer-2.5", selected, "Should skip missing K2.7 and sonnet captains and land on composer");
                return Task.CompletedTask;
            });

            await RunTest("ModelTierSettings_WithinTierPreferenceOrder_DefaultsAndRestores", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertTrue(defaults.WithinTierPreferenceOrder.ContainsKey("mid"), "default preference order contains mid tier");
                List<string> midOrder = defaults.WithinTierPreferenceOrder["mid"];
                // UPDATED 2026-07-22 -- deliberate default change, NOT a blind re-baseline.
                // The order previously listed only PRIOR-generation ids (k2.7 / sonnet-4-6 /
                // composer-2.5), none of which any live captain carries, so within-tier preference
                // steered nothing in production. Current-generation ids now lead each family, with
                // the prior generation retained behind as a generation fallback. Family order
                // (kimi -> sonnet -> composer) is intentionally unchanged, so the behavioural
                // fallback tests below still assert the same contract.
                AssertEqual(6, midOrder.Count, "default mid preference order lists current + prior generation per family");
                AssertEqual("opencode-go/kimi-k3", midOrder[0], "starts with current-generation Kimi (K3), the designated primary");
                AssertEqual("opencode-go/kimi-k2.7-code", midOrder[1], "prior-generation Kimi follows as a generation fallback");
                AssertEqual("claude-sonnet-5", midOrder[2], "sonnet family second, current generation first");
                AssertEqual("claude-sonnet-4-6", midOrder[3], "prior-generation sonnet follows");
                AssertEqual("composer-2-fast", midOrder[4], "composer family third, current generation first");
                AssertEqual("composer-2.5", midOrder[5], "prior-generation composer follows");

                ModelTierSettings custom = new ModelTierSettings();
                custom.WithinTierPreferenceOrder = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "low", new List<string> { "kimi-k2.5" } }
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
                AssertEqual("claude-sonnet-4-6", PreferredModelTierSelector.EnforceHighTierForPersona("claude-sonnet-4-6", "Judge"), "specialist literal pin is not rewritten to a tier selector");
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
                custom.MidTierModels = new List<string> { "custom-mid", "kimi-k2.5" };
                custom.HighTierModels = new List<string> { "custom-high" };

                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("custom-low", custom), "custom low-tier model classifies low");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("custom-mid", custom), "custom mid-tier model classifies mid");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("custom-high", custom), "custom high-tier model classifies high");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("kimi-k2.5", custom), "a default low model moved to mid config classifies mid");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7", custom), "configured low-tier membership overrides the canonical opus high pattern");
                AssertNull(PreferredModelTierSelector.ClassifyModel("not-in-any-list-and-no-pattern-match", custom), "model not in custom lists and not matching a family pattern is not classified");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_Gpt55_ExplicitEntryOnlyClassifiesHigh", () =>
            {
                // gpt-5.5 must classify high through its explicit curated entry, not a fragile
                // regex or prefix fallback. Nearby gpt variants that are not explicitly listed
                // must remain unclassified.
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("gpt-5.5"), "gpt-5.5 is explicitly high");
                AssertNull(PreferredModelTierSelector.ClassifyModel("gpt-5.5-turbo"), "no gpt prefix fallback absorbs variants");
                AssertNull(PreferredModelTierSelector.ClassifyModel("gpt-5.6"), "no gpt prefix fallback absorbs version bumps");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_KimiK27_HardensToMid", () =>
            {
                // Kimi K2.7 must reliably resolve to the mid tier, both bare and under opencode
                // prefixes, while earlier Kimi releases stay low.
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("kimi-k2.7"), "bare k2.7 is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("kimi-k2.7-code"), "k2.7-code variant is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("opencode-go/kimi-k2.7-code"), "opencode-go k2.7 is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("opencode/kimi-k2.7"), "opencode k2.7 is mid");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("kimi-k2.5"), "k2.5 stays low");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("kimi-k2.6"), "k2.6 stays low");
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

            await RunTest("ModelTierSettings_WithinTierPreferenceOrder_K2_7FirstPreserved", () =>
            {
                // The default mid preference order must keep the Kimi family first, then sonnet,
                // then composer, and must be overridable through settings.
                // UPDATED 2026-07-22: the leading id per family moved to the CURRENT generation
                // (k2.7 -> k3, sonnet-4-6 -> sonnet-5, composer-2.5 -> composer-2-fast) because the
                // previous ids matched no live captain and therefore steered nothing. The prior
                // generation is retained directly behind its successor, so family precedence --
                // which is what this test really guards -- is unchanged.
                ModelTierSettings defaults = new ModelTierSettings();
                AssertTrue(defaults.WithinTierPreferenceOrder.ContainsKey("mid"), "default contains mid preference order");
                List<string> midOrder = defaults.WithinTierPreferenceOrder["mid"];
                AssertEqual("opencode-go/kimi-k3", midOrder[0], "default mid order starts with current-generation Kimi");
                AssertEqual("opencode-go/kimi-k2.7-code", midOrder[1], "prior-generation Kimi immediately follows");
                AssertEqual("claude-sonnet-5", midOrder[2], "sonnet family is second");
                AssertEqual("composer-2-fast", midOrder[4], "composer family is third");

                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("opencode-go/kimi-k2.7-code")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Worker", _ => 0, null, defaults.WithinTierPreferenceOrder, defaults);
                AssertEqual("opencode-go/kimi-k2.7-code", selected, "K2.7-first preference is preserved when all mid captains are idle");
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
                AssertFalse(low.Contains("kimi-k2.5"), "default low model must not appear under custom low settings");
                AssertFalse(high.Contains("gpt-5.5"), "default high model must not appear under custom high settings");
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

            await RunTest("ClassifyModel_EmptyHighList_DropsExplicitOnlyEntryButKeepsPatternFamily", () =>
            {
                // Residual-risk guard: emptying the high list removes models that ONLY count via an
                // explicit entry (gpt-5.5) -- they fall through to null since there is no gpt
                // pattern. Models that count via a canonical family pattern (opus) are unaffected,
                // because the pattern fallback runs after the (now empty) list check.
                ModelTierSettings custom = new ModelTierSettings();
                custom.HighTierModels = new List<string>();

                AssertNull(PreferredModelTierSelector.ClassifyModel("gpt-5.5", custom), "explicit-only gpt-5.5 is unclassified once the high list is emptied");
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-opus-4-7", custom), "opus still classifies high via its canonical family pattern even with an empty high list");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_EmptyMidList_PatternFamiliesStillClassifyMid", () =>
            {
                // Clearing the mid list does not strip pattern-driven members: sonnet (canonical
                // pattern) and composer- (prefix fallback) remain mid because the pattern checks
                // run after the empty list check.
                ModelTierSettings custom = new ModelTierSettings();
                custom.MidTierModels = new List<string>();

                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("claude-sonnet-4-6", custom), "sonnet stays mid via canonical pattern with an empty mid list");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("composer-2.5", custom), "composer- stays mid via prefix fallback with an empty mid list");
                return Task.CompletedTask;
            });

            await RunTest("ClassifyModel_KimiK27Pattern_AnchoringBoundaries", () =>
            {
                // The K2.7 mid pattern is anchored: only k2.7 exactly, or k2.7 followed by a '-'
                // or '.' separator, is mid. Adjacent digits (k2.70, k2.75) and later minor
                // versions (k2.8) must NOT be absorbed -- they fall to the bare kimi- low fallback.
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("kimi-k2.7"), "bare k2.7 is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("kimi-k2.7-thinking"), "k2.7 with a dash separator is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("kimi-k2.7.1"), "k2.7 with a dot separator is mid");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("kimi-k2.70"), "k2.70 is NOT k2.7 -- the trailing digit blocks the anchored pattern, so it falls to low");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("kimi-k2.75"), "k2.75 is NOT k2.7 -- it falls to low");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("kimi-k2.8"), "a later kimi minor version stays low until promoted");
                return Task.CompletedTask;
            });

            await RunTest("ModelMatchesTierOrAbove_CustomSettings_FollowsConfiguredMembership", () =>
            {
                // The pin-validation upward chain is config-driven: a model reclassified to high by
                // settings now satisfies a low pin (upward fallback) and a high pin, while a model
                // moved down to low no longer satisfies a mid pin.
                ModelTierSettings custom = new ModelTierSettings();
                custom.LowTierModels = new List<string> { "claude-opus-4-7" };
                custom.HighTierModels = new List<string> { "kimi-k2.5" };

                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("kimi-k2.5", "low", custom), "a model promoted to high via config satisfies a low pin");
                AssertTrue(PreferredModelTierSelector.ModelMatchesTierOrAbove("kimi-k2.5", "high", custom), "a model promoted to high via config satisfies a high pin");
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
                Captain captain = MakeCaptain("claude-sonnet-4-6", "[\"Worker\"]");

                AssertFalse(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "high"), "a default mid-tier captain does not satisfy a high tier pin");

                ModelTierSettings custom = new ModelTierSettings();
                custom.HighTierModels = new List<string> { "claude-sonnet-4-6" };
                AssertTrue(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "high", custom), "config promoting the model to high lets the captain satisfy a high tier pin");
                return Task.CompletedTask;
            });

            await RunTest("CaptainSatisfiesPreferredRouting_LiteralPinAndPersona_AreEnforced", () =>
            {
                // Literal model pins must match exactly (tier config is irrelevant), and the
                // persona allow-list is enforced independently of the model pin.
                Captain captain = MakeCaptain("claude-opus-4-7", "[\"Worker\",\"Judge\"]");

                AssertTrue(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "claude-opus-4-7"), "exact literal model pin is satisfied");
                AssertFalse(MissionService.CaptainSatisfiesPreferredRouting(captain, null, "gpt-5.5"), "a non-matching literal model pin is rejected");
                AssertTrue(MissionService.CaptainSatisfiesPreferredRouting(captain, "Judge", null), "an allowed persona with no model pin is satisfied");
                AssertFalse(MissionService.CaptainSatisfiesPreferredRouting(captain, "Architect", null), "a persona absent from the allow-list is rejected");
                return Task.CompletedTask;
            });
        }
    }
}
