namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests covering the public dependsOnMissionId entry points (dispatch +
    /// create_mission + update_mission). Verifies the field is persisted when
    /// supplied and rejected when the referenced mission does not exist.
    /// </summary>
    public class DependsOnMissionIdDispatchTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "DependsOnMissionId Dispatch";

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

        private async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string name)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private AdmiralService BuildAdmiral(TestDatabase testDb, LoggingModule logging, ArmadaSettings settings)
        {
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
            IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
            return new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
        }

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Dispatch_WithDependsOnMissionId_PersistsField", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselAsync(testDb, "depends-vessel-1").ConfigureAwait(false);

                    // Pre-existing parent mission referenced by the dependent mission.
                    Mission parent = new Mission("Parent mission", "Foundation work");
                    parent.VesselId = vessel.Id;
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("First", "First description"),
                        new MissionDescription("Second", "Second description") { DependsOnMissionId = parent.Id }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        "Depends Voyage",
                        "Voyage that exercises dependsOnMissionId plumbing",
                        vessel.Id,
                        missions).ConfigureAwait(false);

                    AssertNotNull(voyage, "Voyage should be created");

                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(2, voyageMissions.Count, "Should have 2 missions");

                    Mission? secondMission = voyageMissions.FirstOrDefault(m => m.Title == "Second");
                    AssertNotNull(secondMission, "Second mission should exist");
                    AssertEqual(parent.Id, secondMission!.DependsOnMissionId, "Second mission should depend on parent");

                    Mission? firstMission = voyageMissions.FirstOrDefault(m => m.Title == "First");
                    AssertNotNull(firstMission, "First mission should exist");
                    AssertNull(firstMission!.DependsOnMissionId, "First mission should have no dependency");
                }
            });

            await RunTest("Dispatch_WithUnknownDependsOnMissionId_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselAsync(testDb, "depends-vessel-2").ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Bad dep", "References a mission that does not exist") { DependsOnMissionId = "msn_does_not_exist" }
                    };

                    InvalidOperationException? captured = null;
                    try
                    {
                        await admiral.DispatchVoyageAsync("Bad voyage", "should reject", vessel.Id, missions).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        captured = ex;
                    }

                    AssertNotNull(captured, "Dispatch should reject unknown dependsOnMissionId");
                    AssertContains("dependsOnMissionId not found", captured!.Message, "Error message should identify the failing field");

                    // Verify no mission was persisted before the validation failure.
                    List<Mission> persisted = await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(0, persisted.Count, "No missions should be persisted when validation fails");
                }
            });

            await RunTest("CreateMission_WithDependsOnMissionId_PersistsField", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselAsync(testDb, "create-mission-depends").ConfigureAwait(false);

                    Mission parent = new Mission("Parent", "Foundation");
                    parent.VesselId = vessel.Id;
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    Mission dependent = new Mission("Dependent", "Waits on parent");
                    dependent.VesselId = vessel.Id;
                    dependent.DependsOnMissionId = parent.Id;

                    Mission created = await admiral.DispatchMissionAsync(dependent).ConfigureAwait(false);
                    AssertNotNull(created, "Created mission should be returned");
                    AssertEqual(parent.Id, created.DependsOnMissionId, "DispatchMissionAsync should persist DependsOnMissionId");

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Mission should round-trip through the database");
                    AssertEqual(parent.Id, readBack!.DependsOnMissionId, "Persisted DependsOnMissionId should match");
                }
            });

            await RunTest("UpdateMission_SetsDependsOnMissionId", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Vessel vessel = await CreateVesselAsync(testDb, "update-mission-depends").ConfigureAwait(false);

                    Mission parent = new Mission("Parent", "Foundation");
                    parent.VesselId = vessel.Id;
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    Mission target = new Mission("Will gain a dependency", "Initially independent");
                    target.VesselId = vessel.Id;
                    target = await testDb.Driver.Missions.CreateAsync(target).ConfigureAwait(false);

                    AssertNull(target.DependsOnMissionId, "Mission should start with no dependency");

                    target.DependsOnMissionId = parent.Id;
                    target.LastUpdateUtc = DateTime.UtcNow;
                    Mission updated = await testDb.Driver.Missions.UpdateAsync(target).ConfigureAwait(false);

                    AssertEqual(parent.Id, updated.DependsOnMissionId, "Update should persist DependsOnMissionId");

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(target.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Mission should be readable after update");
                    AssertEqual(parent.Id, readBack!.DependsOnMissionId, "DependsOnMissionId should round-trip after update");

                    // Clearing the dependency should round-trip too.
                    readBack!.DependsOnMissionId = null;
                    readBack.LastUpdateUtc = DateTime.UtcNow;
                    await testDb.Driver.Missions.UpdateAsync(readBack).ConfigureAwait(false);

                    Mission? cleared = await testDb.Driver.Missions.ReadAsync(target.Id).ConfigureAwait(false);
                    AssertNotNull(cleared, "Mission should still exist after clearing dependency");
                    AssertNull(cleared!.DependsOnMissionId, "Cleared dependency should round-trip as null");
                }
            });
        }
    }
}
