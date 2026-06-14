namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
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

            await RunTest("SelectModel_MidTier_RandomAcrossEligibleModels", () =>
            {
                // Three mid-tier models, one captain each. Seeded random verifies uniform selection.
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("gemini-3.5-pro")
                };

                // With deterministic picker always returning 0, should pick first eligible
                string? m0 = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 0);
                AssertNotNull(m0, "Should select a model when mid-tier captains are available");

                // With picker returning 1, should pick second eligible
                string? m1 = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 1);
                AssertNotNull(m1, "Should select second model");
                AssertFalse(m0 == m1, "Different picker indices should yield different models");

                // With picker returning 2, should pick third eligible
                string? m2 = PreferredModelTierSelector.SelectModel("mid", captains, null, _ => 2);
                AssertNotNull(m2, "Should select third model");
                AssertFalse(m0 == m2, "Different picker indices should yield different models");
                AssertFalse(m1 == m2, "Different picker indices should yield different models");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_MidTier_DuplicatedCaptains_DoNotDuplicateModelCandidates", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("composer-2.5"),
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("gemini-3.5-pro")
                };
                int observedUpperBound = 0;

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, null, upperBound =>
                {
                    observedUpperBound = upperBound;
                    return 1;
                });

                AssertEqual(3, observedUpperBound, "Random picker should see one entry per eligible model name");
                AssertEqual("claude-sonnet-4-6", selected, "Picker index should select the second model, not the second captain");
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

            await RunTest("SelectModel_High_SelectsCaptainWithClaude46OpusHighThinking", () =>
            {
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-4.6-opus-high-thinking")
                };

                string? selected = PreferredModelTierSelector.SelectModel("high", captains, null, _ => 0);
                AssertNotNull(selected, "High tier should match Claude 4.6 opus high-thinking alias");
                AssertEqual("claude-4.6-opus-high-thinking", selected, "Exact model string should round-trip");
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
                AssertEqual("high", PreferredModelTierSelector.ClassifyModel("claude-4.6-opus-high-thinking"), "curated cursor opus alias is high");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("claude-sonnet-4-6"), "curated sonnet is mid");
                AssertEqual("mid", PreferredModelTierSelector.ClassifyModel("gpt-5.3-codex"), "curated gpt codex is mid");
                AssertEqual("low", PreferredModelTierSelector.ClassifyModel("kimi-k2.5"), "curated kimi is low");
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
        }
    }
}
