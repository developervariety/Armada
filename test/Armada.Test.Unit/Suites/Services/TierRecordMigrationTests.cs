namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The one-time tier record migration: retired tier lists and family rules become captain tiers, the retired
    /// within-tier preference order becomes captain preference ranks, the retired specialist persona list becomes
    /// explicit minimum tiers, and a migrated settings file is never migrated again.
    /// </summary>
    public sealed class TierRecordMigrationTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Tier Record Migration";

        #endregion

        #region Private-Members

        private const string _RetiredSettingsJson = @"{
  ""modelTier"": {
    ""reservedHighTierSlots"": 1,
    ""specialistPersonas"": [ ""Judge"", ""TestEngineer"", ""MissingReviewer"" ],
    ""withinTierStrategy"": ""PreferenceOrderThenRandom"",
    ""midTierModels"": [ ""example/mid-a"", ""example/mid-b"" ],
    ""highTierModels"": [ ""example/high-a"", ""example/high-b"", ""example/high-c"" ],
    ""familyClassificationRules"": [ { ""pattern"": ""^example/family-high-\\d+$"", ""tier"": ""high"" } ],
    ""withinTierPreferenceOrder"": {
      ""high"": [ ""example/high-a"", ""example/high-b"", ""example/high-c"" ],
      ""mid"": [ ""example/mid-b"" ]
    }
  }
}";

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Plan maps the retired lists and family rules onto captain tiers", () =>
            {
                ModelTierSettings retired = Retired();
                List<Captain> captains = Roster();
                TierRecordMigrationResult plan = TierRecordMigrationService.Plan(retired, captains, new List<Persona>());
                Apply(plan, captains);
                AssertEqual(CaptainTierEnum.Premium, Tier(captains, "cpt-high-a"), "high list is Premium");
                AssertEqual(CaptainTierEnum.Standard, Tier(captains, "cpt-mid-a"), "mid list is Standard");
                AssertEqual(CaptainTierEnum.Premium, Tier(captains, "cpt-family"), "a matching family rule is Premium");
                AssertEqual(CaptainTierEnum.Economy, Tier(captains, "cpt-unlisted"), "a model the retired settings never classified is Economy");
                AssertEqual(CaptainTierEnum.Economy, Tier(captains, "cpt-runtime-default"), "a captain on the runtime default model is Economy");
                return Task.CompletedTask;
            });

            await RunTest("Plan maps the retired preference order onto preference ranks, first listed highest", () =>
            {
                List<Captain> captains = Roster();
                Apply(TierRecordMigrationService.Plan(Retired(), captains, new List<Persona>()), captains);
                AssertEqual(3, Rank(captains, "cpt-high-a"));
                AssertEqual(2, Rank(captains, "cpt-high-b"));
                AssertEqual(1, Rank(captains, "cpt-high-c"));
                AssertEqual(1, Rank(captains, "cpt-mid-b"), "a one-model mid list ranks that model 1");
                AssertEqual(0, Rank(captains, "cpt-mid-a"), "an unlisted mid model ranks 0");
                AssertEqual(0, Rank(captains, "cpt-family"), "a family-classified model absent from the list ranks 0");

                ModelTierSettings random = Retired();
                random.RetiredWithinTierStrategy = "Random";
                List<Captain> unranked = Roster();
                Apply(TierRecordMigrationService.Plan(random, unranked, new List<Persona>()), unranked);
                AssertTrue(unranked.All(c => c.PreferenceRank == 0), "the Random strategy never ranked, so no captain is ranked");
                return Task.CompletedTask;
            });

            await RunTest("Plan assigns minimum tiers for retired specialist personas and reports missing names", () =>
            {
                List<Persona> personas = new List<Persona>
                {
                    new Persona("Judge", "persona.judge"),
                    new Persona("Test Engineer", "persona.test_engineer"),
                    new Persona("Worker", "persona.worker"),
                    new Persona("Judge", "persona.judge") { TenantId = "ten_other" }
                };
                TierRecordMigrationResult plan = TierRecordMigrationService.Plan(Retired(), new List<Captain>(), personas);
                AssertEqual(3, plan.Personas.Count, "both Judge records and the canonical Test Engineer record receive minimum tiers");
                AssertEqual(CaptainTierEnum.Premium, TierRecordMigrationService.MinimumTierForPersona("Judge"), "legacy Judge receives Premium");
                AssertEqual(CaptainTierEnum.Standard, TierRecordMigrationService.MinimumTierForPersona("Test Engineer"), "Test Engineer receives Standard");
                AssertFalse(plan.Personas.Any(p => p.Name == "Worker"), "an unlisted persona gets no minimum tier change");
                AssertEqual("MissingReviewer", String.Join(",", plan.UnmatchedSpecialistPersonas), "a listed persona without a record is reported");
                return Task.CompletedTask;
            });

            await RunTest("Plan leaves tiers alone when the retired settings configured no tier membership", () =>
            {
                ModelTierSettings retired = new ModelTierSettings { RetiredSpecialistPersonas = new List<string> { "Judge" }, RetiredWithinTierStrategy = "Random" };
                List<Captain> captains = Roster();
                TierRecordMigrationResult plan = TierRecordMigrationService.Plan(retired, captains, new List<Persona>());
                AssertEqual(0, plan.Captains.Count, "no captain changes without membership lists or rules");
                return Task.CompletedTask;
            });

            await RunTest("The live high order maps to ranks that keep its Judge order", () =>
            {
                ModelTierSettings retired = new ModelTierSettings
                {
                    RetiredHighTierModels = new List<string> { "gpt-6-astra", "claude-fable-5", "claude-fable-5-1" },
                    RetiredWithinTierStrategy = ModelTierSettings.WithinTierStrategyPreferenceOrderThenRandom,
                    RetiredWithinTierPreferenceOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "high", new List<string> { "gpt-6-astra", "claude-fable-5", "claude-fable-5-1" } }
                    }
                };
                List<Captain> captains = new List<Captain>
                {
                    NewCaptain("cpt-fable51", "claude-fable-5-1"),
                    NewCaptain("cpt-astra", "gpt-6-astra"),
                    NewCaptain("cpt-fable", "claude-fable-5")
                };
                Apply(TierRecordMigrationService.Plan(retired, captains, new List<Persona>()), captains);
                AssertEqual("3,2,1", Rank(captains, "cpt-astra") + "," + Rank(captains, "cpt-fable") + "," + Rank(captains, "cpt-fable51"));
                ModelTierSettings tiers = new ModelTierSettings
                {
                    Records = TierRoutingRecords.ForMinimumTiers(new[]
                    {
                        new KeyValuePair<string, CaptainTierEnum>("Judge", CaptainTierEnum.Premium)
                    })
                };
                Mission judge = new Mission { Persona = "Judge", PreferredModel = "high" };
                AssertEqual("cpt-astra,cpt-fable,cpt-fable51", String.Join(",", LegacyCaptainSelector.Order(tiers, judge, captains, false, n => 0).Select(c => c.Id)));
                return Task.CompletedTask;
            });

            await RunTest("RunAsync applies the records once, backs up the settings file, removes the retired keys and stamps the settings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string directory = Path.Combine(Path.GetTempPath(), "armada_tier_migration_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "settings.json");
                    try
                    {
                        await File.WriteAllTextAsync(path, _RetiredSettingsJson);
                        foreach (Captain captain in Roster()) await testDb.Driver.Captains.CreateAsync(captain);
                        Persona judge = await testDb.Driver.Personas.CreateAsync(new Persona("Judge", "persona.judge"));
                        Persona worker = await testDb.Driver.Personas.CreateAsync(new Persona("Worker", "persona.worker"));

                        ArmadaSettings settings = await ArmadaSettings.LoadAsync(path);
                        AssertTrue(settings.ModelTier.HasRetiredTierKeys, "the retired keys load");
                        TierRecordMigrationService service = new TierRecordMigrationService(testDb.Driver, Logging());
                        TierRecordMigrationResult first = await service.RunAsync(settings, path);

                        AssertEqual(TierRecordMigrationResult.OutcomeMigrated, first.Outcome);
                        Captain? highA = await testDb.Driver.Captains.ReadAsync("cpt-high-a");
                        AssertEqual(CaptainTierEnum.Premium, highA!.Tier, "the tier persists");
                        AssertEqual(3, highA.PreferenceRank, "the rank persists");
                        AssertEqual(CaptainTierEnum.Premium, (await testDb.Driver.Personas.ReadAsync(judge.Id))!.MinimumTier, "the Judge Premium minimum persists");
                        AssertNull((await testDb.Driver.Personas.ReadAsync(worker.Id))!.MinimumTier, "an unlisted persona stays unset");

                        AssertNotNull(first.SettingsBackupPath, "a backup is written");
                        AssertEqual(_RetiredSettingsJson, await File.ReadAllTextAsync(first.SettingsBackupPath!), "the backup is the original file");
                        string saved = await File.ReadAllTextAsync(path);
                        foreach (string key in new[] { "midTierModels", "highTierModels", "familyClassificationRules", "specialistPersonas", "withinTierStrategy", "withinTierPreferenceOrder" })
                            AssertFalse(saved.Contains("\"" + key + "\"", StringComparison.Ordinal), key + " is removed from the saved settings");
                        AssertTrue(saved.Contains("\"tierRecordsMigratedUtc\"", StringComparison.Ordinal), "the migration stamp is saved");
                        AssertTrue(saved.Contains("\"reservedHighTierSlots\": 1", StringComparison.Ordinal), "reservedHighTierSlots stays a setting");

                        ArmadaSettings reloaded = await ArmadaSettings.LoadAsync(path);
                        TierRecordMigrationResult second = await service.RunAsync(reloaded, path);
                        AssertEqual(TierRecordMigrationResult.OutcomeAlreadyMigrated, second.Outcome, "a stamped settings file is not migrated again");
                        AssertEqual(1, Directory.GetFiles(directory, "settings.json" + TierRecordMigrationService.BackupInfix + "*").Length, "no second backup");
                    }
                    finally
                    {
                        Directory.Delete(directory, true);
                    }
                }
            });

            await RunTest("ApplyAsync is idempotent: applying the same retired settings again changes nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    foreach (Captain captain in Roster()) await testDb.Driver.Captains.CreateAsync(captain);
                    await testDb.Driver.Personas.CreateAsync(new Persona("Judge", "persona.judge"));
                    TierRecordMigrationService service = new TierRecordMigrationService(testDb.Driver, Logging());
                    TierRecordMigrationResult first = await service.ApplyAsync(Retired());
                    AssertTrue(first.Captains.Count > 0 && first.Personas.Count == 1, "the first run changes records");
                    TierRecordMigrationResult second = await service.ApplyAsync(Retired());
                    AssertEqual(0, second.Captains.Count, "no captain changes on the second run");
                    AssertEqual(0, second.Personas.Count, "no persona changes on the second run");
                }
            });

            await RunTest("Retired keys in a migrated settings file load and are ignored", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string directory = Path.Combine(Path.GetTempPath(), "armada_tier_migration_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "settings.json");
                    try
                    {
                        string stamped = _RetiredSettingsJson.Replace("\"reservedHighTierSlots\": 1,", "\"reservedHighTierSlots\": 1, \"tierRecordsMigratedUtc\": \"2026-01-01T00:00:00Z\",");
                        await File.WriteAllTextAsync(path, stamped);
                        Captain standard = NewCaptain("cpt-standard-on-listed-high", "example/high-a");
                        standard.Tier = CaptainTierEnum.Standard;
                        await testDb.Driver.Captains.CreateAsync(standard);
                        Captain premium = NewCaptain("cpt-premium", "example/other");
                        premium.Tier = CaptainTierEnum.Premium;
                        await testDb.Driver.Captains.CreateAsync(premium);

                        ArmadaSettings settings = await ArmadaSettings.LoadAsync(path);
                        AssertTrue(settings.ModelTier.HasRetiredTierKeys, "the retired keys still load");
                        TierRecordMigrationResult result = await new TierRecordMigrationService(testDb.Driver, Logging()).RunAsync(settings, path);
                        AssertEqual(TierRecordMigrationResult.OutcomeAlreadyMigrated, result.Outcome);
                        AssertEqual(CaptainTierEnum.Standard, (await testDb.Driver.Captains.ReadAsync(standard.Id))!.Tier, "the record keeps the operator's tier");
                        AssertEqual(stamped, await File.ReadAllTextAsync(path), "the settings file is not rewritten");

                        await TierRoutingRecords.RefreshAsync(settings.ModelTier, testDb.Driver);
                        List<Captain> pool = await testDb.Driver.Captains.EnumerateAsync();
                        Mission high = new Mission { Persona = "Worker", PreferredModel = "high" };
                        AssertEqual(premium.Id, LegacyCaptainSelector.Select(settings.ModelTier, high, pool, false, n => 0)!.Id, "routing reads the captain tier, not the retired high list");
                        AssertFalse(settings.ModelTier.IsSpecialistPersona("Judge"), "the retired specialist list does not make Judge a specialist");
                    }
                    finally
                    {
                        Directory.Delete(directory, true);
                    }
                }
            });

            await RunTest("RunAsync without retired keys writes nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string directory = Path.Combine(Path.GetTempPath(), "armada_tier_migration_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "settings.json");
                    try
                    {
                        string plain = "{ \"modelTier\": { \"reservedHighTierSlots\": 2 } }";
                        await File.WriteAllTextAsync(path, plain);
                        ArmadaSettings settings = await ArmadaSettings.LoadAsync(path);
                        TierRecordMigrationResult result = await new TierRecordMigrationService(testDb.Driver, Logging()).RunAsync(settings, path);
                        AssertEqual(TierRecordMigrationResult.OutcomeNothingToMigrate, result.Outcome);
                        AssertEqual(plain, await File.ReadAllTextAsync(path), "the file is untouched");
                        AssertEqual(1, Directory.GetFiles(directory).Length, "no backup is written");
                    }
                    finally
                    {
                        Directory.Delete(directory, true);
                    }
                }
            });
        }

        #endregion

        #region Private-Methods

        private static ModelTierSettings Retired()
        {
            ArmadaSettings settings = System.Text.Json.JsonSerializer.Deserialize<ArmadaSettings>(_RetiredSettingsJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;
            return settings.ModelTier;
        }

        private static List<Captain> Roster()
        {
            return new List<Captain>
            {
                NewCaptain("cpt-high-a", "example/high-a"),
                NewCaptain("cpt-high-b", "example/high-b"),
                NewCaptain("cpt-high-c", "example/high-c"),
                NewCaptain("cpt-mid-a", "example/mid-a"),
                NewCaptain("cpt-mid-b", "example/mid-b"),
                NewCaptain("cpt-family", "example/family-high-7"),
                NewCaptain("cpt-unlisted", "example/unlisted"),
                NewCaptain("cpt-runtime-default", null)
            };
        }

        private static Captain NewCaptain(string id, string? model)
        {
            return new Captain(id) { Id = id, Model = model, State = CaptainStateEnum.Idle };
        }

        private static void Apply(TierRecordMigrationResult plan, List<Captain> captains)
        {
            foreach (CaptainTierMigrationChange change in plan.Captains)
            {
                Captain captain = captains.First(c => c.Id == change.CaptainId);
                captain.Tier = change.Tier;
                captain.PreferenceRank = change.Rank;
            }
        }

        private static CaptainTierEnum Tier(List<Captain> captains, string id) => CaptainTierSelector.EffectiveTier(captains.First(c => c.Id == id));

        private static int Rank(List<Captain> captains, string id) => captains.First(c => c.Id == id).PreferenceRank;

        private static LoggingModule Logging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        #endregion
    }
}
