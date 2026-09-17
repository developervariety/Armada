namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Pins the captain each representative routing case selects for one settings file written with the
    /// model tier lists, the within-tier preference order and the specialist persona list. The same
    /// assertions hold for any build that reads that settings file, so they guard selection behaviour
    /// across a change to where tier membership, rank and specialist status are stored.
    /// </summary>
    public sealed class TierRoutingCharacterizationTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Tier Routing Characterization";

        #endregion

        #region Private-Members

        private const string _SettingsJson = @"{
  ""specialistPersonas"": [ ""Judge"", ""Architect"", ""TestEngineer"" ],
  ""reservedHighTierSlots"": 1,
  ""preferNonNativeFirst"": true,
  ""withinTierStrategy"": ""PreferenceOrderThenRandom"",
  ""midTierModels"": [ ""gpt-5.6-luna"", ""opencode-go/deepseek-v4-flash"", ""composer-2.5"", ""example/mid-external"" ],
  ""highTierModels"": [ ""gpt-6-astra"", ""claude-fable-5"", ""claude-fable-5-1"" ],
  ""withinTierPreferenceOrder"": {
    ""high"": [ ""gpt-6-astra"", ""claude-fable-5"", ""claude-fable-5-1"" ],
    ""mid"": [ ""opencode-go/deepseek-v4-flash"", ""gpt-5.6-luna"" ]
  }
}";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Worker mid follows the mid preference order, then non-native first, then the high order", async () =>
            {
                RoutingFixture fixture = await PrepareAsync(Roster()).ConfigureAwait(false);
                Mission mission = new Mission { Id = "msn-worker_mid", Persona = "Worker", PreferredModel = "mid" };
                foreach (Func<int, int> pick in Picks())
                {
                    List<string> order = Ids(LegacyCaptainSelector.Order(fixture.Tiers, mission, Idle(fixture), false, pick));
                    AssertSequence(new List<string> { "cpt-deepseek", "cpt-luna-ext", "cpt-luna", "cpt-mid-ext", "cpt-composer", "cpt-astra", "cpt-fable", "cpt-fable51" }, order, "Worker mid order");
                }
            });

            await RunTest("Worker without a preferred model reaches an unlisted model only after every tier captain", async () =>
            {
                RoutingFixture fixture = await PrepareAsync(Roster()).ConfigureAwait(false);
                Mission mission = new Mission { Id = "msn-worker_default", Persona = "Worker" };
                List<string> order = Ids(LegacyCaptainSelector.Order(fixture.Tiers, mission, Idle(fixture), false, n => 0));
                AssertEqual("cpt-deepseek", order[0], "the default tier is mid");
                AssertEqual("cpt-unlisted", order[order.Count - 1], "an unlisted model is the last resort");

                Mission mid = new Mission { Id = "msn-worker_mid_only", Persona = "Worker", PreferredModel = "mid" };
                AssertFalse(LegacyCaptainSelector.Order(fixture.Tiers, mid, Idle(fixture), false, n => 0).Any(c => c.Id == "cpt-unlisted"),
                    "a mid request never reaches an unlisted model");
            });

            await RunTest("Judge high follows the ranked high order", async () =>
            {
                RoutingFixture fixture = await PrepareAsync(Roster()).ConfigureAwait(false);
                Mission mission = new Mission { Id = "msn-judge", Persona = "Judge", PreferredModel = "high" };
                foreach (Func<int, int> pick in Picks())
                {
                    List<string> order = Ids(LegacyCaptainSelector.Order(fixture.Tiers, mission, Idle(fixture), false, pick));
                    AssertSequence(new List<string> { "cpt-astra", "cpt-fable", "cpt-fable51" }, order, "Judge order");
                }

                List<Captain> withoutAstra = Idle(fixture).Where(c => c.Id != "cpt-astra").ToList();
                AssertEqual("cpt-fable", LegacyCaptainSelector.Select(fixture.Tiers, mission, withoutAstra, false, n => 0)!.Id, "the next rank takes over");
            });

            await RunTest("A specialist persona is forced to the high tier", async () =>
            {
                RoutingFixture fixture = await PrepareAsync(Roster()).ConfigureAwait(false);
                foreach (string preferred in new string[] { "mid", "low" })
                {
                    Mission mission = new Mission { Id = "msn-specialist", Persona = "TestEngineer", PreferredModel = preferred };
                    List<string> order = Ids(LegacyCaptainSelector.Order(fixture.Tiers, mission, Idle(fixture), false, n => 0));
                    AssertSequence(new List<string> { "cpt-astra", "cpt-fable", "cpt-fable51" }, order, "specialist order for " + preferred);
                }

                Mission unpinned = new Mission { Id = "msn-specialist_default", Persona = "TestEngineer" };
                AssertEqual("cpt-astra", LegacyCaptainSelector.Select(fixture.Tiers, unpinned, Idle(fixture), false, n => 0)!.Id, "a specialist without a preferred model starts at the high tier");

                AssertEqual("high", PreferredModelTierSelector.ResolveTierForPersona("mid", "Judge", fixture.Tiers.SpecialistPersonas), "a specialist stage upgrades mid");
                AssertEqual("mid", PreferredModelTierSelector.ResolveTierForPersona("high", "Worker", fixture.Tiers.SpecialistPersonas), "a Worker stage caps high");
            });

            await RunTest("A concrete model pin takes that model, or the pinned model's tier when none is idle", async () =>
            {
                RoutingFixture fixture = await PrepareAsync(Roster()).ConfigureAwait(false);
                Mission pinned = new Mission { Id = "msn-pin", Persona = "Worker", PreferredModel = "claude-fable-5-1" };
                AssertEqual("cpt-fable51", LegacyCaptainSelector.Select(fixture.Tiers, pinned, Idle(fixture), false, n => 0)!.Id, "exact model match");

                List<Captain> withoutFable = Idle(fixture).Where(c => c.Id != "cpt-fable").ToList();
                Mission fallback = new Mission { Id = "msn-pin_fallback", Persona = "Worker", PreferredModel = "claude-fable-5" };
                AssertEqual("cpt-astra", LegacyCaptainSelector.Select(fixture.Tiers, fallback, withoutFable, false, n => 0)!.Id, "busy pin falls back within its tier by rank");
            });

            await RunTest("Unranked equal peers prefer the non-native captain whatever the random pick", async () =>
            {
                RoutingFixture fixture = await PrepareAsync(Roster()).ConfigureAwait(false);
                List<Captain> pool = Idle(fixture).Where(c => c.Id == "cpt-composer" || c.Id == "cpt-mid-ext").ToList();
                Mission mission = new Mission { Id = "msn-tie", Persona = "Worker", PreferredModel = "mid" };
                foreach (Func<int, int> pick in Picks())
                    AssertEqual("cpt-mid-ext", LegacyCaptainSelector.Select(fixture.Tiers, mission, pool, false, pick)!.Id, "non-native first");
            });

            await RunTest("Smart Routing keeps persona model groups over the legacy order and removes an exhausted account", async () =>
            {
                RoutingFixture fixture = await PrepareAsync(Roster()).ConfigureAwait(false);
                UsageRoutingSettings policy = new UsageRoutingSettings
                {
                    Enabled = true,
                    Accounts = new List<UsageAccountSettings> { Account("exhausted", 0, "cpt-luna", "cpt-luna-ext") }
                };
                policy.PersonaModels["Worker"] = new PersonaModelSettings
                {
                    Default = new List<string> { "gpt-5.6-luna" },
                    Lighter = new List<string> { "composer-2.5" },
                    Stronger = new List<string> { "claude-fable-5" }
                };
                Mission mission = new Mission { Id = "msn-smart", Persona = "Worker", PreferredModel = "mid", Priority = 100, Title = "work" };
                UsageRoutingDecision decision = await SmartRoutingSelector.SelectAsync(new SmartRoutingRequest
                {
                    Tiers = fixture.Tiers,
                    Policy = policy,
                    Usage = new UsageRoutingService(),
                    Mission = mission,
                    Pool = Idle(fixture),
                    NowUtc = DateTime.UtcNow,
                    RandomPick = n => 0
                }).ConfigureAwait(false);
                AssertSequence(new List<string> { "cpt-deepseek", "cpt-luna-ext", "cpt-luna", "cpt-mid-ext", "cpt-composer", "cpt-astra", "cpt-fable", "cpt-fable51" }, Ids(decision.LegacyOrder), "legacy order");
                AssertEqual("removed", decision.Verdicts.First(v => v.CaptainId == "cpt-luna").Outcome);
                AssertSequence(new List<string> { "cpt-fable", "cpt-composer", "cpt-deepseek", "cpt-mid-ext", "cpt-astra", "cpt-fable51" }, Ids(decision.Candidates), "grouped candidates");
                AssertEqual("cpt-fable", decision.Candidates[0].Id, "the Stronger list follows the empty Default list");
            });

            await RunTest("A reserved high-tier slot defers a Worker once a Judge holds the other high captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_tier_char_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_tier_char_repos_" + Guid.NewGuid().ToString("N"));

                    await testDb.Driver.Personas.CreateAsync(new Persona("Judge", "persona.judge")).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(new Captain("reserve-fable-a") { Model = "claude-fable-5", State = CaptainStateEnum.Idle }).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(new Captain("reserve-fable-b") { Model = "claude-fable-5-1", State = CaptainStateEnum.Idle }).ConfigureAwait(false);
                    await ApplySettingsAsync(settings, testDb.Driver).ConfigureAwait(false);

                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    captains.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captains, missions, new VoyageService(logging, testDb.Driver), docks);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("tier-char-vessel", "https://github.com/test/repo.git") { DefaultBranch = "main", AllowConcurrentMissions = true };
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("Judge review", "review") { VesselId = vessel.Id, Persona = "Judge", Status = MissionStatusEnum.Pending }).ConfigureAwait(false);
                    Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("Worker", "work") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);

                    await admiral.HealthCheckAsync().ConfigureAwait(false);
                    await Task.Delay(200).ConfigureAwait(false);

                    Mission? judgeAfter = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertTrue(judgeAfter!.Status == MissionStatusEnum.Assigned || judgeAfter.Status == MissionStatusEnum.InProgress, "the Judge dispatches first (got " + judgeAfter.Status + ")");
                    Mission? workerAfter = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, workerAfter!.Status, "the Worker waits for the reserved high-tier captain");
                    AssertEqual(1, (await testDb.Driver.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle).ConfigureAwait(false)).Count, "one high-tier captain stays idle");
                }
            });
        }

        #endregion

        #region Private-Methods

        private static List<Captain> Roster()
        {
            return new List<Captain>
            {
                NewCaptain("cpt-astra", "gpt-6-astra", AgentRuntimeEnum.Codex, null),
                NewCaptain("cpt-fable", "claude-fable-5", AgentRuntimeEnum.ClaudeCode, null),
                NewCaptain("cpt-fable51", "claude-fable-5-1", AgentRuntimeEnum.ClaudeCode, null),
                NewCaptain("cpt-luna", "gpt-5.6-luna", AgentRuntimeEnum.Codex, null),
                NewCaptain("cpt-luna-ext", "gpt-5.6-luna", AgentRuntimeEnum.Codex, "https://provider.example/v1"),
                NewCaptain("cpt-deepseek", "opencode-go/deepseek-v4-flash", AgentRuntimeEnum.OpenCode, null),
                NewCaptain("cpt-composer", "composer-2.5", AgentRuntimeEnum.Cursor, null),
                NewCaptain("cpt-mid-ext", "example/mid-external", AgentRuntimeEnum.ClaudeCode, "https://provider.example/v1"),
                NewCaptain("cpt-unlisted", "example/unlisted", AgentRuntimeEnum.ClaudeCode, null)
            };
        }

        private static Captain NewCaptain(string id, string model, AgentRuntimeEnum runtime, string? apiBaseUrl)
        {
            return new Captain(id) { Id = id, Model = model, Runtime = runtime, ApiBaseUrl = apiBaseUrl, State = CaptainStateEnum.Idle };
        }

        private static List<Captain> Idle(RoutingFixture fixture)
        {
            return new List<Captain>(fixture.Captains);
        }

        private static IEnumerable<Func<int, int>> Picks()
        {
            yield return n => 0;
            yield return n => n - 1;
        }

        private static UsageAccountSettings Account(string id, double remaining, params string[] captainIds)
        {
            return new UsageAccountSettings
            {
                Id = id,
                CaptainIds = new List<string>(captainIds),
                ManualSnapshot = new ProviderUsageSnapshot
                {
                    ObservedUtc = DateTime.UtcNow,
                    Source = "test",
                    Windows = new List<ProviderUsageWindow> { new ProviderUsageWindow { Name = "weekly", RemainingPercent = remaining, ResetsUtc = DateTime.UtcNow.AddDays(1) } }
                }
            };
        }

        private static ModelTierSettings LoadSettings()
        {
            return JsonSerializer.Deserialize<ModelTierSettings>(_SettingsJson, _JsonOptions)!;
        }

        /// <summary>
        /// The routing inputs a build derives from the settings file and the captain roster: the tier record
        /// migration moves the retired tier keys onto the captain and persona records.
        /// </summary>
        private static Task<RoutingFixture> PrepareAsync(List<Captain> roster)
        {
            ModelTierSettings tiers = LoadSettings();
            List<Persona> personas = new List<Persona> { new Persona("Judge", "persona.judge"), new Persona("Architect", "persona.architect"), new Persona("TestEngineer", "persona.test_engineer") };
            TierRecordMigrationResult plan = TierRecordMigrationService.Plan(tiers, roster, personas);
            foreach (CaptainTierMigrationChange change in plan.Captains)
            {
                Captain captain = roster.First(c => c.Id == change.CaptainId);
                captain.Tier = change.Tier;
                captain.PreferenceRank = change.Rank;
            }
            foreach (PersonaSpecialistMigrationChange change in plan.Personas)
                personas.First(p => p.Id == change.PersonaId).Specialist = true;
            tiers.Records = TierRoutingRecords.From(personas, roster);
            RoutingFixture fixture = new RoutingFixture { Tiers = tiers, Captains = roster };
            return Task.FromResult(fixture);
        }

        /// <summary>Apply the settings file to a live settings instance backed by a database through the tier record migration.</summary>
        private static async Task ApplySettingsAsync(ArmadaSettings settings, DatabaseDriver database)
        {
            settings.ModelTier.CopyFrom(LoadSettings());
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            await new TierRecordMigrationService(database, logging).ApplyAsync(LoadSettings()).ConfigureAwait(false);
            await TierRoutingRecords.RefreshAsync(settings.ModelTier, database).ConfigureAwait(false);
        }

        private static List<string> Ids(IEnumerable<Captain> captains) => captains.Select(c => c.Id).ToList();

        private void AssertSequence(List<string> expected, List<string> actual, string label)
        {
            AssertEqual(String.Join(",", expected), String.Join(",", actual), label);
        }

        #endregion

        #region Private-Types

        private sealed class RoutingFixture
        {
            public ModelTierSettings Tiers { get; set; } = new ModelTierSettings();

            public List<Captain> Captains { get; set; } = new List<Captain>();
        }

        #endregion
    }
}
