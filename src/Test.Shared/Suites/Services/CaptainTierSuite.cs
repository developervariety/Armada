namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="CaptainTierSelector"/>. Positive cases confirm model classification, the
    /// explicit-tier override, and lowest-eligible selection; negative cases confirm no captain is chosen
    /// when none can meet the required tier and that unknown models fall back to Standard.
    /// </summary>
    public sealed class CaptainTierSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the captain-tier suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // ---- Model classification ----
            cases.Add(Case("classify_premium_models", "Premium models classify as Premium", TestTags.Positive, () =>
            {
                AssertEqual(CaptainTierEnum.Premium, CaptainTierSelector.ClassifyModel("claude-opus-4-8"));
                AssertEqual(CaptainTierEnum.Premium, CaptainTierSelector.ClassifyModel("gpt-5.2"));
                AssertEqual(CaptainTierEnum.Premium, CaptainTierSelector.ClassifyModel("o3-pro"));
                AssertEqual(CaptainTierEnum.Premium, CaptainTierSelector.ClassifyModel("gemini-2.5-pro"));
            }));

            cases.Add(Case("classify_economy_models", "Economy models classify as Economy", TestTags.Positive, () =>
            {
                AssertEqual(CaptainTierEnum.Economy, CaptainTierSelector.ClassifyModel("claude-haiku-4-5"));
                AssertEqual(CaptainTierEnum.Economy, CaptainTierSelector.ClassifyModel("gpt-5-mini"));
                AssertEqual(CaptainTierEnum.Economy, CaptainTierSelector.ClassifyModel("gemini-2.0-flash"));
                AssertEqual(CaptainTierEnum.Economy, CaptainTierSelector.ClassifyModel("gpt-oss-120b"));
            }));

            cases.Add(Case("classify_standard_models", "Sonnet/gpt-4 classify as Standard", TestTags.Positive, () =>
            {
                AssertEqual(CaptainTierEnum.Standard, CaptainTierSelector.ClassifyModel("claude-sonnet-5"));
                AssertEqual(CaptainTierEnum.Standard, CaptainTierSelector.ClassifyModel("gpt-4.1"));
            }));

            cases.Add(Case("classify_unknown_defaults_standard", "Unknown/blank models default to Standard", TestTags.Negative, () =>
            {
                AssertEqual(CaptainTierEnum.Standard, CaptainTierSelector.ClassifyModel("some-unknown-model"));
                AssertEqual(CaptainTierEnum.Standard, CaptainTierSelector.ClassifyModel(null));
                AssertEqual(CaptainTierEnum.Standard, CaptainTierSelector.ClassifyModel("   "));
            }));

            // ---- Effective tier ----
            cases.Add(Case("explicit_tier_overrides_model", "Explicit tier overrides model classification", TestTags.Positive, () =>
            {
                Captain economyModelPremiumTier = MakeCaptain("claude-haiku-4-5", CaptainTierEnum.Premium);
                AssertEqual(CaptainTierEnum.Premium, CaptainTierSelector.EffectiveTier(economyModelPremiumTier));
            }));

            cases.Add(Case("effective_tier_null_captain_standard", "Null captain resolves to Standard", TestTags.Negative, () =>
            {
                AssertEqual(CaptainTierEnum.Standard, CaptainTierSelector.EffectiveTier(null!));
            }));

            // ---- Selection ----
            cases.Add(Case("select_prefers_lowest_eligible", "Select prefers lowest eligible tier at/above required", TestTags.Positive, () =>
            {
                Captain economy = MakeCaptain("claude-haiku-4-5", null);
                Captain standard = MakeCaptain("claude-sonnet-5", null);
                Captain premium = MakeCaptain("claude-opus-4-8", null);
                List<Captain> pool = new List<Captain> { premium, economy, standard };

                // Standard mission: should pick Standard, not Premium.
                Captain? chosen = CaptainTierSelector.Select(pool, CaptainTierEnum.Standard);
                AssertNotNull(chosen);
                AssertEqual(standard.Id, chosen!.Id);

                // Economy mission: should pick Economy.
                Captain? cheap = CaptainTierSelector.Select(pool, CaptainTierEnum.Economy);
                AssertNotNull(cheap);
                AssertEqual(economy.Id, cheap!.Id);
            }));

            cases.Add(Case("select_falls_back_upward", "Select falls back upward when required tier idle-empty", TestTags.Positive, () =>
            {
                Captain premium = MakeCaptain("claude-opus-4-8", null);
                List<Captain> pool = new List<Captain> { premium };

                // Only a Premium captain is idle; a Standard mission may use it.
                Captain? chosen = CaptainTierSelector.Select(pool, CaptainTierEnum.Standard);
                AssertNotNull(chosen);
                AssertEqual(premium.Id, chosen!.Id);
            }));

            cases.Add(Case("select_null_when_none_meet_tier", "Select returns null when no captain meets required tier", TestTags.Negative, () =>
            {
                Captain economy = MakeCaptain("claude-haiku-4-5", null);
                List<Captain> pool = new List<Captain> { economy };

                // Premium mission, only Economy idle -> no eligible captain.
                Captain? chosen = CaptainTierSelector.Select(pool, CaptainTierEnum.Premium);
                AssertNull(chosen);
            }));

            cases.Add(Case("select_null_on_empty_pool", "Select returns null on empty/null pool", TestTags.Negative, () =>
            {
                AssertNull(CaptainTierSelector.Select(new List<Captain>(), CaptainTierEnum.Standard));
                AssertNull(CaptainTierSelector.Select(null!, CaptainTierEnum.Standard));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.CaptainTier",
                displayName: "Captain Tier Selector",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static Captain MakeCaptain(string model, CaptainTierEnum? tier)
        {
            Captain captain = new Captain();
            captain.Model = model;
            captain.Tier = tier;
            return captain;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.CaptainTier",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
