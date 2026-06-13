namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class ContextPackUsageStatusTests : TestSuite
    {
        public override string Name => "Context Pack Usage Status";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private AdmiralService CreateAdmiralService(LoggingModule logging, TestDatabase testDb, ArmadaSettings settings)
        {
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
            IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
            return new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
        }

        private static async Task CreateUsageEventAsync(
            TestDatabase testDb,
            string missionId,
            bool contextPackStaged,
            string compliance,
            int searchToolCallCount)
        {
            string payload = JsonSerializer.Serialize(new
            {
                MissionId = missionId,
                LogAvailable = true,
                ContextPackStaged = contextPackStaged,
                ContextPackCompliance = compliance,
                SearchToolCallCount = searchToolCallCount
            });

            ArmadaEvent evt = new ArmadaEvent(ContextPackUsageSummary.EventType, "Context pack usage: " + compliance);
            evt.EntityType = "mission";
            evt.EntityId = missionId;
            evt.MissionId = missionId;
            evt.Payload = payload;
            await testDb.Driver.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("GetStatusAsync_WithUsageEvents_PopulatesContextPackUsage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService service = CreateAdmiralService(logging, testDb, settings);

                    await CreateUsageEventAsync(testDb, "msn_usage_a", true, "ReadBeforeSearch", 1).ConfigureAwait(false);
                    await CreateUsageEventAsync(testDb, "msn_usage_b", true, "SearchBeforeRead", 2).ConfigureAwait(false);
                    await CreateUsageEventAsync(testDb, "msn_usage_c", false, "NoPackStagedNoSearch", 0).ConfigureAwait(false);

                    ArmadaStatus status = await service.GetStatusAsync().ConfigureAwait(false);

                    AssertNotNull(status.ContextPackUsage, "ContextPackUsage should be populated when events exist");
                    AssertEqual(3, status.ContextPackUsage!.MissionsConsidered);
                    AssertTrue(
                        Math.Abs(status.ContextPackUsage.PackStagedShare - (2.0 / 3.0)) < 0.0001,
                        "PackStagedShare should reflect staged missions");
                    AssertEqual(0.5, status.ContextPackUsage.ReadBeforeSearchShare);
                    AssertTrue(
                        Math.Abs(status.ContextPackUsage.AverageSearchToolCalls - 1.0) < 0.0001,
                        "AverageSearchToolCalls should average across considered missions");
                }
            });

            await RunTest("GetStatusAsync_WithoutUsageEvents_LeavesContextPackUsageNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService service = CreateAdmiralService(logging, testDb, settings);

                    ArmadaStatus status = await service.GetStatusAsync().ConfigureAwait(false);

                    AssertNull(status.ContextPackUsage, "ContextPackUsage should stay null when no usage events exist");
                }
            });
        }
    }
}
