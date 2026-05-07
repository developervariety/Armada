namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
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
                    MakeCaptain("composer-2-fast"),
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
                    MakeCaptain("composer-2-fast"),
                    MakeCaptain("composer-2-fast"),
                    MakeCaptain("composer-2-fast"),
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
                // Two mid-tier captains, only one allows Judge persona
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("composer-2-fast", "[\"Worker\"]"),
                    MakeCaptain("claude-sonnet-4-6", "[\"Worker\",\"Judge\"]")
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Judge", _ => 0);
                AssertNotNull(selected, "Should find a model eligible for Judge persona");
                AssertEqual("claude-sonnet-4-6", selected, "Only claude-sonnet-4-6 captain allows Judge persona");
                return Task.CompletedTask;
            });

            await RunTest("SelectModel_UpgradesLowToMid_WhenLowHasNoEligible", () =>
            {
                // No low-tier captains, but mid-tier captains are available
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-sonnet-4-6"),
                    MakeCaptain("composer-2-fast")
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
                // Captain with null AllowedPersonas should be eligible for any persona
                List<Captain> captains = new List<Captain>
                {
                    MakeCaptain("claude-sonnet-4-6", null)
                };

                string? selected = PreferredModelTierSelector.SelectModel("mid", captains, "Judge", _ => 0);
                AssertNotNull(selected, "Captain with null AllowedPersonas should serve any persona including Judge");
                return Task.CompletedTask;
            });
        }
    }
}
