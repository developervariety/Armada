namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// A voyage halted after one of its missions fails is cancelled through the one voyage cancel: the captain of every
    /// parallel mission still running is recalled, so its agent process stops and the captain is released, before the
    /// mission is written Cancelled.
    /// </summary>
    public class VoyageHaltRecallTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Voyage Halt Recall";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("HaltedVoyage_RecallsTheCaptainOfARunningParallelMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    List<string> stopped = new List<string>();
                    AdmiralService admiral = CreateAdmiralService(testDb.Driver, settings, stopped);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("halt-voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Captain failingCaptain = await testDb.Driver.Captains.CreateAsync(new Captain("halt-failing") { State = CaptainStateEnum.Working, ProcessId = 51001 }).ConfigureAwait(false);
                    Captain parallelCaptain = await testDb.Driver.Captains.CreateAsync(new Captain("halt-parallel") { State = CaptainStateEnum.Working, ProcessId = 51002 }).ConfigureAwait(false);
                    Mission failing = await testDb.Driver.Missions.CreateAsync(new Mission("[Worker] failing", "work")
                    {
                        VoyageId = voyage.Id,
                        CaptainId = failingCaptain.Id,
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = 51001,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-20)
                    }).ConfigureAwait(false);
                    Mission parallel = await testDb.Driver.Missions.CreateAsync(new Mission("[Worker] parallel", "work")
                    {
                        VoyageId = voyage.Id,
                        CaptainId = parallelCaptain.Id,
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = 51002,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-20)
                    }).ConfigureAwait(false);
                    failingCaptain.CurrentMissionId = failing.Id;
                    await testDb.Driver.Captains.UpdateAsync(failingCaptain).ConfigureAwait(false);
                    parallelCaptain.CurrentMissionId = parallel.Id;
                    await testDb.Driver.Captains.UpdateAsync(parallelCaptain).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(51001, 1, failingCaptain.Id, failing.Id).ConfigureAwait(false);

                    Voyage? storedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Cancelled, storedVoyage!.Status, "the voyage is halted");
                    Mission? storedParallel = await testDb.Driver.Missions.ReadAsync(parallel.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Cancelled, storedParallel!.Status, "the parallel mission is cancelled with the voyage");
                    AssertTrue(stopped.Contains(parallelCaptain.Id),
                        "the parallel mission's agent process is stopped: " + String.Join(",", stopped));
                    Captain? storedParallelCaptain = await testDb.Driver.Captains.ReadAsync(parallelCaptain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, storedParallelCaptain!.State, "the parallel mission's captain is released");
                    AssertNull(storedParallelCaptain.CurrentMissionId, "the released captain holds no mission");
                }
            }).ConfigureAwait(false);
        }

        private static AdmiralService CreateAdmiralService(SqliteDatabaseDriver db, ArmadaSettings settings, List<string> stopped)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            captainService.OnStopAgent = captain =>
            {
                lock (stopped) stopped.Add(captain.Id);
                return Task.CompletedTask;
            };
            IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, null, git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService);
        }
    }
}
