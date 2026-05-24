namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
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
    /// Tests for background-assignment dispatch and AssignmentState transitions.
    /// Verifies that DispatchVoyageAsync returns immediately (before dock provisioning
    /// completes) and that TryAssignAsync writes the correct MissionAssignmentStateEnum
    /// value at every gate in the assignment pipeline.
    /// </summary>
    public class DispatchAssignmentStateTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Dispatch Assignment State";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Dispatch_ReturnsBeforeDockProvisioningCompletes_WithinTightBound", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    DelayingDockService delayingDock = new DelayingDockService(realDock, delayMs: 2000);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, delayingDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, delayingDock, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, delayingDock);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("timing-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("timing-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Timing probe", Description = "Checks dispatch returns fast." }
                    };

                    Stopwatch sw = Stopwatch.StartNew();
                    Voyage voyage = await admiral.DispatchVoyageAsync("Timing voyage", "Test", vessel.Id, missions).ConfigureAwait(false);
                    sw.Stop();

                    AssertTrue(sw.ElapsedMilliseconds < 500, "DispatchVoyageAsync must return in under 500ms even with 2s dock delay (actual: " + sw.ElapsedMilliseconds + "ms)");
                    AssertNotNull(voyage, "Voyage must be returned");

                    // Give the background task a moment then clean up
                    await Task.Delay(100).ConfigureAwait(false);
                }
            });

            await RunTest("Dispatch_PollsThroughTransitions_PendingThenProvisioningThenAssigned", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    DelayingDockService delayingDock = new DelayingDockService(realDock, delayMs: 500);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, delayingDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, delayingDock, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, delayingDock);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("transition-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("transition-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Transition probe", Description = "Observes state transitions." }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync("Transition voyage", "Test", vessel.Id, missions).ConfigureAwait(false);

                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertTrue(voyageMissions.Count == 1, "Voyage should have one mission");
                    string missionId = voyageMissions[0].Id;

                    bool sawProvisioning = false;
                    bool sawAssigned = false;
                    Stopwatch poll = Stopwatch.StartNew();

                    while (poll.ElapsedMilliseconds < 5000)
                    {
                        Mission? m = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                        if (m == null) break;

                        if (m.AssignmentState == MissionAssignmentStateEnum.Provisioning)
                            sawProvisioning = true;

                        if (m.AssignmentState == MissionAssignmentStateEnum.Assigned)
                        {
                            sawAssigned = true;
                            break;
                        }

                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertTrue(sawAssigned, "Mission must reach AssignmentState=Assigned within 5s");
                    AssertTrue(sawProvisioning, "Mission must pass through AssignmentState=Provisioning (500ms delay makes it observable)");
                }
            });

            await RunTest("Dispatch_SerialVessel_SecondMissionShowsWaitingForVesselMutex", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    // Serial vessel: AllowConcurrentMissions=false (default)
                    Vessel vessel = new Vessel("serial-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = false;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // Two captains so the first mission can be assigned
                    Captain captain1 = new Captain("serial-captain-1");
                    captain1.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain1).ConfigureAwait(false);

                    Captain captain2 = new Captain("serial-captain-2");
                    captain2.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain2).ConfigureAwait(false);

                    // Dispatch first mission and wait for it to go InProgress
                    List<MissionDescription> firstBatch = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "First mission", Description = "Occupies the serial vessel." }
                    };
                    Voyage voyage1 = await admiral.DispatchVoyageAsync("Voyage 1", "Test", vessel.Id, firstBatch).ConfigureAwait(false);
                    List<Mission> first = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage1.Id).ConfigureAwait(false);
                    string firstId = first[0].Id;

                    // Poll until first mission is InProgress
                    Stopwatch waitFirst = Stopwatch.StartNew();
                    while (waitFirst.ElapsedMilliseconds < 3000)
                    {
                        Mission? m = await testDb.Driver.Missions.ReadAsync(firstId).ConfigureAwait(false);
                        if (m != null && m.Status == MissionStatusEnum.InProgress) break;
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    // Dispatch second mission while first is still active
                    List<MissionDescription> secondBatch = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Second mission", Description = "Should be deferred by mutex." }
                    };
                    Voyage voyage2 = await admiral.DispatchVoyageAsync("Voyage 2", "Test", vessel.Id, secondBatch).ConfigureAwait(false);
                    List<Mission> second = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage2.Id).ConfigureAwait(false);
                    string secondId = second[0].Id;

                    // Within 2s the second mission should show WaitingForVesselMutex
                    bool sawMutex = false;
                    Stopwatch poll = Stopwatch.StartNew();
                    while (poll.ElapsedMilliseconds < 2000)
                    {
                        Mission? m = await testDb.Driver.Missions.ReadAsync(secondId).ConfigureAwait(false);
                        if (m != null && m.AssignmentState == MissionAssignmentStateEnum.WaitingForVesselMutex)
                        {
                            sawMutex = true;
                            break;
                        }
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertTrue(sawMutex, "Second mission on serial vessel must reach WaitingForVesselMutex within 2s");
                }
            });

            await RunTest("Dispatch_NoIdleCaptain_ShowsWaitingForIdleCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                    // No captains registered -- no idle captain will be found

                    Vessel vessel = new Vessel("no-captain-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "No captain mission", Description = "No idle captain available." }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync("No captain voyage", "Test", vessel.Id, missions).ConfigureAwait(false);
                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    string missionId = voyageMissions[0].Id;

                    // Poll until WaitingForIdleCaptain is set (background task must settle within 2s)
                    bool sawWaiting = false;
                    Stopwatch poll = Stopwatch.StartNew();
                    while (poll.ElapsedMilliseconds < 2000)
                    {
                        Mission? m = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                        if (m != null && m.AssignmentState == MissionAssignmentStateEnum.WaitingForIdleCaptain)
                        {
                            sawWaiting = true;
                            break;
                        }
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertTrue(sawWaiting, "Mission with no idle captain must reach WaitingForIdleCaptain within 2s");
                }
            });

            await RunTest("Dispatch_DockProvisioningThrows_ShowsFailedThenRecoversOnRetry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    ThrowOnceThenSucceedDockService faultyDock = new ThrowOnceThenSucceedDockService(realDock);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, faultyDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, faultyDock, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, faultyDock);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("faulty-dock-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("faulty-dock-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Faulty dock mission", Description = "Dock throws once then succeeds." }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync("Faulty dock voyage", "Test", vessel.Id, missions).ConfigureAwait(false);
                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    string missionId = voyageMissions[0].Id;

                    // Poll until Failed state appears after first attempt
                    bool sawFailed = false;
                    Stopwatch poll = Stopwatch.StartNew();
                    while (poll.ElapsedMilliseconds < 3000)
                    {
                        Mission? m = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                        if (m != null && m.AssignmentState == MissionAssignmentStateEnum.Failed)
                        {
                            sawFailed = true;
                            break;
                        }
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertTrue(sawFailed, "Mission must reach AssignmentState=Failed after dock provisioning exception");

                    // Retry via TryAssignAsync -- faultyDock.ProvisionAsync now succeeds
                    Mission? toRetry = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    AssertNotNull(toRetry, "Mission must still exist after failure");
                    AssertEqual(MissionStatusEnum.Pending.ToString(), toRetry!.Status.ToString(), "Mission status must be Pending for retry");

                    bool recovered = await missionService.TryAssignAsync(toRetry!, vessel).ConfigureAwait(false);
                    AssertTrue(recovered, "Second TryAssignAsync call must succeed after dock recovers");

                    Mission? afterRetry = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    AssertNotNull(afterRetry, "Mission must still exist after recovery");
                    AssertEqual(MissionAssignmentStateEnum.Assigned.ToString(), afterRetry!.AssignmentState.ToString(), "Mission must reach AssignmentState=Assigned after successful retry");
                }
            });

            await RunTest("Dispatch_BadVesselId_ThrowsInvalidOperationException_NoBackgroundTaskKicked", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Bad vessel mission", Description = "Vessel does not exist." }
                    };

                    bool threw = false;
                    try
                    {
                        await admiral.DispatchVoyageAsync("Bad vessel voyage", "Test", "vsl_does_not_exist", missions).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        threw = true;
                    }

                    AssertTrue(threw, "DispatchVoyageAsync must throw InvalidOperationException for unknown vessel ID");
                }
            });
        }

        #endregion

        #region Private-Methods

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

        #endregion

        #region Private-Types

        /// <summary>
        /// Dock service that introduces a configurable delay before delegating to a real DockService.
        /// Used to make provisioning observable in timing tests.
        /// </summary>
        private sealed class DelayingDockService : IDockService
        {
            private readonly IDockService _Inner;
            private readonly int _DelayMs;

            public DelayingDockService(IDockService inner, int delayMs)
            {
                _Inner = inner;
                _DelayMs = delayMs;
            }

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, CancellationToken token = default)
            {
                await Task.Delay(_DelayMs, CancellationToken.None).ConfigureAwait(false);
                return await _Inner.ProvisionAsync(vessel, captain, branchName, missionId, token).ConfigureAwait(false);
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.ReclaimAsync(dockId, tenantId, token);

            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.RepairAsync(dockId, tenantId, token);

            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.DeleteAsync(dockId, tenantId, token);

            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        /// <summary>
        /// Dock service that throws InvalidOperationException on the first ProvisionAsync call,
        /// then delegates to a real DockService on subsequent calls.
        /// </summary>
        private sealed class ThrowOnceThenSucceedDockService : IDockService
        {
            private readonly IDockService _Inner;
            private int _CallCount = 0;

            public ThrowOnceThenSucceedDockService(IDockService inner)
            {
                _Inner = inner;
            }

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, CancellationToken token = default)
            {
                int call = Interlocked.Increment(ref _CallCount);
                if (call == 1)
                    throw new InvalidOperationException("Simulated dock provisioning failure (first attempt)");

                return await _Inner.ProvisionAsync(vessel, captain, branchName, missionId, token).ConfigureAwait(false);
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.ReclaimAsync(dockId, tenantId, token);

            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.RepairAsync(dockId, tenantId, token);

            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.DeleteAsync(dockId, tenantId, token);

            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        #endregion
    }
}
