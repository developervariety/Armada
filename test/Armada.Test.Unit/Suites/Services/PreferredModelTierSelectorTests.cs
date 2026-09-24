namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for tier routing: preferredModel tier selectors and the tier floors they name, the tier order,
    /// Legacy Routing selection by Capability tier and preference rank, persona eligibility, specialist
    /// personas from records, concrete model pins, and the eligibility-layer explanation.
    /// </summary>
    public class PreferredModelTierSelectorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Preferred Model Tier Selector";

        private static Captain MakeCaptain(string model, CaptainTierEnum tier, int rank = 0, string? allowedPersonas = null)
        {
            Captain c = new Captain("test-captain");
            c.Model = model;
            c.Tier = tier;
            c.PreferenceRank = rank;
            c.AllowedPersonas = allowedPersonas;
            c.State = CaptainStateEnum.Idle;
            return c;
        }

        private static ModelTierSettings Settings(params string[] premiumPersonas)
        {
            ModelTierSettings settings = new ModelTierSettings();
            settings.Records = TierRoutingRecords.ForMinimumTiers((premiumPersonas ?? Array.Empty<string>())
                .Select(name => new KeyValuePair<string, CaptainTierEnum>(name, CaptainTierEnum.Premium)));
            return settings;
        }

        private static ModelTierSettings ModelTierSettingsFor(CaptainTierEnum minimumTier)
        {
            ModelTierSettings settings = new ModelTierSettings();
            settings.Records = TierRoutingRecords.ForMinimumTiers(new[]
            {
                new KeyValuePair<string, CaptainTierEnum>("Judge", minimumTier)
            });
            return settings;
        }

        private static string? Pick(ModelTierSettings settings, string? preferredModel, List<Captain> pool, string? persona, Func<int, int> randomPick)
        {
            Mission mission = new Mission { Persona = persona, PreferredModel = preferredModel };
            return LegacyCaptainSelector.Select(settings, mission, pool, false, randomPick)?.Model;
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
                AssertFalse(PreferredModelTierSelector.IsTierSelector("gpt-5.6-luna"), "literal model name should not be a tier selector");
                AssertFalse(PreferredModelTierSelector.IsTierSelector("opencode-go/deepseek-v4-flash"), "literal model name should not be a tier selector");
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
                AssertEqual(PreferredModelTierSelector.LowTier, PreferredModelTierSelector.NormalizeTier("LOW"), "low normalizes to low");
                AssertEqual(PreferredModelTierSelector.MidTier, PreferredModelTierSelector.NormalizeTier("quick"), "quick normalizes to mid");
                AssertEqual(PreferredModelTierSelector.MidTier, PreferredModelTierSelector.NormalizeTier("medium"), "medium normalizes to mid");
                return Task.CompletedTask;
            });

            await RunTest("NormalizeTier_UnknownSelector_Throws", () =>
            {
                AssertThrows<ArgumentException>(() => PreferredModelTierSelector.NormalizeTier("ultra"), "unknown tier selector throws");
                AssertThrows<ArgumentException>(() => PreferredModelTierSelector.NormalizeTier(""), "empty tier selector throws");
                return Task.CompletedTask;
            });

            await RunTest("FloorOf_LowMidHigh_MapToEconomyStandardPremium", () =>
            {
                AssertEqual(CaptainTierEnum.Economy, PreferredModelTierSelector.FloorOf("low"), "low is an Economy floor");
                AssertEqual(CaptainTierEnum.Standard, PreferredModelTierSelector.FloorOf("mid"), "mid is a Standard floor");
                AssertEqual(CaptainTierEnum.Premium, PreferredModelTierSelector.FloorOf("HIGH"), "high is a Premium floor");
                AssertEqual(CaptainTierEnum.Standard, PreferredModelTierSelector.FloorOf("medium"), "an alias maps through its canonical tier");
                return Task.CompletedTask;
            });

            await RunTest("TierOrder_Floor_TriesFloorThenHigherTiersLowestFirst", () =>
            {
                AssertEqual("Economy,Standard,Premium", String.Join(",", PreferredModelTierSelector.TierOrder(CaptainTierEnum.Economy, false)), "Economy floor");
                AssertEqual("Standard,Premium", String.Join(",", PreferredModelTierSelector.TierOrder(CaptainTierEnum.Standard, false)), "Standard floor");
                AssertEqual("Premium", String.Join(",", PreferredModelTierSelector.TierOrder(CaptainTierEnum.Premium, false)), "Premium floor");
                return Task.CompletedTask;
            });

            await RunTest("TierOrder_NoFloor_StandardThenPremiumThenEconomy", () =>
            {
                AssertEqual("Standard,Premium,Economy", String.Join(",", PreferredModelTierSelector.TierOrder(null, false)), "no floor");
                return Task.CompletedTask;
            });

            await RunTest("TierOrder_PremiumMinimum_PremiumOnlyWhateverTheFloor", () =>
            {
                foreach (CaptainTierEnum? floor in new CaptainTierEnum?[] { null, CaptainTierEnum.Economy, CaptainTierEnum.Standard, CaptainTierEnum.Premium })
                    AssertEqual("Premium", String.Join(",", PreferredModelTierSelector.TierOrder(floor, CaptainTierEnum.Premium)), "Premium minimum with floor " + floor);
                AssertEqual("Standard,Premium", String.Join(",", PreferredModelTierSelector.TierOrder(null, CaptainTierEnum.Standard)), "a Standard minimum can use Standard or Premium");
                return Task.CompletedTask;
            });

            await RunTest("CaptainMatchesTierOrAbove_RespectsUpwardChain", () =>
            {
                Captain standard = MakeCaptain("model-s", CaptainTierEnum.Standard);
                AssertTrue(PreferredModelTierSelector.CaptainMatchesTierOrAbove(standard, "low"), "Standard satisfies low");
                AssertTrue(PreferredModelTierSelector.CaptainMatchesTierOrAbove(standard, "mid"), "Standard satisfies mid");
                AssertFalse(PreferredModelTierSelector.CaptainMatchesTierOrAbove(standard, "high"), "Standard does not satisfy high");
                return Task.CompletedTask;
            });

            await RunTest("Select_LowestTierAtOrAboveTheFloor_Wins", () =>
            {
                List<Captain> pool = new List<Captain>
                {
                    MakeCaptain("model-p", CaptainTierEnum.Premium),
                    MakeCaptain("model-s", CaptainTierEnum.Standard),
                    MakeCaptain("model-e", CaptainTierEnum.Economy)
                };
                AssertEqual("model-e", Pick(Settings(), "low", pool, "Worker", n => 0), "low takes the Economy captain");
                AssertEqual("model-s", Pick(Settings(), "mid", pool, "Worker", n => 0), "mid takes the Standard captain, not Premium");
                AssertEqual("model-p", Pick(Settings(), "high", pool, "Worker", n => 0), "high takes the Premium captain");
                return Task.CompletedTask;
            });

            await RunTest("Select_NoCaptainAtTheFloor_FallsUpNeverDown", () =>
            {
                List<Captain> premiumOnly = new List<Captain> { MakeCaptain("model-p", CaptainTierEnum.Premium) };
                AssertEqual("model-p", Pick(Settings(), "mid", premiumOnly, "Worker", n => 0), "mid falls up to Premium");
                List<Captain> belowHigh = new List<Captain> { MakeCaptain("model-s", CaptainTierEnum.Standard), MakeCaptain("model-e", CaptainTierEnum.Economy) };
                AssertNull(Pick(Settings(), "high", belowHigh, "Worker", n => 0), "high never falls down");
                AssertNull(Pick(Settings(), "mid", new List<Captain> { MakeCaptain("model-e", CaptainTierEnum.Economy) }, "Worker", n => 0), "mid never takes Economy");
                return Task.CompletedTask;
            });

            await RunTest("Select_EqualPeers_RandomByModelNotByCaptainCount", () =>
            {
                List<Captain> pool = new List<Captain>
                {
                    MakeCaptain("model-a", CaptainTierEnum.Standard),
                    MakeCaptain("model-a", CaptainTierEnum.Standard),
                    MakeCaptain("model-a", CaptainTierEnum.Standard),
                    MakeCaptain("model-b", CaptainTierEnum.Standard)
                };
                List<int> bounds = new List<int>();
                AssertEqual("model-a", Pick(Settings(), "mid", pool, "Worker", n => { bounds.Add(n); return 0; }), "random index 0 takes the first model");
                AssertEqual("model-b", Pick(Settings(), "mid", pool, "Worker", n => n - 1), "the last random index takes the other model");
                AssertEqual("2", String.Join(",", bounds), "the random pick is over the two models, not the four captains");
                return Task.CompletedTask;
            });

            await RunTest("Select_HigherPreferenceRank_WinsRegardlessOfRandom", () =>
            {
                List<Captain> pool = new List<Captain>
                {
                    MakeCaptain("model-low-rank", CaptainTierEnum.Premium, 1),
                    MakeCaptain("model-high-rank", CaptainTierEnum.Premium, 3),
                    MakeCaptain("model-unranked", CaptainTierEnum.Premium)
                };
                AssertEqual("model-high-rank", Pick(Settings(), "high", pool, "Judge", n => 0), "rank 3 wins (index 0)");
                AssertEqual("model-high-rank", Pick(Settings(), "high", pool, "Judge", n => n - 1), "rank 3 wins (last index)");
                Mission judge = new Mission { Persona = "Judge", PreferredModel = "high" };
                AssertEqual("model-high-rank,model-low-rank,model-unranked",
                    String.Join(",", LegacyCaptainSelector.Order(Settings(), judge, pool, false, n => 0).Select(c => c.Model)), "order follows rank");
                return Task.CompletedTask;
            });

            await RunTest("Select_EqualRank_IsARandomTie", () =>
            {
                List<Captain> pool = new List<Captain>
                {
                    MakeCaptain("model-a", CaptainTierEnum.Standard, 5),
                    MakeCaptain("model-b", CaptainTierEnum.Standard, 5),
                    MakeCaptain("model-c", CaptainTierEnum.Standard, 1)
                };
                HashSet<string?> picks = new HashSet<string?> { Pick(Settings(), "mid", pool, "Worker", n => 0), Pick(Settings(), "mid", pool, "Worker", n => n - 1) };
                AssertTrue(picks.SetEquals(new[] { "model-a", "model-b" }), "the tied rank-5 models vary with the random pick and rank 1 is never chosen");
                return Task.CompletedTask;
            });

            await RunTest("Select_RankDominatesTheNonNativePreference", () =>
            {
                Captain native = MakeCaptain("model-native", CaptainTierEnum.Premium, 2);
                native.Runtime = AgentRuntimeEnum.OpenCode;
                native.ApiBaseUrl = "https://opencode.example.com/v1";
                Captain external = MakeCaptain("model-external", CaptainTierEnum.Premium, 1);
                external.Runtime = AgentRuntimeEnum.ClaudeCode;
                external.ApiBaseUrl = "https://api.example.com/v1";
                ModelTierSettings settings = Settings();
                settings.PreferNonNativeFirst = true;
                AssertEqual("model-native", Pick(settings, "high", new List<Captain> { external, native }, null, n => 0), "higher rank wins over external service");
                return Task.CompletedTask;
            });

            await RunTest("Select_NonNativeFirst_BreaksEqualRankTies_OnlyWhenEnabled", () =>
            {
                Captain native = MakeCaptain("model-native", CaptainTierEnum.Standard);
                native.Runtime = AgentRuntimeEnum.Codex;
                Captain external = MakeCaptain("model-external", CaptainTierEnum.Standard);
                external.Runtime = AgentRuntimeEnum.Codex;
                external.ApiBaseUrl = "https://api.example.com/v1";
                List<Captain> pool = new List<Captain> { native, external };

                ModelTierSettings preferring = Settings();
                preferring.PreferNonNativeFirst = true;
                AssertEqual("model-external", Pick(preferring, "mid", pool, "Worker", n => 0), "external wins at index 0");
                AssertEqual("model-external", Pick(preferring, "mid", pool, "Worker", n => n - 1), "external wins at the last index");

                AssertEqual("model-native", Pick(Settings(), "mid", pool, "Worker", n => 0), "without the preference the tie is random (index 0)");
                AssertEqual("model-external", Pick(Settings(), "mid", pool, "Worker", n => n - 1), "without the preference the tie is random (last index)");
                return Task.CompletedTask;
            });

            await RunTest("Select_OpenCodeCaptainWithBaseUrl_IsNative", () =>
            {
                Captain openCode = MakeCaptain("model-opencode", CaptainTierEnum.Standard);
                openCode.Runtime = AgentRuntimeEnum.OpenCode;
                openCode.ApiBaseUrl = "https://api.example.com/v1";
                Captain other = MakeCaptain("model-other", CaptainTierEnum.Standard);
                ModelTierSettings settings = Settings();
                settings.PreferNonNativeFirst = true;
                AssertEqual("model-other", Pick(settings, "mid", new List<Captain> { openCode, other }, "Worker", n => n - 1), "an OpenCode captain is not preferred as external");
                return Task.CompletedTask;
            });

            await RunTest("Select_FiltersByPersonaEligibility", () =>
            {
                List<Captain> pool = new List<Captain>
                {
                    MakeCaptain("model-worker-only", CaptainTierEnum.Premium, 9, "[\"Worker\"]"),
                    MakeCaptain("model-judge", CaptainTierEnum.Premium, 0, "[\"Worker\",\"Judge\"]")
                };
                AssertEqual("model-judge", Pick(Settings(), "high", pool, "Judge", n => 0), "only the captain that allows Judge is eligible, whatever its rank");
                return Task.CompletedTask;
            });

            await RunTest("Select_NullPersona_AcceptsCaptainsWithAnyAllowList", () =>
            {
                List<Captain> pool = new List<Captain> { MakeCaptain("model-a", CaptainTierEnum.Standard, 0, "[\"Worker\"]") };
                AssertEqual("model-a", Pick(Settings(), "mid", pool, null, n => 0), "a mission without a persona accepts a restricted captain");
                return Task.CompletedTask;
            });

            await RunTest("Select_CaptainWithoutAllowList_AcceptsAnyPersona", () =>
            {
                List<Captain> pool = new List<Captain> { MakeCaptain("model-a", CaptainTierEnum.Premium) };
                AssertEqual("model-a", Pick(Settings("Judge"), "high", pool, "Judge", n => 0), "a captain without an allow-list serves Judge");
                return Task.CompletedTask;
            });

            await RunTest("Select_PremiumMinimum_ForcesPremiumForEveryPreferredModel", () =>
            {
                List<Captain> pool = new List<Captain>
                {
                    MakeCaptain("model-e", CaptainTierEnum.Economy),
                    MakeCaptain("model-s", CaptainTierEnum.Standard),
                    MakeCaptain("model-p", CaptainTierEnum.Premium)
                };
                foreach (string? preferred in new string?[] { "low", "mid", "high", null })
                    AssertEqual("model-p", Pick(Settings("Judge"), preferred, pool, "Judge", n => 0), "Premium minimum with preferred model " + (preferred ?? "(none)"));
                return Task.CompletedTask;
            });

            await RunTest("Select_PremiumMinimum_WithoutAnIdlePremiumCaptain_Waits", () =>
            {
                List<Captain> pool = new List<Captain> { MakeCaptain("model-s", CaptainTierEnum.Standard) };
                AssertNull(Pick(Settings("Judge"), null, pool, "Judge", n => 0), "a Premium minimum never runs below Premium");
                AssertEqual("model-s", Pick(Settings(), null, pool, "Judge", n => 0), "without the flag the same persona may run on Standard");
                return Task.CompletedTask;
            });

            await RunTest("Select_NonSpecialistExplicitHigh_IsHonoured", () =>
            {
                List<Captain> pool = new List<Captain> { MakeCaptain("model-s", CaptainTierEnum.Standard), MakeCaptain("model-p", CaptainTierEnum.Premium) };
                AssertEqual("model-p", Pick(Settings(), "high", pool, "Worker", n => 0), "a Worker asking for high gets Premium");
                return Task.CompletedTask;
            });

            await RunTest("Select_NoPreferredModel_TriesStandardThenPremiumThenEconomy", () =>
            {
                List<Captain> pool = new List<Captain>
                {
                    MakeCaptain("model-e", CaptainTierEnum.Economy),
                    MakeCaptain("model-p", CaptainTierEnum.Premium),
                    MakeCaptain("model-s", CaptainTierEnum.Standard)
                };
                Mission mission = new Mission { Persona = "Worker" };
                AssertEqual("model-s,model-p,model-e", String.Join(",", LegacyCaptainSelector.Order(Settings(), mission, pool, false, n => 0).Select(c => c.Model)), "default order");
                return Task.CompletedTask;
            });

            await RunTest("Select_ConcretePin_IdleCaptainOnTheModel_IsHonoured", () =>
            {
                List<Captain> pool = new List<Captain> { MakeCaptain("model-p", CaptainTierEnum.Premium, 9), MakeCaptain("model-e", CaptainTierEnum.Economy) };
                AssertEqual("model-e", Pick(Settings(), "model-e", pool, "Worker", n => 0), "an explicit model pin is honoured");
                List<Captain> restricted = new List<Captain> { MakeCaptain("model-e", CaptainTierEnum.Economy, 0, "[\"Worker\"]"), MakeCaptain("model-p", CaptainTierEnum.Premium) };
                AssertNull(Pick(Settings(), "model-e", restricted, "Judge", n => 0), "a pinned model whose captain disallows the persona waits instead of substituting");
                return Task.CompletedTask;
            });

            await RunTest("Select_ConcretePin_NoIdleCaptainOnTheModel_UsesTheRosterTierOfTheModel", () =>
            {
                Captain busyPremium = MakeCaptain("model-pinned", CaptainTierEnum.Premium);
                List<Captain> roster = new List<Captain> { busyPremium, MakeCaptain("model-s", CaptainTierEnum.Standard), MakeCaptain("model-p", CaptainTierEnum.Premium) };
                ModelTierSettings settings = Settings();
                settings.Records = TierRoutingRecords.From(null, roster);
                List<Captain> idle = roster.Where(c => c != busyPremium).ToList();
                AssertEqual("model-p", Pick(settings, "model-pinned", idle, "Worker", n => 0), "the pin falls back to a captain at the pinned model's Premium tier");
                return Task.CompletedTask;
            });

            await RunTest("Select_ConcretePin_UnknownModel_SetsNoFloor", () =>
            {
                List<Captain> pool = new List<Captain> { MakeCaptain("model-e", CaptainTierEnum.Economy) };
                AssertEqual("model-e", Pick(Settings(), "model-nobody-runs", pool, "Worker", n => 0), "a pin to a model no captain runs does not strand the work");
                return Task.CompletedTask;
            });

            await RunTest("Select_ConcretePin_KnownFamilyNoCaptain_UsesTheFamilyTier", () =>
            {
                List<Captain> pool = new List<Captain> { MakeCaptain("model-s", CaptainTierEnum.Standard), MakeCaptain("model-p", CaptainTierEnum.Premium) };
                AssertEqual("model-p", Pick(Settings(), "claude-opus-9", pool, "Worker", n => 0), "an opus-family pin no captain runs sets a Premium floor");
                return Task.CompletedTask;
            });

            await RunTest("Select_RetrySkipList_AvoidsTheSkippedCaptainWhileAnotherIsAdmitted", () =>
            {
                Captain skipped = MakeCaptain("model-s", CaptainTierEnum.Standard, 5);
                Captain other = MakeCaptain("model-p", CaptainTierEnum.Premium);
                Mission retry = new Mission { Persona = "Worker", PreferredModel = "mid", RetrySkipCaptainIds = skipped.Id };
                AssertEqual(other.Id, LegacyCaptainSelector.Select(Settings(), retry, new List<Captain> { skipped, other }, false, n => 0)!.Id, "the retry moves to another admitted captain");
                AssertEqual(skipped.Id, LegacyCaptainSelector.Select(Settings(), retry, new List<Captain> { skipped }, false, n => 0)!.Id, "with no other captain the skipped one is reused");
                return Task.CompletedTask;
            });

            await RunTest("Select_PreferredPersona_BreaksTiesAfterRank", () =>
            {
                Captain plain = MakeCaptain("model-a", CaptainTierEnum.Standard);
                Captain preferring = MakeCaptain("model-b", CaptainTierEnum.Standard);
                preferring.PreferredPersona = "Worker";
                Captain ranked = MakeCaptain("model-c", CaptainTierEnum.Standard, 1);
                AssertEqual("model-b", Pick(Settings(), "mid", new List<Captain> { plain, preferring }, "Worker", n => 0), "preferred persona wins an equal-rank tie");
                AssertEqual("model-c", Pick(Settings(), "mid", new List<Captain> { plain, preferring, ranked }, "Worker", n => 0), "rank outranks preferred persona");
                return Task.CompletedTask;
            });

            await RunTest("TierRoutingRecords_TierOfModel_IsTheHighestTierOfItsCaptains", () =>
            {
                TierRoutingRecords records = TierRoutingRecords.From(null, new List<Captain>
                {
                    MakeCaptain("model-x", CaptainTierEnum.Standard),
                    MakeCaptain("MODEL-X", CaptainTierEnum.Premium),
                    MakeCaptain("model-y", CaptainTierEnum.Economy)
                });
                AssertEqual(CaptainTierEnum.Premium, records.TierOfModel("model-x"), "highest tier, case-insensitive");
                AssertEqual(CaptainTierEnum.Economy, records.TierOfModel("model-y"));
                AssertNull(records.TierOfModel("model-z"), "an unknown model has no tier");
                return Task.CompletedTask;
            });

            await RunTest("ExplainExclusions_NamesThePersonaLockTheTierFloorAndThePin", () =>
            {
                Captain locked = MakeCaptain("model-locked", CaptainTierEnum.Premium, 0, "[\"Worker\"]");
                Captain below = MakeCaptain("model-below", CaptainTierEnum.Standard);
                Captain admitted = MakeCaptain("model-ok", CaptainTierEnum.Premium);
                List<Captain> pool = new List<Captain> { locked, below, admitted };
                Dictionary<string, string> reasons = LegacyCaptainSelector.ExplainExclusions(Settings("Judge"), new Mission { Persona = "Judge", PreferredModel = "mid" }, pool);
                AssertEqual(LegacyCaptainSelector.ReasonPersonaNotAllowed, reasons[locked.Id]);
                AssertEqual(LegacyCaptainSelector.ReasonBelowTierFloor, reasons[below.Id], "the specialist Premium floor excludes Standard");
                AssertFalse(reasons.ContainsKey(admitted.Id), "an admitted captain has no exclusion");

                Dictionary<string, string> pinned = LegacyCaptainSelector.ExplainExclusions(Settings(), new Mission { Persona = "Worker", PreferredModel = "model-below" }, pool);
                AssertEqual(LegacyCaptainSelector.ReasonModelPinMismatch, pinned[admitted.Id]);
                return Task.CompletedTask;
            });

            await RunTest("PersonaMinimumTier_ComesFromPersonaRecords", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertFalse(defaults.IsSpecialistPersona("Judge"), "no persona has a Premium minimum until its record is set");
                AssertEqual(0, defaults.SpecialistPersonas.Count);

                List<Persona> personas = new List<Persona>
                {
                    new Persona("Judge", "persona.judge") { MinimumTier = CaptainTierEnum.Premium },
                    new Persona("Test Engineer", "persona.test_engineer") { MinimumTier = CaptainTierEnum.Standard },
                    new Persona("Worker", "persona.worker")
                };
                ModelTierSettings settings = new ModelTierSettings { Records = TierRoutingRecords.From(personas, null) };
                AssertTrue(settings.IsSpecialistPersona("judge"), "Premium minimum matching is case-insensitive");
                AssertFalse(settings.IsSpecialistPersona("TestEngineer"), "a Standard minimum is not a Premium minimum");
                AssertEqual(CaptainTierEnum.Standard, settings.MinimumTierForPersona("TestEngineer"), "legacy spelling matches the Test Engineer minimum");
                AssertFalse(settings.IsSpecialistPersona("Worker"), "a persona without a Premium minimum is not Premium-only");
                AssertFalse(settings.IsSpecialistPersona(null), "null has no Premium minimum");
                AssertEqual(1, settings.SpecialistPersonas.Count);
                return Task.CompletedTask;
            });

            await RunTest("EnforceHighTierForPersona_NonSpecialist_PassesTierThroughUnchanged", () =>
            {
                AssertEqual("mid", PreferredModelTierSelector.EnforceHighTierForPersona("mid", "Worker"), "non-specialist mid request is preserved at create time");
                AssertEqual("low", PreferredModelTierSelector.EnforceHighTierForPersona("low", "Worker"), "non-specialist low request is preserved at create time");
                AssertNull(PreferredModelTierSelector.EnforceHighTierForPersona(null, "Worker"), "non-specialist with no preferred model is left unset");
                AssertNull(PreferredModelTierSelector.EnforceHighTierForPersona(null, null), "null persona is non-specialist and is left unset");
                return Task.CompletedTask;
            });

            await RunTest("EnforceHighTierForPersona_Specialist_UpgradesBelowHighToHigh", () =>
            {
                IReadOnlyCollection<string> specialists = Settings("Judge", "Architect").SpecialistPersonas;
                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona("mid", "Judge", specialists), "specialist mid request is upgraded to high");
                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona("low", "Architect", specialists), "specialist low request is upgraded to high");
                AssertEqual("high", PreferredModelTierSelector.EnforceHighTierForPersona("high", "Judge", specialists), "specialist that already asked for high stays high");
                return Task.CompletedTask;
            });

            await RunTest("EnforceHighTierForPersona_SpecialistLiteralModel_PassesThroughUnchanged", () =>
            {
                AssertEqual("gpt-5.6-luna", PreferredModelTierSelector.EnforceHighTierForPersona("gpt-5.6-luna", "Judge", Settings("Judge").SpecialistPersonas), "specialist literal pin is not rewritten");
                return Task.CompletedTask;
            });

            await RunTest("ResolveTierForPersona_HighRequestRemainsAHardFloor", () =>
            {
                AssertEqual("high", PreferredModelTierSelector.ResolveTierForPersona("high", "Worker"), "a high mission request stays a hard floor");
                AssertEqual("high", PreferredModelTierSelector.ResolveTierForPersona("High", "Worker"), "the request is case-insensitive");
                return Task.CompletedTask;
            });

            await RunTest("ResolveTierForPersona_NonHighTiers_PassThroughUnchanged", () =>
            {
                AssertEqual("mid", PreferredModelTierSelector.ResolveTierForPersona("mid", "Worker"), "an explicit mid request is preserved");
                AssertEqual("low", PreferredModelTierSelector.ResolveTierForPersona("low", "Worker"), "a low request is preserved verbatim");
                AssertNull(PreferredModelTierSelector.ResolveTierForPersona(null, "Worker"), "a non-specialist with no preferred model is left unset");
                return Task.CompletedTask;
            });

            await RunTest("ResolveTierForPersona_PremiumMinimum_RaisesLowOrUnsetRequest", () =>
            {
                AssertEqual("high", PreferredModelTierSelector.ResolveTierForPersona("high", CaptainTierEnum.Premium), "Premium remains Premium");
                AssertEqual("high", PreferredModelTierSelector.ResolveTierForPersona("mid", CaptainTierEnum.Premium), "Premium raises Standard to Premium");
                AssertEqual("high", PreferredModelTierSelector.ResolveTierForPersona(null, CaptainTierEnum.Premium), "an unset selector resolves to the persona floor");
                AssertEqual("mid", PreferredModelTierSelector.ResolveTierForPersona(null, CaptainTierEnum.Standard), "Test Engineer's Standard floor resolves to mid");
                return Task.CompletedTask;
            });

            await RunTest("ResolveTierForPersona_LiteralModelName_IsNeverRewritten", () =>
            {
                AssertEqual("claude-opus-4-7", PreferredModelTierSelector.ResolveTierForPersona("claude-opus-4-7", "Worker"), "a literal model name is not capped");
                AssertEqual("gpt-5.6-sol", PreferredModelTierSelector.ResolveTierForPersona("gpt-5.6-sol", "Judge"), "a literal model name is not upgraded");
                return Task.CompletedTask;
            });

            await RunTest("ResolveEffectivePreferredModel_InheritsThenAppliesPersonaMinimum", () =>
            {
                IReadOnlyCollection<string> specialists = Settings("Judge").SpecialistPersonas;
                AssertEqual("high", PreferredModelTierSelector.ResolveEffectivePreferredModel(null, "high", "Worker", (CaptainTierEnum?)null), "a mission-level high tier stays a hard floor");
                AssertEqual("high", PreferredModelTierSelector.ResolveEffectivePreferredModel(null, "high", "Judge", CaptainTierEnum.Premium), "a Judge stage keeps high");
                AssertEqual("gpt-5.6-luna", PreferredModelTierSelector.ResolveEffectivePreferredModel(null, "gpt-5.6-luna", "Worker", specialists), "literal pins pass through");
                AssertEqual("claude-opus-5", PreferredModelTierSelector.ResolveEffectivePreferredModel("claude-opus-5", "high", "Worker", specialists), "a stage literal override wins");
                return Task.CompletedTask;
            });

            await RunTest("CouldSelect_TierPin_UsesTheCaptainTier", () =>
            {
                ModelTierSettings settings = new ModelTierSettings();
                Captain standard = MakeCaptain("gpt-5.6-luna", CaptainTierEnum.Standard, 0, "[\"Worker\"]");
                AssertFalse(LegacyCaptainSelector.CouldSelect(settings, new Mission { PreferredModel = "high" }, standard), "a Standard captain does not satisfy a high pin");
                standard.Tier = CaptainTierEnum.Premium;
                AssertTrue(LegacyCaptainSelector.CouldSelect(settings, new Mission { PreferredModel = "high" }, standard), "pinning the captain to Premium lets it satisfy a high pin");
                return Task.CompletedTask;
            });

            await RunTest("CouldSelect_PersonaMinimum_IsAHardFloor", () =>
            {
                Captain standard = MakeCaptain("model-s", CaptainTierEnum.Standard);
                Captain premium = MakeCaptain("model-p", CaptainTierEnum.Premium);
                ModelTierSettings settings = Settings("Judge");
                AssertFalse(LegacyCaptainSelector.CouldSelect(settings, new Mission { Persona = "Judge", PreferredModel = "mid" }, standard), "a specialist needs Premium even for a mid request");
                AssertTrue(LegacyCaptainSelector.CouldSelect(settings, new Mission { Persona = "Judge" }, premium), "a Premium captain serves the specialist");
                AssertFalse(LegacyCaptainSelector.CouldSelect(settings, new Mission { Persona = "Judge", PreferredModel = "model-s" }, standard), "a literal pin below the persona minimum is rejected");
                return Task.CompletedTask;
            });

            await RunTest("CouldSelect_LiteralPinAndPersona_AreEnforced", () =>
            {
                ModelTierSettings settings = new ModelTierSettings();
                Captain captain = MakeCaptain("claude-opus-4-7", CaptainTierEnum.Premium, 0, "[\"Worker\",\"Judge\"]");
                AssertTrue(LegacyCaptainSelector.CouldSelect(settings, new Mission { PreferredModel = "claude-opus-4-7" }, captain), "exact literal model pin is satisfied");
                AssertTrue(LegacyCaptainSelector.CouldSelect(settings, new Mission { Persona = "Judge" }, captain), "an allowed persona with no model pin is satisfied");
                AssertFalse(LegacyCaptainSelector.CouldSelect(settings, new Mission { Persona = "Architect" }, captain), "a persona absent from the allow-list is rejected");
                return Task.CompletedTask;
            });

            await RunTest("VanillaDefaults_NoPremiumMinimumNoNonNativePreferenceNoReserve", () =>
            {
                ModelTierSettings defaults = new ModelTierSettings();
                AssertFalse(defaults.IsSpecialistPersona("Judge"), "vanilla configures no Judge Premium minimum");
                AssertFalse(defaults.PreferNonNativeFirst, "vanilla does not prefer non-native captains");
                AssertEqual(0, defaults.ReservedHighTierSlots, "vanilla reserved high-tier slots is zero");
                AssertFalse(defaults.HasRetiredTierKeys, "vanilla carries no retired tier keys");
                AssertNull(defaults.TierRecordsMigratedUtc, "vanilla has not migrated");
                return Task.CompletedTask;
            });
        }
    }
}
