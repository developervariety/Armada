namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests that a halted chain cancels everything behind it rather than leaving stages Pending.
    /// </summary>
    /// <remarks>
    /// A cancelled dependent is itself a dependency. Stopping after one level leaves every later
    /// stage waiting on a mission that can never run, so the voyage still reads InProgress while
    /// nothing in it can advance -- unrecoverable, not slow.
    /// </remarks>
    public class DependencyChainCancellationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Dependency Chain Cancellation";

        private static AdmiralService CreateAdmiralService(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, null, git);
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService);
        }

        private static async Task<Mission> AddStageAsync(
            TestDatabase testDb, Voyage voyage, string title, string? dependsOn, MissionStatusEnum status)
        {
            Mission mission = new Mission(title, "work");
            mission.VoyageId = voyage.Id;
            mission.DependsOnMissionId = dependsOn;
            mission.Status = status;
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>Run all dependency chain cancellation tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A halted chain cancels every stage behind it, not only the first", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("chain-voyage")).ConfigureAwait(false);

                    Mission failed = await AddStageAsync(testDb, voyage, "[Worker] one", null, MissionStatusEnum.Failed).ConfigureAwait(false);
                    Mission judge1 = await AddStageAsync(testDb, voyage, "[Judge] one", failed.Id, MissionStatusEnum.Pending).ConfigureAwait(false);
                    Mission worker2 = await AddStageAsync(testDb, voyage, "[Worker] two", judge1.Id, MissionStatusEnum.Pending).ConfigureAwait(false);
                    Mission judge2 = await AddStageAsync(testDb, voyage, "[Judge] two", worker2.Id, MissionStatusEnum.Pending).ConfigureAwait(false);

                    AdmiralService admiral = CreateAdmiralService(testDb.Driver, settings);
                    await admiral.CancelDirectDependentsAsync(failed, "upstream failed", default).ConfigureAwait(false);

                    foreach (Mission stage in new[] { judge1, worker2, judge2 })
                    {
                        Mission? reloaded = await testDb.Driver.Missions.ReadAsync(stage.Id).ConfigureAwait(false);
                        AssertNotNull(reloaded, "Stage should remain readable");
                        AssertEqual(
                            MissionStatusEnum.Cancelled,
                            reloaded!.Status,
                            "Stage '" + stage.Title + "' waits on a mission that can never run and must be cancelled");
                    }
                }
            });

            await RunTest("A stage that already produced work is not cancelled, and shields its dependents", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("chain-voyage")).ConfigureAwait(false);

                    Mission failed = await AddStageAsync(testDb, voyage, "[Worker] one", null, MissionStatusEnum.Failed).ConfigureAwait(false);
                    Mission produced = await AddStageAsync(testDb, voyage, "[Worker] two", failed.Id, MissionStatusEnum.WorkProduced).ConfigureAwait(false);
                    Mission downstream = await AddStageAsync(testDb, voyage, "[Judge] two", produced.Id, MissionStatusEnum.Pending).ConfigureAwait(false);

                    AdmiralService admiral = CreateAdmiralService(testDb.Driver, settings);
                    await admiral.CancelDirectDependentsAsync(failed, "upstream failed", default).ConfigureAwait(false);

                    Mission? reloadedProduced = await testDb.Driver.Missions.ReadAsync(produced.Id).ConfigureAwait(false);
                    AssertEqual(
                        MissionStatusEnum.WorkProduced,
                        reloadedProduced!.Status,
                        "Work that exists must not be discarded because an upstream stage failed");

                    Mission? reloadedDownstream = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertEqual(
                        MissionStatusEnum.Pending,
                        reloadedDownstream!.Status,
                        "A stage whose own dependency produced work can still run");
                }
            });
        }
    }
}
