namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

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
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, delayingDock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
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
                    GatedDockService gatedDock = new GatedDockService(realDock);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, gatedDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, gatedDock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, gatedDock);
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

                    // The gate holds provisioning open, so observing Provisioning is deterministic
                    // instead of a race between a wall-clock delay and the poll interval.
                    Task entered = await Task.WhenAny(gatedDock.Entered, Task.Delay(30000)).ConfigureAwait(false);
                    AssertTrue(entered == gatedDock.Entered, "Dock provisioning must start within 30s");

                    Mission? provisioning = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    AssertTrue(provisioning != null, "Mission must exist while the dock is provisioning");
                    AssertTrue(provisioning!.AssignmentState == MissionAssignmentStateEnum.Provisioning,
                        "Mission must be persisted as AssignmentState=Provisioning while the dock provisions; was " + provisioning.AssignmentState);

                    gatedDock.Release();

                    // Assigned is terminal for this flow, so waiting for it is not a transient-window race.
                    bool sawAssigned = false;
                    Stopwatch poll = Stopwatch.StartNew();

                    while (poll.ElapsedMilliseconds < 30000)
                    {
                        Mission? m = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                        if (m != null && m.AssignmentState == MissionAssignmentStateEnum.Assigned)
                        {
                            sawAssigned = true;
                            break;
                        }

                        await Task.Delay(25).ConfigureAwait(false);
                    }

                    AssertTrue(sawAssigned, "Mission must reach AssignmentState=Assigned after the dock gate is released");
                }
            });

            await RunTest("Dispatch_SerialVessel_SecondMissionShowsWaitingForVesselMutex", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 2;
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 2;
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
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
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
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

            await RunTest("TryAssign_MissionCancelledWhileSelectingACaptain_StaysCancelled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);

                    Vessel vessel = new Vessel("cancel-select-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = new Captain("cancel-select-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Cancelled while selecting") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);

                    // The voyage cancel lands after the pass loaded the mission and before it records WaitingForIdleCaptain.
                    CancellingQuarantineService quarantine = new CancellingQuarantineService(new CaptainQuarantineService(testDb.Driver, settings, logging), testDb.Driver, mission.Id);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, captainQuarantine: quarantine, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "a cancelled mission is not assigned");
                    AssertTrue(quarantine.CancelledMission, "the cancellation landed inside the assignment pass");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Cancelled, stored!.Status, "the pass must not write its stale Pending copy over the cancellation");
                }
            });

            await RunTest("TryAssign_MissionCancelledDuringDockProvisioning_StaysCancelledAndReleasesTheCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("cancel-provision-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = new Captain("cancel-provision-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Cancelled while provisioning") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);

                    CancelThenThrowDockService dock = new CancelThenThrowDockService(realDock, testDb.Driver, mission.Id);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dock);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "a mission cancelled during provisioning is not assigned");
                    AssertTrue(dock.CancelledMission, "the cancellation landed during dock provisioning");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Cancelled, stored!.Status, "the provisioning rollback must not revert the cancellation to Pending");
                    Captain? released = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, released!.State, "the captain is released");
                }
            });

            await RunTest("TryAssign_CaptainClaimedByAnotherMissionAfterProvisioning_StoresMissionPendingAndDeletesTheDock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("claim-lost-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = new Captain("claim-lost-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Loses its captain") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);

                    const string rivalMissionId = "msn_rival_claim";
                    AfterProvisionDockService dock = new AfterProvisionDockService(realDock, async _ =>
                    {
                        bool rivalClaimed = await testDb.Driver.Captains.TryClaimAsync(Constants.DefaultTenantId, captain.Id, rivalMissionId, "dck_rival_claim").ConfigureAwait(false);
                        AssertTrue(rivalClaimed, "the rival claim lands between provisioning and the assignment commit");
                    });
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "a mission whose captain was claimed elsewhere is not assigned");
                    AssertNotNull(dock.ProvisionedDockId, "the dock was provisioned before the claim");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, stored!.Status, "the stored mission returns to Pending");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForIdleCaptain, stored.AssignmentState, "the stored mission names why it is waiting");
                    AssertNull(stored.CaptainId, "the stored mission holds no captain");
                    AssertNull(stored.DockId, "the stored mission holds no dock");
                    AssertTrue(String.IsNullOrEmpty(stored.BranchName), "the stored mission holds no branch cut for the lost captain");
                    Dock? storedDock = await testDb.Driver.Docks.ReadAsync(dock.ProvisionedDockId!).ConfigureAwait(false);
                    AssertNull(storedDock, "the provisioned dock record is deleted");
                    Captain? rivalCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Working, rivalCaptain!.State, "the rival keeps the captain");
                    AssertEqual(rivalMissionId, rivalCaptain.CurrentMissionId, "the losing pass must not release the rival's claim");
                }
            });

            await RunTest("TryAssign_DockProvisioningFailsWhileAnotherMissionHoldsTheCaptain_LeavesTheCaptainWorkingOnIt", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("provision-fail-rival-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = new Captain("provision-fail-rival-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Provisioning fails") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);

                    const string rivalMissionId = "msn_rival_during_provision";
                    RivalClaimDockService dock = new RivalClaimDockService(realDock, () => ClaimForRivalAsync(testDb.Driver, captain.Id, rivalMissionId));
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dock);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "a mission whose dock provisioning failed is not assigned");
                    AssertTrue(dock.RivalClaimed, "the rival claim landed while the dock was provisioning");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionAssignmentStateEnum.Failed, stored!.AssignmentState, "the provisioning failure is recorded");
                    AssertNull(stored.CaptainId, "the failed mission holds no captain");
                    Captain? rivalCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Working, rivalCaptain!.State, "the captain stays Working on the other mission");
                    AssertEqual(rivalMissionId, rivalCaptain.CurrentMissionId, "the failed pass must not release another mission's captain");
                }
            });

            await RunTest("TryAssign_StartRefFailsWhileAnotherMissionHoldsTheCaptain_LeavesTheCaptainWorkingOnIt", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("start-ref-rival-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = new Captain("start-ref-rival-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Start ref does not resolve")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.Pending,
                        StartFromRef = "refs/heads/missing"
                    }).ConfigureAwait(false);

                    const string rivalMissionId = "msn_rival_during_start_ref";
                    RivalClaimDockService dock = new RivalClaimDockService(realDock, () => ClaimForRivalAsync(testDb.Driver, captain.Id, rivalMissionId));
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dock);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "a mission whose start ref does not resolve is not assigned");
                    AssertTrue(dock.RivalClaimed, "the rival claim landed while the start ref was resolving");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, stored!.Status, "the unresolved start ref fails the mission");
                    Captain? rivalCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Working, rivalCaptain!.State, "the captain stays Working on the other mission");
                    AssertEqual(rivalMissionId, rivalCaptain.CurrentMissionId, "the failed pass must not release another mission's captain");
                }
            });

            await RunTest("TryAssign_MissionCancelledAfterProvisioning_StaysCancelledReleasesTheCaptainAndDeletesTheDock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("cancel-after-provision-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = new Captain("cancel-after-provision-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Cancelled after provisioning") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);

                    AfterProvisionDockService dock = new AfterProvisionDockService(realDock, _ => CancelMissionAsync(testDb.Driver, mission.Id));
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dock);
                    int launches = 0;
                    captainService.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(12345); };
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "a mission cancelled before its assignment committed is not assigned");
                    AssertEqual(0, launches, "no agent is launched for a cancelled mission");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Cancelled, stored!.Status, "the cancellation wins over the assignment");
                    Captain? released = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, released!.State, "the captain is not left claimed for a cancelled mission");
                    AssertNull(released.CurrentMissionId, "the released captain holds no mission");
                    Dock? storedDock = await testDb.Driver.Docks.ReadAsync(dock.ProvisionedDockId!).ConfigureAwait(false);
                    AssertNull(storedDock, "the provisioned dock record is deleted");
                }
            });

            await RunTest("TryAssign_InstructionWriteFails_ReleasesTheCaptainRequeuesTheMissionAndLaunchesNothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("instruction-write-fails-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = new Captain("instruction-write-fails-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Instruction write fails") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);

                    // Replace the provisioned worktree with a plain file, so writing the mission instructions throws.
                    AfterProvisionDockService dock = new AfterProvisionDockService(realDock, provisioned =>
                    {
                        string path = provisioned.WorktreePath!;
                        if (Directory.Exists(path)) Directory.Delete(path, true);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, "not a directory");
                        return Task.CompletedTask;
                    });
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dock);
                    int launches = 0;
                    captainService.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(12345); };
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "a mission whose instructions cannot be written is not assigned");
                    AssertEqual(0, launches, "no agent is launched without instructions");
                    Captain? released = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, released!.State, "the captain is released, not left Working with no process");
                    AssertNull(released.CurrentMissionId, "the released captain holds no mission");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, stored!.Status, "the mission is requeued");
                    AssertNull(stored.CaptainId, "the requeued mission holds no captain");
                    AssertNull(stored.ProcessId, "the requeued mission records no process");
                }
            });

            await RunTest("Held dock failure remains visible and stops after one retry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    HeldDockService heldDock = new HeldDockService(realDock);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, heldDock);
                    int launches = 0;
                    captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(12345); };
                    IMissionService missions = new MissionService(logging, testDb.Driver, settings, heldDock, captains,
                        resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("held-dock", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("held-dock-captain")).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Continue", "Reproduce a persistent dock collision")
                    { VesselId = vessel.Id, Status = MissionStatusEnum.Pending }).ConfigureAwait(false);
                    AssertFalse(await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false));
                    Mission first = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Pending, first.Status);
                    AssertContains("/tmp/holding-dock", first.FailureReason ?? String.Empty);
                    AssertContains("pids 2147483647", first.FailureReason ?? String.Empty);
                    AssertFalse(await missions.TryAssignAsync(first, vessel).ConfigureAwait(false));
                    Mission last = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Failed, last.Status);
                    AssertContains("retry bound", last.FailureReason ?? String.Empty);
                    AssertNotNull(last.CompletedUtc);
                    AssertFalse(await missions.TryAssignAsync(last, vessel).ConfigureAwait(false));
                    AssertEqual(2, heldDock.Calls, "terminal mission must never attempt a third provision");
                    AssertEqual(0, launches);
                    AssertEqual(CaptainStateEnum.Idle, (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!.State);
                }
            });

            await RunTest("A stage whose branch is held by its own earlier stage's dock waits instead of failing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    string predecessorPath = Path.Combine(Path.GetTempPath(), "armada_predecessor_dock_" + Guid.NewGuid().ToString("N"));
                    HeldDockService heldDock = new HeldDockService(realDock, predecessorPath);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, heldDock);
                    int launches = 0;
                    captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(12345); };
                    IMissionService missions = new MissionService(logging, testDb.Driver, settings, heldDock, captains,
                        resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("held-by-predecessor", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("held-by-predecessor-captain")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("chain") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);

                    Dock predecessorDock = new Dock(vessel.Id) { WorktreePath = predecessorPath, BranchName = "armada/chain-branch" };
                    predecessorDock = await testDb.Driver.Docks.CreateAsync(predecessorDock).ConfigureAwait(false);
                    Mission predecessor = await testDb.Driver.Missions.CreateAsync(new Mission("Earlier stage", "Produced the branch")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.Complete,
                        BranchName = "armada/chain-branch",
                        DockId = predecessorDock.Id
                    }).ConfigureAwait(false);
                    Mission stage = await testDb.Driver.Missions.CreateAsync(new Mission("Next stage", "Continue on the branch")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.Pending,
                        DependsOnMissionId = predecessor.Id,
                        BranchName = "armada/chain-branch"
                    }).ConfigureAwait(false);

                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        Mission current = (await testDb.Driver.Missions.ReadAsync(stage.Id).ConfigureAwait(false))!;
                        AssertFalse(await missions.TryAssignAsync(current, vessel).ConfigureAwait(false), "attempt " + attempt + " cannot provision yet");
                        Mission after = (await testDb.Driver.Missions.ReadAsync(stage.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Pending, after.Status, "attempt " + attempt + ": the stage waits, it is not failed by the retry bound");
                        AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, after.AssignmentState, "attempt " + attempt + ": the stage waits on its dependency");
                        AssertEqual(MissionAssignmentHold.PredecessorDockHeld, after.AssignmentHoldReason, "attempt " + attempt + ": the wait names its reason");
                        AssertEqual(predecessor.Id, after.AssignmentHoldEntityId, "attempt " + attempt + ": the wait names the earlier stage");
                        AssertEqual("armada/chain-branch", after.BranchName, "attempt " + attempt + ": the inherited branch is kept");
                        AssertFalse((after.FailureReason ?? String.Empty).Contains("retry bound", StringComparison.Ordinal), "attempt " + attempt + ": no retry bound");
                    }
                    AssertEqual(3, heldDock.Calls, "each pass tries again while the earlier stage holds the branch");
                    AssertEqual(0, launches);
                    AssertEqual(CaptainStateEnum.Idle, (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!.State, "the captain is released");
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
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, faultyDock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, faultyDock);
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

                    // The first attempt runs in dispatch's background work. Its failure path writes
                    // AssignmentState=Failed before it releases the captain and its in-flight entry, so
                    // a retry started on the Failed state alone can still collide with that attempt.
                    await admiral.WhenQueuedAssignmentsDrainedAsync().ConfigureAwait(false);

                    Mission? failed = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    AssertEqual(MissionAssignmentStateEnum.Failed, failed!.AssignmentState,
                        "Mission must reach AssignmentState=Failed after dock provisioning exception");
                    Captain? released = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, released!.State,
                        "The failed attempt must release the captain before the retry");

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

            await RunTest("Dispatch_QueuedAssignmentsDrain_WaitsForTheBackgroundAssignmentToSettle", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    GatedDockService gatedDock = new GatedDockService(realDock);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, gatedDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, gatedDock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, gatedDock);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("drain-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Drain probe", Description = "Held open in provisioning." }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync("Drain voyage", "Test", vessel.Id, missions).ConfigureAwait(false);
                    string missionId = (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false))[0].Id;

                    Task entered = await Task.WhenAny(gatedDock.Entered, Task.Delay(30000)).ConfigureAwait(false);
                    AssertTrue(entered == gatedDock.Entered, "Dock provisioning must start within 30s");

                    // The gate holds the queued assignment inside provisioning, so the drain cannot
                    // have completed yet; this is a property of the gate, not of timing.
                    Task drained = admiral.WhenQueuedAssignmentsDrainedAsync();
                    AssertFalse(drained.IsCompleted, "The drain must not complete while queued assignment work is still running");

                    gatedDock.Release();

                    Task settled = await Task.WhenAny(drained, Task.Delay(30000)).ConfigureAwait(false);
                    AssertTrue(settled == drained, "The drain must complete within 30s once the queued assignment can finish");

                    // No polling: once the drain completes, the assignment result is already durable.
                    Mission? assigned = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.InProgress, assigned!.Status, "The drained assignment must have launched the mission");
                    AssertEqual(MissionAssignmentStateEnum.Assigned, assigned.AssignmentState, "The drained assignment must be recorded as Assigned");
                    Captain? working = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(missionId, working!.CurrentMissionId, "The captain must hold the drained mission");
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
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
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

            // A fan-out worker depends on a planner stage but never inherits its branch. When an
            // earlier assignment attempt wrote the captain's own branch name and then aborted, the
            // retry must not read that leftover as "continues the upstream branch": it must cut a
            // fresh branch and the base guard must not fire. Measured live: four stages of one
            // voyage failed stage_base_missing on branches cut from the default branch by design.
            await RunTest("TryAssign_FanOutWorkerWithLeftoverOwnBranchName_CutsFreshBranchAndPassesBaseGuard", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.IsAncestorResult = false;

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = new Vessel("fanout-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("fanout-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission planner = new Mission("Architect plan", "Emits the plan.");
                    planner.VesselId = vessel.Id;
                    planner.Persona = "Architect";
                    planner.Status = MissionStatusEnum.WorkProduced;
                    planner.BranchName = "armada/planner-captain/msn_planner";
                    planner.CommitHash = "6df8bf4b9d68000000000000000000000000abcd";
                    planner = await testDb.Driver.Missions.CreateAsync(planner).ConfigureAwait(false);
                    planner.Status = MissionStatusEnum.WorkProduced;
                    planner.BranchName = "armada/planner-captain/msn_planner";
                    planner.CommitHash = "6df8bf4b9d68000000000000000000000000abcd";
                    await testDb.Driver.Missions.UpdateAsync(planner).ConfigureAwait(false);

                    // The Architect handoff marks each spawned block as prepared; the fixture carries the same marker.
                    Mission worker = new Mission("Block worker", "Implements one block of the plan." + Environment.NewLine + "<!-- ARMADA:ARCHITECT-HANDOFF -->");
                    worker.VesselId = vessel.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.Pending;
                    worker.DependsOnMissionId = planner.Id;
                    // The leftover of an aborted attempt: the previous captain's own branch name.
                    worker.BranchName = "armada/previous-captain/" + "msn_stale_attempt";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);
                    worker.BranchName = "armada/previous-captain/msn_stale_attempt";
                    worker.DependsOnMissionId = planner.Id;
                    await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                    Mission? plannerStored = await testDb.Driver.Missions.ReadAsync(planner.Id).ConfigureAwait(false);
                    AssertNotNull(plannerStored, "Planner must be stored");
                    AssertEqual(MissionStatusEnum.WorkProduced, plannerStored!.Status, "Planner fixture must read back as WorkProduced (id " + planner.Id + ", worker depends on " + worker.DependsOnMissionId + ")");

                    bool assigned = await missionService.TryAssignAsync(worker, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Worker must still exist");
                    AssertTrue(assigned, "A fan-out worker with a leftover branch name must still be assignable; state=" + readBack!.AssignmentState + " status=" + readBack.Status + " reason: " + (readBack.FailureReason ?? "(none)"));
                    AssertTrue(readBack.Status != MissionStatusEnum.Failed, "The base guard must not fire on a fan-out worker: " + (readBack.FailureReason ?? "(none)"));
                    AssertTrue(!String.Equals(readBack.BranchName, "armada/previous-captain/msn_stale_attempt", StringComparison.Ordinal),
                        "The leftover branch name must be replaced by a fresh captain branch, was " + readBack.BranchName);
                    AssertTrue(!String.IsNullOrEmpty(readBack.BranchName) && readBack.BranchName!.Contains(captain.Name), "The fresh branch is the assigned captain's own");
                }
            });

            // An admiral restart resets an in-flight ROOT (stage-1) mission to Pending but retains its
            // committed branch and its active dock. The replacement captain must CONTINUE that branch at
            // its committed head, not cut a fresh branch from the default branch and orphan the work.
            // Real git proves the head: the saved branch carries a commit that is absent from main.
            await RunTest("TryAssign_ReassignedRootMissionWithRetainedDock_ContinuesCommittedBranchHead", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    GitService git = new GitService(logging);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    string bareRepo = global::Test.Shared.Infrastructure.TestGitRepoHelper.CreateBareRepoCopy();
                    string mainHead = RunGitProcess(bareRepo, "rev-parse", "main").Trim();

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(NewRealGitVessel("root-continuation-vessel", bareRepo)).ConfigureAwait(false);

                    Mission root = new Mission("Root worker", "Implements the first slice.");
                    root.VesselId = vessel.Id;
                    root.Persona = "Worker";
                    root.Status = MissionStatusEnum.Pending;
                    root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);

                    // The previous captain committed work on the mission's own root branch; the saved head is
                    // ahead of main.
                    string savedBranch = "armada/captain-a/" + root.Id;
                    string savedHead = CommitAheadOnBranch(bareRepo, savedBranch, "Captain A committed work");
                    AssertTrue(!String.Equals(savedHead, mainHead, StringComparison.Ordinal), "precondition: the saved branch head is ahead of main");

                    Captain captainA = await testDb.Driver.Captains.CreateAsync(new Captain("captain-a")
                    {
                        State = CaptainStateEnum.Working, CurrentMissionId = root.Id, ProcessId = 8812
                    }).ConfigureAwait(false);
                    Dock retained = await dockService.ProvisionAsync(vessel, captainA, savedBranch, root.Id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Fixture dock provisioning failed");
                    // Legacy mission owner fields normalize to the default owner, just like the explicit dock owner.
                    root.UserId = null;
                    root.BranchName = savedBranch;
                    root.DockId = retained.Id;
                    root.CaptainId = captainA.Id;
                    root.ProcessId = 8812;
                    root.Status = MissionStatusEnum.InProgress;
                    root.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    await testDb.Driver.Missions.UpdateAsync(root).ConfigureAwait(false);
                    captainA.CurrentDockId = retained.Id;
                    await testDb.Driver.Captains.UpdateAsync(captainA).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.Combine(settings.LogDirectory, "missions"));
                    await File.WriteAllTextAsync(Path.Combine(settings.LogDirectory, "missions", root.Id + ".log"),
                        "[stderr] API Error: 401 Invalid credential\nAgent exited with code 1").ConfigureAwait(false);
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService,
                        missionService, new VoyageService(logging, testDb.Driver), dockService);
                    await admiral.HandleProcessExitAsync(8812, 1, captainA.Id, root.Id).ConfigureAwait(false);
                    await admiral.WhenQueuedAssignmentsDrainedAsync().ConfigureAwait(false);
                    root = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Retry mission disappeared");
                    AssertEqual(MissionStatusEnum.Pending, root.Status, "Provider failure must requeue the root");
                    AssertEqual(retained.Id, root.DockId, "Provider failure must retain continuation authority");
                    AssertTrue(Directory.Exists(retained.WorktreePath), "Provider retry must retain the committed checkout");

                    // The replacement captain is a DIFFERENT captain -- the exact restart-swap failure.
                    Captain captainB = await testDb.Driver.Captains.CreateAsync(new Captain("captain-b") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(root, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "The root mission must still exist");
                    AssertTrue(assigned, "The reassigned root mission must be assignable; state=" + readBack!.AssignmentState + " reason: " + (readBack.FailureReason ?? "(none)"));
                    AssertEqual(savedBranch, readBack!.BranchName ?? "(null)", "The reassigned root mission must keep its persisted branch, not a fresh captain branch");

                    Dock? newDock = await testDb.Driver.Docks.ReadAsync(readBack.DockId ?? "").ConfigureAwait(false);
                    AssertNotNull(newDock, "A dock must be provisioned for the reassigned mission");
                    string? provisionedHead = await git.GetHeadCommitHashAsync(newDock!.WorktreePath!).ConfigureAwait(false);
                    AssertEqual(savedHead, provisionedHead ?? "(null)", "The provisioned checkout must sit on the committed branch head, not on main (" + mainHead + ")");
                }
            });

            // A stage-2 mission stopped with uncommitted work -- an edit, a deletion and a new file -- keeps
            // its branch across the restart. The rebuilt checkout shows that work again as uncommitted
            // changes, with no commit made for it.
            await RunTest("TryAssign_ReassignedStageTwoMissionWithUncommittedWork_ResumesTheWorkAsUncommittedChanges", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StageTwoRestart restart = await StageTwoRestart.StopWithUncommittedWorkAsync(testDb, CreateLogging(), CreateSettings(), commitAfterSavingTheWork: false).ConfigureAwait(false);

                    bool assigned = await restart.MissionService.TryAssignAsync(restart.Stage, restart.Vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(restart.Stage.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "The restarted stage must be assignable; reason: " + (readBack?.FailureReason ?? "(none)"));
                    Dock? dock = await testDb.Driver.Docks.ReadAsync(readBack!.DockId ?? "").ConfigureAwait(false);
                    string checkout = dock!.WorktreePath!;
                    AssertEqual(restart.BranchHead, RunGitProcess(checkout, "rev-parse", "HEAD").Trim(), "No commit is made for the resumed work");
                    AssertEqual("edited by the stopped captain", File.ReadAllText(Path.Combine(checkout, "README.md")).Trim(), "The edit is back");
                    AssertFalse(File.Exists(Path.Combine(checkout, "work.txt")), "The deletion is back");
                    AssertEqual("new file", File.ReadAllText(Path.Combine(checkout, "new.txt")).Trim(), "The new file is back");
                    string status = RunGitProcess(checkout, "status", "--porcelain");
                    AssertContains(" M README.md", status);
                    AssertContains(" D work.txt", status);
                    AssertContains("?? new.txt", status);
                    AssertEqual(0, restart.Poster.Posts.Count(post => post.Contains("[work not restored]", StringComparison.Ordinal)), "A resume posts no warning");

                    List<ArmadaEvent> events = (await testDb.Driver.Events.EnumerateByMissionAsync(restart.Stage.Id, 50).ConfigureAwait(false));
                    AssertEqual(1, events.Count(e => e.EventType == "mission.work_snapshot_applied"), "The resume is recorded once");
                    AssertEqual(0, events.Count(e => e.EventType == "mission.work_snapshot_skipped"), "A resume is not a skip");
                }
            });

            // Saved work made on an older commit than the rebuilt checkout's HEAD is not applied: it would land
            // on a tree it was not made on. The skip is recorded and announced once, and the work stays saved.
            await RunTest("TryAssign_ReassignedStageTwoMissionWhoseSavedWorkIsOnAnOlderCommit_AppliesNothingAndRecordsTheSkip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StageTwoRestart restart = await StageTwoRestart.StopWithUncommittedWorkAsync(testDb, CreateLogging(), CreateSettings(), commitAfterSavingTheWork: true).ConfigureAwait(false);

                    bool assigned = await restart.MissionService.TryAssignAsync(restart.Stage, restart.Vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(restart.Stage.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "The restarted stage must still be assignable; reason: " + (readBack?.FailureReason ?? "(none)"));
                    Dock? dock = await testDb.Driver.Docks.ReadAsync(readBack!.DockId ?? "").ConfigureAwait(false);
                    string checkout = dock!.WorktreePath!;
                    AssertEqual("", RunGitProcess(checkout, "status", "--porcelain").Trim(), "Nothing is applied to a checkout on a different commit");
                    AssertEqual(restart.BranchHead, RunGitProcess(checkout, "rev-parse", "HEAD").Trim(), "The checkout sits on the newer branch head");

                    List<ArmadaEvent> events = (await testDb.Driver.Events.EnumerateByMissionAsync(restart.Stage.Id, 50).ConfigureAwait(false));
                    ArmadaEvent skipped = events.Single(e => e.EventType == "mission.work_snapshot_skipped");
                    AssertContains("recover/" + restart.Stage.Id + "-1", skipped.Message);
                    AssertEqual(0, events.Count(e => e.EventType == "mission.work_snapshot_applied"), "Nothing was applied");
                    AssertEqual(1, restart.Poster.Posts.Count(post => post.Contains("[work not restored]", StringComparison.Ordinal)), "The skip is announced once on the board");
                    AssertTrue(RunGitProcess(restart.BareRepo, "for-each-ref", "refs/heads/recover/" + restart.Stage.Id + "-1").Contains("recover/", StringComparison.Ordinal),
                        "The saved work stays in its recover ref");
                }
            });

            // NEGATIVE: the retained dock authorizes continuation but the saved branch no longer resolves
            // in the vessel repository. The mission must HOLD, never silently cut a fresh branch from the
            // default branch (which would orphan the committed work).
            await RunTest("TryAssign_ReassignedRootMissionWhoseSavedBranchIsGone_HoldsInsteadOfCuttingFromMain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    GitService git = new GitService(logging);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    string bareRepo = global::Test.Shared.Infrastructure.TestGitRepoHelper.CreateBareRepoCopy();
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(NewRealGitVessel("gone-branch-vessel", bareRepo)).ConfigureAwait(false);

                    Mission root = new Mission("Root worker", "Implements the first slice.");
                    root.VesselId = vessel.Id;
                    root.Persona = "Worker";
                    root.Status = MissionStatusEnum.Pending;
                    root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);

                    string missingBranch = "armada/captain-a/" + root.Id; // never created in the bare repo
                    Dock retained = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        BranchName = missingBranch,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_olddock_" + Guid.NewGuid().ToString("N")),
                        Active = true,
                    };
                    retained = await testDb.Driver.Docks.CreateAsync(retained).ConfigureAwait(false);
                    root.BranchName = missingBranch;
                    root.DockId = retained.Id;
                    await testDb.Driver.Missions.UpdateAsync(root).ConfigureAwait(false);

                    Captain captainB = await testDb.Driver.Captains.CreateAsync(new Captain("captain-b") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(root, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "The root mission must still exist");
                    AssertFalse(assigned, "A root mission whose saved branch is gone must not be assigned onto a fresh branch");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "The held mission stays Pending");
                    AssertEqual(MissionAssignmentHold.RootContinuationRefMissing, readBack.AssignmentHoldReason ?? "(null)", "The hold names the missing-ref reason");
                    AssertEqual(missingBranch, readBack.BranchName ?? "(null)", "The saved branch name is retained, never replaced by a fresh one");
                    AssertTrue(String.IsNullOrEmpty(readBack.CaptainId), "No captain is assigned to the held mission");
                }
            });

            // NEGATIVE: an INACTIVE retained dock does not authorize continuation, but the committed branch
            // still carries work. The mission must HOLD, never cut a fresh branch from the default branch,
            // which would orphan the committed work. A failed provision that left no live dock reaches the
            // same state on a retry.
            await RunTest("TryAssign_ReassignedRootMissionWithInactiveDock_HoldsInsteadOfCuttingFromMain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    GitService git = new GitService(logging);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    string bareRepo = global::Test.Shared.Infrastructure.TestGitRepoHelper.CreateBareRepoCopy();
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(NewRealGitVessel("inactive-dock-vessel", bareRepo)).ConfigureAwait(false);

                    Mission root = new Mission("Root worker", "Implements the first slice.");
                    root.VesselId = vessel.Id;
                    root.Persona = "Worker";
                    root.Status = MissionStatusEnum.Pending;
                    root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);

                    string savedBranch = "armada/captain-a/" + root.Id;
                    CommitAheadOnBranch(bareRepo, savedBranch, "Captain A committed work");

                    Dock inactive = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        BranchName = savedBranch,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_olddock_" + Guid.NewGuid().ToString("N")),
                        Active = false,
                    };
                    inactive = await testDb.Driver.Docks.CreateAsync(inactive).ConfigureAwait(false);
                    root.BranchName = savedBranch;
                    root.DockId = inactive.Id;
                    await testDb.Driver.Missions.UpdateAsync(root).ConfigureAwait(false);

                    Captain captainB = await testDb.Driver.Captains.CreateAsync(new Captain("captain-b") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(root, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "The root mission must still exist");
                    AssertFalse(assigned, "A root mission with committed work and no active dock must not be assigned onto a fresh branch");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "The held mission stays Pending");
                    AssertEqual(MissionAssignmentHold.RootContinuationDockMissing, readBack.AssignmentHoldReason ?? "(null)", "The hold names the dock-missing reason");
                    AssertEqual(savedBranch, readBack.BranchName ?? "(null)", "The committed branch name is retained, never replaced by a fresh one");
                    AssertTrue(String.IsNullOrEmpty(readBack.CaptainId), "No captain is assigned to the held mission");
                }
            });

            // A root mission whose saved branch sits at the commit it started from has no work to orphan.
            // Holding it for a continuation dock waits for a dock that no mission will ever provision, so
            // it takes a fresh dock and branch instead.
            await RunTest("TryAssign_ReassignedRootMissionWhoseSavedBranchHoldsNoCommits_ProvisionsFreshAndReleasesTheHold", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    GitService git = new GitService(logging);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    string bareRepo = global::Test.Shared.Infrastructure.TestGitRepoHelper.CreateBareRepoCopy();
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(NewRealGitVessel("no-commit-branch-vessel", bareRepo)).ConfigureAwait(false);

                    Mission root = new Mission("Root worker", "Implements the first slice.");
                    root.VesselId = vessel.Id;
                    root.Persona = "Worker";
                    root.Status = MissionStatusEnum.Pending;
                    root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);

                    string savedBranch = "armada/captain-a/" + root.Id;
                    RunGitProcess(bareRepo, "branch", savedBranch, "main");
                    root.BranchName = savedBranch;
                    root.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                    root.AssignmentHoldReason = MissionAssignmentHold.RootContinuationDockMissing;
                    root.AssignmentHoldEntityId = savedBranch;
                    await testDb.Driver.Missions.UpdateAsync(root).ConfigureAwait(false);

                    await testDb.Driver.Captains.CreateAsync(new Captain("captain-b") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(root, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "The root mission must still exist");
                    AssertTrue(assigned, "A root mission whose saved branch holds no commits is assigned; state=" + readBack!.AssignmentState + " hold=" + (readBack.AssignmentHoldReason ?? "(none)"));
                    AssertNull(readBack.AssignmentHoldReason, "The stale continuation hold is released");
                    AssertTrue(!String.Equals(savedBranch, readBack.BranchName, StringComparison.Ordinal), "The mission works on a fresh captain branch, not the empty saved one");
                    AssertNotNull(readBack.DockId, "A dock is provisioned from the start ref");
                }
            });

            // Cancelling a held mission, and every stage the cancel cascades to, ends the wait it recorded.
            await RunTest("Cancel_MissionsHeldForAssignment_ClearTheirHolds", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("cancel-hold-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("cancel hold voyage")).ConfigureAwait(false);

                    Mission root = new Mission("Held root", "Waits for a continuation dock.")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.Pending,
                        AssignmentState = MissionAssignmentStateEnum.WaitingForDependency,
                        AssignmentHoldReason = MissionAssignmentHold.RootContinuationDockMissing,
                        AssignmentHoldEntityId = "armada/captain-a/branch"
                    };
                    root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);
                    Mission dependent = await testDb.Driver.Missions.CreateAsync(new Mission("Held stage", "Waits for the root.")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Persona = "Judge",
                        Status = MissionStatusEnum.Pending,
                        DependsOnMissionId = root.Id,
                        AssignmentState = MissionAssignmentStateEnum.WaitingForDependency,
                        AssignmentHoldReason = MissionAssignmentHold.DependencyUnfinished,
                        AssignmentHoldEntityId = root.Id
                    }).ConfigureAwait(false);

                    MissionCancellationResult result = await MissionCancellation.CancelAsync(
                        testDb.Driver, root, MissionCancellation.OperatorCancelReason, null).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "precondition: the cancel is applied");

                    foreach (string id in new[] { root.Id, dependent.Id })
                    {
                        Mission? cancelled = await testDb.Driver.Missions.ReadAsync(id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Cancelled, cancelled!.Status, id + " is cancelled");
                        AssertNull(cancelled.AssignmentHoldReason, id + " keeps no assignment hold");
                        AssertNull(cancelled.AssignmentHoldEntityId, id + " names no holder");
                    }
                }
            });

            // RETRY: the FIRST assignment of a reassigned root mission fails in launch (an I/O error, an
            // out-of-memory brief build), which clears the mission's dock pointer but keeps the committed
            // branch. The retry must re-authorize continuation from the still-active retained dock and sit
            // on the committed head -- never cut a fresh branch from the default branch because the pointer
            // was cleared. This is the retry gap: authority is a property of the live dock, not the pointer.
            await RunTest("TryAssign_ReassignedRootMissionLaunchFailsThenRetries_ContinuesCommittedBranchNotFreshFromMain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    GitService git = new GitService(logging);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    bool failLaunch = true;
                    captainService.OnLaunchAgent = (_, _, _) =>
                    {
                        if (failLaunch) throw new InvalidOperationException("simulated launch failure");
                        return Task.FromResult(12345);
                    };
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    string bareRepo = global::Test.Shared.Infrastructure.TestGitRepoHelper.CreateBareRepoCopy();
                    string mainHead = RunGitProcess(bareRepo, "rev-parse", "main").Trim();
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(NewRealGitVessel("retry-continuation-vessel", bareRepo)).ConfigureAwait(false);

                    Mission root = new Mission("Root worker", "Implements the first slice.");
                    root.VesselId = vessel.Id;
                    root.Persona = "Worker";
                    root.Status = MissionStatusEnum.Pending;
                    root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);

                    string savedBranch = "armada/captain-a/" + root.Id;
                    string savedHead = CommitAheadOnBranch(bareRepo, savedBranch, "Captain A committed work");
                    AssertTrue(!String.Equals(savedHead, mainHead, StringComparison.Ordinal), "precondition: the saved branch head is ahead of main");

                    Dock retained = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        BranchName = savedBranch,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_olddock_" + Guid.NewGuid().ToString("N")),
                        Active = true,
                    };
                    retained = await testDb.Driver.Docks.CreateAsync(retained).ConfigureAwait(false);
                    root.BranchName = savedBranch;
                    root.DockId = retained.Id;
                    await testDb.Driver.Missions.UpdateAsync(root).ConfigureAwait(false);

                    Captain captainB = await testDb.Driver.Captains.CreateAsync(new Captain("captain-b") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    // Attempt 1: launch fails. The failure path clears the dock pointer and keeps the branch.
                    bool firstAssigned = await missionService.TryAssignAsync(root, vessel).ConfigureAwait(false);
                    Mission? afterFail = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false);
                    AssertNotNull(afterFail, "The root mission must still exist after a failed launch");
                    AssertFalse(firstAssigned, "The first assignment fails in launch");
                    AssertTrue(String.IsNullOrEmpty(afterFail!.DockId), "The failed launch clears the dock pointer; was " + (afterFail.DockId ?? "(null)"));
                    AssertEqual(savedBranch, afterFail.BranchName ?? "(null)", "The failed launch keeps the committed branch, never a fresh one");

                    // Attempt 2: launch succeeds. The still-active retained dock re-authorizes continuation.
                    failLaunch = false;
                    bool secondAssigned = await missionService.TryAssignAsync(afterFail, vessel).ConfigureAwait(false);
                    Mission? afterRetry = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false);
                    AssertNotNull(afterRetry, "The root mission must still exist after the retry");
                    AssertTrue(secondAssigned, "The retry must assign; state=" + afterRetry!.AssignmentState + " reason: " + (afterRetry.FailureReason ?? "(none)"));
                    AssertEqual(savedBranch, afterRetry!.BranchName ?? "(null)", "The retry must continue the committed branch, not a fresh captain branch cut from main");

                    Dock? newDock = await testDb.Driver.Docks.ReadAsync(afterRetry.DockId ?? "").ConfigureAwait(false);
                    AssertNotNull(newDock, "A dock must be provisioned for the retried mission");
                    string? provisionedHead = await git.GetHeadCommitHashAsync(newDock!.WorktreePath!).ConfigureAwait(false);
                    AssertEqual(savedHead, provisionedHead ?? "(null)", "The retried checkout must sit on the committed branch head, not on main (" + mainHead + ")");
                }
            });

            // A transient provisioning fault -- a boot-window ssh/fork failure, exit 128 -- on a reassigned root
            // mission must stay retryable and must NOT clear the dock-authority pointer. The retained active dock
            // is the mission's continuation authority, and the orphan reaper preserves an active dock only while a
            // pending mission still points at it. Clearing the pointer on a retryable provisioning failure lets
            // the reaper reclaim the authority dock before the retry, which strands the mission on
            // root_continuation_dock_missing. The failure stays Pending and keeps both the committed branch and
            // its dock pointer.
            await RunTest("TryAssign_ReassignedRootProvisioningFails_StaysRetryableAndKeepsTheDockAuthorityPointer", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    GitService git = new GitService(logging);
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    ThrowOnceThenSucceedDockService faultyDock = new ThrowOnceThenSucceedDockService(realDock);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, faultyDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, faultyDock, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    string bareRepo = global::Test.Shared.Infrastructure.TestGitRepoHelper.CreateBareRepoCopy();
                    string mainHead = RunGitProcess(bareRepo, "rev-parse", "main").Trim();
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(NewRealGitVessel("provision-fail-continuation-vessel", bareRepo)).ConfigureAwait(false);

                    Mission root = new Mission("Root worker", "Implements the first slice.");
                    root.VesselId = vessel.Id;
                    root.Persona = "Worker";
                    root.Status = MissionStatusEnum.Pending;
                    root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);

                    string savedBranch = "armada/captain-a/" + root.Id;
                    string savedHead = CommitAheadOnBranch(bareRepo, savedBranch, "Captain A committed work");
                    AssertTrue(!String.Equals(savedHead, mainHead, StringComparison.Ordinal), "precondition: the saved branch head is ahead of main");

                    // The restart retained the committed branch and its active dock, and cleared the captain.
                    Dock retained = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        BranchName = savedBranch,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_olddock_" + Guid.NewGuid().ToString("N")),
                        Active = true,
                    };
                    retained = await testDb.Driver.Docks.CreateAsync(retained).ConfigureAwait(false);
                    root.BranchName = savedBranch;
                    root.DockId = retained.Id;
                    await testDb.Driver.Missions.UpdateAsync(root).ConfigureAwait(false);

                    Captain captainB = await testDb.Driver.Captains.CreateAsync(new Captain("captain-b") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    // The provision throws (the transient boot-window fault); the attempt does not assign.
                    bool assigned = await missionService.TryAssignAsync(root, vessel).ConfigureAwait(false);

                    Mission? afterFail = await testDb.Driver.Missions.ReadAsync(root.Id).ConfigureAwait(false);
                    AssertNotNull(afterFail, "The root mission must still exist after a failed provision");
                    AssertFalse(assigned, "A transient provisioning failure does not assign");
                    AssertEqual(MissionStatusEnum.Pending, afterFail!.Status, "A transient provisioning failure stays retryable (Pending)");
                    AssertEqual(savedBranch, afterFail.BranchName ?? "(null)", "The committed branch is kept, never a fresh one");
                    AssertEqual(retained.Id, afterFail.DockId ?? "(null)", "The retained dock-authority pointer is kept so the reaper does not reclaim it before the retry");
                }
            });

            // A voyage that waits on another voyage's Recorder waits forever when that voyage failed and
            // its Recorder was cancelled. A completed rescue of that voyage replaces the Recorder.
            await RunTest("TryAssign_DependencyCancelledInARescuedVoyage_WaitsOnTheRescueStageAndRecordsTheRewire", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.IsAncestorResult = true;
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = new Vessel("rescued-dependency-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("rescued-dependency-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    Voyage original = await testDb.Driver.Voyages.CreateAsync(new Voyage("original") { Status = VoyageStatusEnum.Failed }).ConfigureAwait(false);
                    Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("Judge", "review") { VesselId = vessel.Id, VoyageId = original.Id, Persona = "Judge", Status = MissionStatusEnum.Failed }).ConfigureAwait(false);
                    Mission recorder = await testDb.Driver.Missions.CreateAsync(new Mission("Recorder", "record") { VesselId = vessel.Id, VoyageId = original.Id, Persona = "Recorder", Status = MissionStatusEnum.Cancelled, DependsOnMissionId = judge.Id }).ConfigureAwait(false);

                    Voyage rescue = await testDb.Driver.Voyages.CreateAsync(new Voyage("rescue") { Status = VoyageStatusEnum.Complete }).ConfigureAwait(false);
                    Mission rescueWorker = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue 1: Judge", RescueMissionMarker.Marker + "\nRecover") { VesselId = vessel.Id, VoyageId = rescue.Id, Persona = "Worker", Status = MissionStatusEnum.Complete, ParentMissionId = judge.Id }).ConfigureAwait(false);
                    Mission rescueRecorder = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue Recorder", "record") { VesselId = vessel.Id, VoyageId = rescue.Id, Persona = "Recorder", Status = MissionStatusEnum.Complete, DependsOnMissionId = rescueWorker.Id, CompletedUtc = DateTime.UtcNow, BranchName = "armada/rescue/recorder", CommitHash = "7777777777777777777777777777777777777777" }).ConfigureAwait(false);

                    Voyage next = await testDb.Driver.Voyages.CreateAsync(new Voyage("next root")).ConfigureAwait(false);
                    Mission waiting = await testDb.Driver.Missions.CreateAsync(new Mission("Next architect", "Plan the next slice") { VesselId = vessel.Id, VoyageId = next.Id, Persona = "Worker", Status = MissionStatusEnum.Pending, DependsOnMissionId = recorder.Id }).ConfigureAwait(false);
                    waiting.DependsOnMissionId = recorder.Id;
                    await testDb.Driver.Missions.UpdateAsync(waiting).ConfigureAwait(false);

                    await missionService.TryAssignAsync(waiting, vessel).ConfigureAwait(false);

                    Mission? after = await testDb.Driver.Missions.ReadAsync(waiting.Id).ConfigureAwait(false);
                    AssertEqual(rescueRecorder.Id, after!.DependsOnMissionId, "The dependant now waits on the rescue's completed Recorder");
                    AssertTrue(after.AssignmentState != MissionAssignmentStateEnum.WaitingForDependency, "The dependant is no longer held by the cancelled Recorder; state=" + after.AssignmentState);
                    List<ArmadaEvent> rewires = await testDb.Driver.Events.EnumerateByTypeAsync("mission.dependency_rewired").ConfigureAwait(false);
                    AssertEqual(1, rewires.Count, "The rewire is recorded");
                    AssertContains(recorder.Id, rewires[0].Payload ?? String.Empty, "The event keeps the original dependency");
                    AssertEqual(MissionStatusEnum.Cancelled, (await testDb.Driver.Missions.ReadAsync(recorder.Id).ConfigureAwait(false))!.Status, "The original stage stays in the record");

                    Voyage lone = await testDb.Driver.Voyages.CreateAsync(new Voyage("lone failed") { Status = VoyageStatusEnum.Failed }).ConfigureAwait(false);
                    Mission loneRecorder = await testDb.Driver.Missions.CreateAsync(new Mission("Lone recorder", "record") { VesselId = vessel.Id, VoyageId = lone.Id, Persona = "Recorder", Status = MissionStatusEnum.Cancelled }).ConfigureAwait(false);
                    Mission stuck = await testDb.Driver.Missions.CreateAsync(new Mission("Waits on an unrescued voyage", "Plan") { VesselId = vessel.Id, VoyageId = next.Id, Persona = "Worker", Status = MissionStatusEnum.Pending, DependsOnMissionId = loneRecorder.Id }).ConfigureAwait(false);
                    await missionService.TryAssignAsync(stuck, vessel).ConfigureAwait(false);
                    Mission? stuckAfter = await testDb.Driver.Missions.ReadAsync(stuck.Id).ConfigureAwait(false);
                    AssertEqual(loneRecorder.Id, stuckAfter!.DependsOnMissionId, "Without a completed rescue the dependency is not changed");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, stuckAfter.AssignmentState);
                }
            });

            // A pipeline can name one persona at two stages. The replacement for a cancelled stage is the
            // rescue's stage at the same order, not whichever stage of that persona finished last.
            await RunTest("FindReplacement_PersonaAtTwoStages_PrefersTheRescueStageOfTheSameOrder", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("duplicate-persona-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;

                    Voyage original = await testDb.Driver.Voyages.CreateAsync(new Voyage("original") { Status = VoyageStatusEnum.Failed }).ConfigureAwait(false);
                    Mission firstWorker = await testDb.Driver.Missions.CreateAsync(new Mission("Worker", "implement") { VesselId = vessel.Id, VoyageId = original.Id, Persona = "Worker", StageOrder = 1, Status = MissionStatusEnum.Failed }).ConfigureAwait(false);
                    Mission reviseWorker = await testDb.Driver.Missions.CreateAsync(new Mission("Worker revise", "revise") { VesselId = vessel.Id, VoyageId = original.Id, Persona = "Worker", StageOrder = 4, Status = MissionStatusEnum.Cancelled, DependsOnMissionId = firstWorker.Id }).ConfigureAwait(false);
                    Mission orphanTester = await testDb.Driver.Missions.CreateAsync(new Mission("Tester", "test") { VesselId = vessel.Id, VoyageId = original.Id, Persona = "TestEngineer", StageOrder = 7, Status = MissionStatusEnum.Cancelled, DependsOnMissionId = reviseWorker.Id }).ConfigureAwait(false);

                    Voyage rescue = await testDb.Driver.Voyages.CreateAsync(new Voyage("rescue") { Status = VoyageStatusEnum.Complete }).ConfigureAwait(false);
                    Mission rescueFirst = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue 1: Worker", RescueMissionMarker.Marker + "\nRecover") { VesselId = vessel.Id, VoyageId = rescue.Id, Persona = "Worker", StageOrder = 1, Status = MissionStatusEnum.Complete, ParentMissionId = firstWorker.Id, CompletedUtc = now.AddMinutes(-30) }).ConfigureAwait(false);
                    Mission rescueTester = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue tester", "test") { VesselId = vessel.Id, VoyageId = rescue.Id, Persona = "TestEngineer", StageOrder = 3, Status = MissionStatusEnum.Complete, DependsOnMissionId = rescueFirst.Id, CompletedUtc = now.AddMinutes(-20) }).ConfigureAwait(false);
                    Mission rescueRevise = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue revise", "revise") { VesselId = vessel.Id, VoyageId = rescue.Id, Persona = "Worker", StageOrder = 4, Status = MissionStatusEnum.Complete, DependsOnMissionId = rescueTester.Id, CompletedUtc = now.AddMinutes(-10) }).ConfigureAwait(false);

                    Mission? forFirst = await RescueDependencyResolver.FindReplacementAsync(testDb.Driver, firstWorker).ConfigureAwait(false);
                    AssertEqual(rescueFirst.Id, forFirst?.Id ?? "(none)", "The first Worker is replaced by the rescue's stage-1 Worker, not the later stage-4 Worker");

                    Mission? forRevise = await RescueDependencyResolver.FindReplacementAsync(testDb.Driver, reviseWorker).ConfigureAwait(false);
                    AssertEqual(rescueRevise.Id, forRevise?.Id ?? "(none)", "The revise Worker is replaced by the rescue's stage-4 Worker");

                    Mission? forOrphan = await RescueDependencyResolver.FindReplacementAsync(testDb.Driver, orphanTester).ConfigureAwait(false);
                    AssertEqual(rescueTester.Id, forOrphan?.Id ?? "(none)", "With no rescue stage at the same order, the latest completed stage of the persona replaces it");
                }
            });

            await RunTest("TryAssign_FanOutWorkerBelowARecoveryTipPlanner_CutsBranchAtTheTipAndVerifiesTheCheckout", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    const string recoveryTip = "5555555555555555555555555555555555555555";
                    git.RevisionCommitShaResult = recoveryTip;
                    git.IsAncestorResult = true;

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = new Vessel("recovery-fanout-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("recovery-fanout-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission planner = new Mission("Architect plan from a recovery tip", "Emits the plan.");
                    planner.VesselId = vessel.Id;
                    planner.Persona = "Architect";
                    planner.Status = MissionStatusEnum.WorkProduced;
                    planner.StartFromRef = recoveryTip;
                    planner.CommitHash = recoveryTip;
                    planner = await testDb.Driver.Missions.CreateAsync(planner).ConfigureAwait(false);
                    planner.Status = MissionStatusEnum.WorkProduced;
                    planner.CommitHash = recoveryTip;
                    await testDb.Driver.Missions.UpdateAsync(planner).ConfigureAwait(false);

                    // The planner handoff passes the planner's start commit to each fan-out worker.
                    Mission worker = new Mission("Block worker", "Implements one block of the plan." + Environment.NewLine + "<!-- ARMADA:ARCHITECT-HANDOFF -->");
                    worker.VesselId = vessel.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.Pending;
                    worker.DependsOnMissionId = planner.Id;
                    worker.StartFromRef = recoveryTip;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);
                    worker.DependsOnMissionId = planner.Id;
                    worker.StartFromRef = recoveryTip;
                    await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(worker, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "The fan-out worker assigns; state=" + readBack!.AssignmentState + " status=" + readBack.Status + " reason: " + (readBack.FailureReason ?? "(none)"));
                    AssertEqual(1, git.ForceUpdateBranchRefCalls.Count, "The worker branch is cut from the planner's start commit, not the target branch");
                    AssertEqual(vessel.LocalPath + ":" + readBack.BranchName + ":" + recoveryTip, git.ForceUpdateBranchRefCalls[0]);
                    AssertTrue(git.IsAncestorCalls.Any(call => call.EndsWith(":" + recoveryTip + ":HEAD", StringComparison.Ordinal)),
                        "The worker's checkout is proven to contain the recovery commit before launch");
                }
            });

            // Assignment passes run concurrently and select from the same Idle list. Two missions
            // in one pass must not both provision a dock for the one idle captain: the second must
            // wait for an idle captain without spending the provisioning cost. Measured live: 12
            // aborted claims in one day, each after a full dock and sibling provisioning.
            // A mission that moves from one captain to another announces the move once on the board, and the
            // unread mail addressed to the mission moves with it. Mail addressed to a captain, to another
            // mission, or already read stays where it is.
            await RunTest("TryAssign_MissionMovesToADifferentCaptain_AnnouncesOnceAndMovesItsUnreadMail", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    FakeBoardNotePoster poster = new FakeBoardNotePoster();
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    missionService.ReassignmentNotePoster = poster;

                    Vessel vessel = new Vessel("reassign-note-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain previous = await testDb.Driver.Captains.CreateAsync(new Captain("previous-captain") { State = CaptainStateEnum.Stalled }).ConfigureAwait(false);
                    Captain replacement = await testDb.Driver.Captains.CreateAsync(new Captain("replacement-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    Mission moved = new Mission("Slice that outlives its captain", "Implements the slice.");
                    moved.VesselId = vessel.Id;
                    moved.Persona = "Worker";
                    moved.Status = MissionStatusEnum.Pending;
                    moved = await testDb.Driver.Missions.CreateAsync(moved).ConfigureAwait(false);
                    Mission other = await testDb.Driver.Missions.CreateAsync(new Mission("Another slice", "Implements another slice.")).ConfigureAwait(false);
                    await MissionAttemptProvenanceRecorder.StartAsync(testDb.Driver, moved, previous, 8812, null).ConfigureAwait(false);

                    Signal missionMail = await CreateMailAsync(testDb, previous.Id, moved.Id, "fix list for the moved mission").ConfigureAwait(false);
                    Signal waitingMail = await CreateMailAsync(testDb, null, moved.Id, "mail sent while the mission had no captain").ConfigureAwait(false);
                    Signal readMail = await CreateMailAsync(testDb, previous.Id, moved.Id, "already read").ConfigureAwait(false);
                    await testDb.Driver.Signals.MarkReadAsync(readMail.Id).ConfigureAwait(false);
                    Signal otherMissionMail = await CreateMailAsync(testDb, previous.Id, other.Id, "fix list for another mission").ConfigureAwait(false);
                    Signal captainMail = await CreateMailAsync(testDb, previous.Id, null, "addressed to the captain").ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(moved, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "The mission must be assignable to the replacement captain");
                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(moved.Id).ConfigureAwait(false);
                    AssertEqual(replacement.Id, readBack!.CaptainId ?? "(null)", "The replacement captain holds the mission");
                    AssertEqual(1, poster.Posts.Count, "The move is announced once");
                    AssertContains(moved.Id, poster.Posts[0]);
                    AssertContains(previous.Name, poster.Posts[0]);
                    AssertContains(replacement.Name, poster.Posts[0]);
                    AssertEqual(moved.Id, poster.MissionIds[0] ?? "(null)", "The note names the mission");

                    AssertEqual(replacement.Id, (await testDb.Driver.Signals.ReadAsync(missionMail.Id).ConfigureAwait(false))!.ToCaptainId ?? "(null)", "Unread mission mail follows the mission");
                    AssertEqual(replacement.Id, (await testDb.Driver.Signals.ReadAsync(waitingMail.Id).ConfigureAwait(false))!.ToCaptainId ?? "(null)", "Mail sent while the mission had no captain reaches its next captain");
                    AssertEqual(previous.Id, (await testDb.Driver.Signals.ReadAsync(readMail.Id).ConfigureAwait(false))!.ToCaptainId ?? "(null)", "Read mail stays where it was read");
                    AssertEqual(previous.Id, (await testDb.Driver.Signals.ReadAsync(otherMissionMail.Id).ConfigureAwait(false))!.ToCaptainId ?? "(null)", "Mail for another mission stays");
                    AssertEqual(previous.Id, (await testDb.Driver.Signals.ReadAsync(captainMail.Id).ConfigureAwait(false))!.ToCaptainId ?? "(null)", "Mail addressed to a captain stays");
                }
            });

            // The first assignment of a mission, and a relaunch on the same captain, announce nothing.
            await RunTest("TryAssign_FirstAssignmentOrSameCaptain_PostsNoReassignmentNote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    FakeBoardNotePoster poster = new FakeBoardNotePoster();
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    missionService.ReassignmentNotePoster = poster;

                    Vessel vessel = new Vessel("no-note-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("only-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    Mission first = new Mission("Fresh slice", "Implements the slice.");
                    first.VesselId = vessel.Id;
                    first.Persona = "Worker";
                    first.Status = MissionStatusEnum.Pending;
                    first = await testDb.Driver.Missions.CreateAsync(first).ConfigureAwait(false);
                    AssertTrue(await missionService.TryAssignAsync(first, vessel).ConfigureAwait(false), "The first assignment must succeed");
                    AssertEqual(0, poster.Posts.Count, "A first assignment is not a reassignment");
                    Mission? finished = await testDb.Driver.Missions.ReadAsync(first.Id).ConfigureAwait(false);
                    finished!.Status = MissionStatusEnum.Complete;
                    await testDb.Driver.Missions.UpdateAsync(finished).ConfigureAwait(false);

                    Mission again = new Mission("Slice relaunched on its captain", "Implements the slice.");
                    again.VesselId = vessel.Id;
                    again.Persona = "Worker";
                    again.Status = MissionStatusEnum.Pending;
                    again = await testDb.Driver.Missions.CreateAsync(again).ConfigureAwait(false);
                    await MissionAttemptProvenanceRecorder.StartAsync(testDb.Driver, again, captain, 8812, null).ConfigureAwait(false);
                    Captain? idle = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    idle!.State = CaptainStateEnum.Idle;
                    idle.CurrentMissionId = null;
                    idle.CurrentDockId = null;
                    await testDb.Driver.Captains.UpdateAsync(idle).ConfigureAwait(false);
                    AssertTrue(await missionService.TryAssignAsync(again, vessel).ConfigureAwait(false), "The relaunch must succeed");
                    AssertEqual(0, poster.Posts.Count, "A relaunch on the same captain is not a reassignment");
                }
            });

            await RunTest("TryAssign_StartRefThatDoesNotResolve_FailsByNameAndReleasesTheCaptain_ResolvableRefCutsTheBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    int launchCalls = 0;
                    captainService.OnLaunchAgent = (_, _, _) =>
                    {
                        launchCalls++;
                        return Task.FromResult(12345);
                    };
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = new Vessel("start-ref-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("start-ref-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission gone = new Mission("Continue from a ref that is gone", "Continues the slice.");
                    gone.VesselId = vessel.Id;
                    gone.Persona = "Worker";
                    gone.Status = MissionStatusEnum.Pending;
                    gone.StartFromRef = "recover/gone";
                    gone = await testDb.Driver.Missions.CreateAsync(gone).ConfigureAwait(false);
                    gone.StartFromRef = "recover/gone";
                    await testDb.Driver.Missions.UpdateAsync(gone).ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(gone, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(gone.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Mission must still exist");
                    AssertFalse(assigned, "An unresolvable start ref must not assign.");
                    AssertTrue(readBack!.Status == MissionStatusEnum.Failed,
                        "The mission fails rather than falling back to the default branch; assignment=" + assigned
                        + ", state=" + readBack.AssignmentState + ", reason=" + (readBack.FailureReason ?? "(none)"));
                    AssertTrue((readBack.FailureReason ?? "").StartsWith("start_from_ref_missing", StringComparison.Ordinal), "The failure is named: " + (readBack.FailureReason ?? "(none)"));
                    AssertContains("recover/gone", readBack.FailureReason ?? "", "The failure names the ref.");
                    AssertNull(readBack.CaptainId, "No captain stays attached to a failed assignment.");
                    AssertEqual(0, git.ForceUpdateBranchRefCalls.Count, "No branch is cut for a ref that does not resolve.");
                    Captain? captainAfter = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, captainAfter!.State, "The captain is released.");

                    // The same mission shape with a ref that resolves is cut at that commit before provisioning.
                    missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    const string resolvedStartCommit = "abcdef0123456789abcdef0123456789abcdef01";
                    git.RevisionCommitShaResult = resolvedStartCommit;
                    git.IsAncestorResult = true;
                    Mission good = new Mission("Continue from the accepted tip", "Continues the slice.");
                    good.VesselId = vessel.Id;
                    good.Persona = "Worker";
                    good.Status = MissionStatusEnum.Pending;
                    good.StartFromRef = "recover/accepted-tip-abc1234";
                    good = await testDb.Driver.Missions.CreateAsync(good).ConfigureAwait(false);
                    good.StartFromRef = "recover/accepted-tip-abc1234";
                    await testDb.Driver.Missions.UpdateAsync(good).ConfigureAwait(false);

                    bool assignedGood = await missionService.TryAssignAsync(good, vessel).ConfigureAwait(false);

                    Mission? goodBack = await testDb.Driver.Missions.ReadAsync(good.Id).ConfigureAwait(false);
                    AssertNotNull(goodBack, "Mission must still exist");
                    AssertTrue(assignedGood, "A resolvable start ref assigns; state=" + goodBack!.AssignmentState + " status=" + goodBack.Status + " reason: " + (goodBack.FailureReason ?? "(none)"));
                    AssertEqual(1, git.ForceUpdateBranchRefCalls.Count, "The mission branch is cut once.");
                    AssertEqual(vessel.LocalPath + ":" + goodBack.BranchName + ":" + resolvedStartCommit, git.ForceUpdateBranchRefCalls[0], "The branch is cut at the full start-ref commit before provisioning.");
                    AssertTrue(git.IsAncestorCalls.Any(call => call.EndsWith(":" + resolvedStartCommit + ":HEAD", StringComparison.Ordinal)),
                        "The provisioned checkout must be checked for the resolved start commit before launch.");

                    goodBack.Status = MissionStatusEnum.Complete;
                    await testDb.Driver.Missions.UpdateAsync(goodBack).ConfigureAwait(false);

                    Captain rescueCaptain = new Captain("rescue-start-ref-captain") { State = CaptainStateEnum.Idle };
                    rescueCaptain = await testDb.Driver.Captains.CreateAsync(rescueCaptain).ConfigureAwait(false);
                    git.IsAncestorResult = false;
                    Mission staleRescue = new Mission("Rescue from accepted tip", RescueMissionMarker.Marker + "\nRepair the reviewed work.")
                    {
                        VesselId = vessel.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.Pending,
                        StartFromRef = "recover/accepted-tip-abc1234"
                    };
                    staleRescue = await testDb.Driver.Missions.CreateAsync(staleRescue).ConfigureAwait(false);

                    bool assignedStale = await missionService.TryAssignAsync(staleRescue, vessel).ConfigureAwait(false);
                    Mission? staleBack = await testDb.Driver.Missions.ReadAsync(staleRescue.Id).ConfigureAwait(false);
                    Captain? rescueCaptainBack = await testDb.Driver.Captains.ReadAsync(rescueCaptain.Id).ConfigureAwait(false);
                    AssertFalse(assignedStale, "A rescue checkout that lacks the reviewed tip must not launch.");
                    AssertEqual(MissionStatusEnum.Failed, staleBack!.Status);
                    AssertContains("start_from_ref_base_missing", staleBack.FailureReason ?? String.Empty);
                    AssertNull(staleBack.CaptainId, "The rejected rescue must release its captain linkage.");
                    AssertEqual(CaptainStateEnum.Idle, rescueCaptainBack!.State, "The rejected rescue captain must return to Idle.");
                    AssertEqual(1, launchCalls, "Only the verified start-ref mission may launch.");
                    List<Dock> docksAfterStaleRejection = await testDb.Driver.Docks.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                    Dock? staleDock = docksAfterStaleRejection.FirstOrDefault(item =>
                        String.Equals(item.BranchName, staleBack.BranchName, StringComparison.Ordinal));
                    AssertTrue(staleDock != null && !staleDock.Active,
                        "The rejected rescue dock must be reclaimed and marked inactive.");
                    AssertFalse(Directory.Exists(staleDock!.WorktreePath),
                        "The rejected rescue worktree must be removed.");

                    git.IsAncestorResult = null;
                    Mission unverifiedRescue = new Mission("Rescue with unknown base", RescueMissionMarker.Marker + "\nRepair the reviewed work.")
                    {
                        VesselId = vessel.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.Pending,
                        StartFromRef = "recover/accepted-tip-abc1234"
                    };
                    unverifiedRescue = await testDb.Driver.Missions.CreateAsync(unverifiedRescue).ConfigureAwait(false);
                    bool assignedUnverified = await missionService.TryAssignAsync(unverifiedRescue, vessel).ConfigureAwait(false);
                    Mission? unverifiedBack = await testDb.Driver.Missions.ReadAsync(unverifiedRescue.Id).ConfigureAwait(false);
                    AssertFalse(assignedUnverified, "A rescue with unknown ancestry must fail closed.");
                    AssertContains("start_from_ref_unverified", unverifiedBack!.FailureReason ?? String.Empty);
                    AssertEqual(1, launchCalls, "Unknown rescue ancestry must not reach the agent launch.");
                }
            });

            await RunTest("TryAssign_TwoMissionsOneIdleCaptainConcurrently_OnlyOneProvisions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = new Vessel("race-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("only-captain");
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission first = new Mission("First mission", "Runs.");
                    first.VesselId = vessel.Id;
                    first.Persona = "Worker";
                    first.Status = MissionStatusEnum.Pending;
                    first = await testDb.Driver.Missions.CreateAsync(first).ConfigureAwait(false);
                    Mission second = new Mission("Second mission", "Waits.");
                    second.VesselId = vessel.Id;
                    second.Persona = "Worker";
                    second.Status = MissionStatusEnum.Pending;
                    second = await testDb.Driver.Missions.CreateAsync(second).ConfigureAwait(false);

                    // Hold the first pass open inside provisioning so the second pass selects while
                    // the captain is still Idle in the database: the shape of two missions in one sweep.
                    TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    git.BeforeWorktreeCreate = () => gate.Task;
                    Task<bool> firstAssign = missionService.TryAssignAsync(first, vessel);
                    for (int i = 0; i < 100 && git.WorktreeCalls.Count == 0; i++)
                        await Task.Delay(20).ConfigureAwait(false);
                    AssertEqual(1, git.WorktreeCalls.Count, "The first pass must be held inside provisioning before the second starts.");
                    Task<bool> secondAssign = missionService.TryAssignAsync(second, vessel);
                    for (int i = 0; i < 25; i++)
                        await Task.Delay(20).ConfigureAwait(false);
                    gate.SetResult();
                    bool[] results = await Task.WhenAll(firstAssign, secondAssign).ConfigureAwait(false);

                    int assignedCount = results.Count(r => r);
                    AssertEqual(1, assignedCount, "Exactly one of two missions can take the one idle captain.");
                    AssertEqual(1, git.WorktreeCalls.Count, "Only the mission that takes the captain may provision a dock; the other must wait without provisioning, worktrees created: " + git.WorktreeCalls.Count);

                    Mission? firstBack = await testDb.Driver.Missions.ReadAsync(first.Id).ConfigureAwait(false);
                    Mission? secondBack = await testDb.Driver.Missions.ReadAsync(second.Id).ConfigureAwait(false);
                    Mission waiting = results[0] ? secondBack! : firstBack!;
                    AssertEqual(MissionStatusEnum.Pending, waiting.Status, "The mission that lost the captain stays Pending.");
                    // The concurrent pass queues behind the admission transition, then finds the only captain taken.
                    AssertEqual(MissionAssignmentStateEnum.WaitingForIdleCaptain, waiting.AssignmentState,
                        "The concurrent pass waits for the admission transition and then reports the captain it lacks.");
                    AssertTrue(String.IsNullOrEmpty(waiting.BranchName), "No branch name is written for a mission that never provisioned.");
                }
            });

            await RunTest("TryAssign_ActiveBuildSiblingMission_BlocksBeforeProvisioning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    captains.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    Vessel producer = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("lane-producer-assignment", "https://github.com/test/lane-producer-assignment.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            AllowConcurrentMissions = true
                        }).ConfigureAwait(false);
                    Vessel consumer = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("lane-consumer-assignment", "https://github.com/test/lane-consumer-assignment.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            AllowConcurrentMissions = true
                        }).ConfigureAwait(false);
                    consumer.SiblingRepos = "[{\"relativePath\":\"../producer\",\"vesselRef\":\""
                        + producer.Id + "\",\"buildParticipant\":true}]";
                    consumer = await testDb.Driver.Vessels.UpdateAsync(consumer).ConfigureAwait(false);

                    await testDb.Driver.Missions.CreateAsync(new Mission("operator work", "No objective link.")
                    {
                        TenantId = Constants.DefaultTenantId,
                        VesselId = producer.Id,
                        Status = MissionStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Mission candidate = await testDb.Driver.Missions.CreateAsync(
                        new Mission("candidate", "Must wait for the sibling lane.")
                        {
                            TenantId = Constants.DefaultTenantId,
                            VesselId = consumer.Id,
                            Status = MissionStatusEnum.Pending
                        }).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(new Captain("lane-captain")
                    {
                        State = CaptainStateEnum.Idle
                    }).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(candidate, consumer).ConfigureAwait(false);
                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(candidate.Id).ConfigureAwait(false);

                    AssertFalse(assigned, "An active build sibling must block assignment.");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForVesselMutex, readBack!.AssignmentState);
                    AssertEqual(0, git.WorktreeCalls.Count, "A blocked sibling mission must not provision a dock.");
                }
            });

            await RunTest("TryAssign_RefusedAdmissionWithoutReason_RemainsDeferred", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    RefusingAdmissionWithoutReason admission = new RefusingAdmissionWithoutReason();
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains,
                        resourcePressureAdmission: admission);
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("admission-test", "https://example.com/repo.git"));
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("refused", "No reason supplied")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.Pending
                    });
                    AssertFalse(await missions.TryAssignAsync(mission, vessel), "Refusal must prevent assignment");
                    Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionAssignmentStateEnum.WaitingForResourcePressure, stored!.AssignmentState,
                        "The boolean decision controls admission even when its explanation is empty");
                    AssertNull(stored.CaptainId, "Refused work must not claim a captain");
                    AssertNotNull(stored.LastAdmissionObservation, "The actual refusal must persist");
                    AssertFalse(stored.LastAdmissionObservation!.Admit, "Stored decision remains refused");
                    AssertNotNull(stored.LastAdmissionObservation.PressureDecision, "The evaluated pressure decision is retained");
                    AssertEqual(1, admission.Calls, "One evaluation occurred");
                    settings.MaxConcurrentCaptainWorkloads = 1;
                    Vessel other = await testDb.Driver.Vessels.CreateAsync(new Vessel("other-admission", "https://example.com/other.git"));
                    await testDb.Driver.Missions.CreateAsync(new Mission("active elsewhere", "Global count")
                    {
                        VesselId = other.Id, Status = MissionStatusEnum.InProgress
                    });
                    AssertFalse(await missions.TryAssignAsync(mission, vessel), "Global limit defers launch");
                    Mission limited = (await testDb.Driver.Missions.ReadAsync(mission.Id))!;
                    AssertTrue(limited.LastAdmissionObservation!.GlobalLimitReached, "Global refusal is recorded");
                    AssertNull(limited.LastAdmissionObservation.PressureDecision, "Pressure was not evaluated after global refusal");
                    AssertEqual(1, admission.Calls, "Global refusal does not invoke the pressure policy");
                }
            });

            await RunTest("TryAssign_ParallelAssignmentsInSeparateLanes_LaunchNoMoreThanGlobalWorkloadLimit", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxConcurrentCaptainWorkloads = 1;
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    int launches = 0;
                    captains.OnLaunchAgent = (_, _, _) =>
                    {
                        Interlocked.Increment(ref launches);
                        return Task.FromResult(12345);
                    };
                    // Each admission evaluation waits for the other pass to arrive, so without one
                    // reservation held from the count to the durable write both passes read zero
                    // active workloads under the limit before either records its assignment.
                    BarrierAdmission admission = new BarrierAdmission(2, TimeSpan.FromSeconds(2));
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains,
                        resourcePressureAdmission: admission);

                    Vessel firstVessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("workload-lane-a", "https://github.com/test/workload-lane-a.git") { DefaultBranch = "main" }).ConfigureAwait(false);
                    Vessel secondVessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("workload-lane-b", "https://github.com/test/workload-lane-b.git") { DefaultBranch = "main" }).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(new Captain("workload-captain-a") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(new Captain("workload-captain-b") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);
                    Mission first = await testDb.Driver.Missions.CreateAsync(new Mission("lane a work", "Runs.")
                    {
                        VesselId = firstVessel.Id, Persona = "Worker", Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);
                    Mission second = await testDb.Driver.Missions.CreateAsync(new Mission("lane b work", "Runs.")
                    {
                        VesselId = secondVessel.Id, Persona = "Worker", Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    bool[] results = await Task.WhenAll(
                        Task.Run(() => missions.TryAssignAsync(first, firstVessel)),
                        Task.Run(() => missions.TryAssignAsync(second, secondVessel))).ConfigureAwait(false);

                    AssertEqual(1, results.Count(r => r), "Exactly one assignment may pass a global workload limit of one.");
                    AssertEqual(1, Volatile.Read(ref launches), "Exactly one agent may launch under a global workload limit of one.");
                    Mission firstBack = (await testDb.Driver.Missions.ReadAsync(first.Id).ConfigureAwait(false))!;
                    Mission secondBack = (await testDb.Driver.Missions.ReadAsync(second.Id).ConfigureAwait(false))!;
                    Mission deferred = results[0] ? secondBack : firstBack;
                    AssertEqual(MissionStatusEnum.Pending, deferred.Status, "The deferred mission stays Pending.");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForResourcePressure, deferred.AssignmentState,
                        "The deferred mission waits on workload admission.");
                    AssertTrue(deferred.LastAdmissionObservation!.GlobalLimitReached,
                        "The deferred mission counted the winner's durable assignment.");
                }
            });

            await RunTest("TryAssign_MissionWithUnknownDependency_ShowsWaitingForDependency", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = new Vessel("missing-dep-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("missing-dep-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    // AdmiralService.DispatchVoyageAsync rejects unknown DependsOnMissionId
                    // at validation time, so the "dependency == null" gate in TryAssignAsync
                    // is reachable only when a dependency record has been deleted (or never
                    // persisted) after the mission row was created. Seed that state directly.
                    Mission orphan = new Mission("Orphaned dependent", "Dependency was deleted.");
                    orphan.VesselId = vessel.Id;
                    orphan.Status = MissionStatusEnum.Pending;
                    orphan.DependsOnMissionId = "msn_dep_does_not_exist";
                    orphan = await testDb.Driver.Missions.CreateAsync(orphan).ConfigureAwait(false);

                    bool assigned = await missionService.TryAssignAsync(orphan, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "TryAssignAsync must return false when DependsOnMissionId resolves to no row");

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(orphan.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Mission must still exist after deferred assignment");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, readBack!.AssignmentState,
                        "Missing dependency must surface as WaitingForDependency, not Pending");
                    AssertEqual(MissionStatusEnum.Pending, readBack.Status, "Status must remain Pending while waiting on dependency");
                    AssertNull(readBack.CaptainId, "Captain must not be claimed when assignment is deferred for a dependency");

                    Captain? captainAfter = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(captainAfter, "Captain must still exist");
                    AssertEqual(CaptainStateEnum.Idle, captainAfter!.State,
                        "Captain must remain Idle when the dependency gate defers assignment");
                }
            });

            await RunTest("Dispatch_MissionWithPendingDependency_ShowsWaitingForDependency", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 2;
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 2;
                    StubGitService git = new StubGitService();

                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("pending-dep-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel.AllowConcurrentMissions = true;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("pending-dep-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    // Precursor mission seeded directly into DB in Pending state -- never
                    // dispatched so it stays Pending and blocks the dependent's assignment.
                    Mission precursor = new Mission("Precursor mission", "Stays Pending forever.");
                    precursor.VesselId = vessel.Id;
                    precursor.Status = MissionStatusEnum.Pending;
                    precursor = await testDb.Driver.Missions.CreateAsync(precursor).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription
                        {
                            Title = "Dependent mission",
                            Description = "Waits for the precursor.",
                            DependsOnMissionId = precursor.Id
                        }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync("Pending-dep voyage", "Test", vessel.Id, missions).ConfigureAwait(false);
                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    string dependentId = voyageMissions[0].Id;

                    bool sawWaiting = false;
                    Stopwatch poll = Stopwatch.StartNew();
                    while (poll.ElapsedMilliseconds < 2000)
                    {
                        Mission? m = await testDb.Driver.Missions.ReadAsync(dependentId).ConfigureAwait(false);
                        if (m != null && m.AssignmentState == MissionAssignmentStateEnum.WaitingForDependency)
                        {
                            sawWaiting = true;
                            break;
                        }
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertTrue(sawWaiting, "Mission with Pending dependency must reach WaitingForDependency within 2s");

                    Mission? finalDependent = await testDb.Driver.Missions.ReadAsync(dependentId).ConfigureAwait(false);
                    AssertNotNull(finalDependent, "Dependent mission must still exist");
                    AssertEqual(MissionStatusEnum.Pending, finalDependent!.Status, "Dependent mission status must remain Pending while waiting");
                }
            });

            await RunTest("Dispatch_VoyageRemainsOpen_AfterDispatchReturns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    DelayingDockService delayingDock = new DelayingDockService(realDock, delayMs: 1000);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, delayingDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, delayingDock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, delayingDock);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("voyage-state-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("voyage-state-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Voyage state probe", Description = "Voyage must stay Open right after dispatch." }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync("Voyage state voyage", "Test", vessel.Id, missions).ConfigureAwait(false);

                    // Background assignment is delayed by 1s -- read back the voyage from the
                    // database immediately and assert it is Open. Pre-M2 this would already be
                    // InProgress because assignment ran synchronously inside dispatch.
                    AssertEqual(VoyageStatusEnum.Open, voyage.Status, "Returned Voyage must be Open immediately after dispatch (no synchronous transition)");

                    Voyage? readBack = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Voyage must be persisted");
                    AssertEqual(VoyageStatusEnum.Open, readBack!.Status, "Persisted voyage must be Open immediately after dispatch");
                }
            });

            await RunTest("Dispatch_PersistsPendingAssignmentState_BeforeBackgroundTaskRuns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();

                    // Long delay in the dock keeps the background task busy so we can observe
                    // the persisted Pending state before any transition happens.
                    IDockService realDock = new DockService(logging, testDb.Driver, settings, git);
                    DelayingDockService delayingDock = new DelayingDockService(realDock, delayMs: 5000);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, delayingDock);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, delayingDock, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, delayingDock);
                    admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("pending-state-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("pending-state-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Pending persistence probe", Description = "AssignmentState=Pending must be persisted by dispatch." }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync("Pending state voyage", "Test", vessel.Id, missions).ConfigureAwait(false);

                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertTrue(voyageMissions.Count == 1, "Voyage should have one mission");
                    string missionId = voyageMissions[0].Id;

                    // Read mission immediately after dispatch returns. Either we observe Pending
                    // (background task hasn't transitioned yet) or Provisioning (background task
                    // started but is still in dock.ProvisionAsync due to the 5s delay).
                    // Crucially, the row must NOT contain the legacy default of an uninitialized
                    // assignment state -- some persisted value must exist.
                    Mission? snapshot = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    AssertNotNull(snapshot, "Mission must be persisted immediately after dispatch returns");

                    bool initialIsValid =
                        snapshot!.AssignmentState == MissionAssignmentStateEnum.Pending ||
                        snapshot!.AssignmentState == MissionAssignmentStateEnum.Provisioning;
                    AssertTrue(initialIsValid,
                        "Immediately after dispatch, mission AssignmentState must be Pending (persisted by dispatch) or Provisioning (background task already past captain claim); was " + snapshot!.AssignmentState);
                }
            });

            await RunTest("DispatchPending_ExpectedWait_DoesNotWarnOrSetRetryFlag", async () =>
            {
                foreach (MissionAssignmentStateEnum waitState in new[]
                {
                    MissionAssignmentStateEnum.WaitingForDependency,
                    MissionAssignmentStateEnum.WaitingForVesselMutex,
                    MissionAssignmentStateEnum.WaitingForIdleCaptain
                })
                {
                    await RunDispatchPendingFalseReturnScenarioAsync(
                        waitState,
                        expectRetryFlag: false,
                        expectWarnAssignLine: false,
                        expectDebugAssignLine: true).ConfigureAwait(false);
                }
            });

            await RunTest("DispatchPending_RealFailure_WarnsAndSetsRetryFlag", async () =>
            {
                await RunDispatchPendingFalseReturnScenarioAsync(
                    MissionAssignmentStateEnum.Failed,
                    expectRetryFlag: true,
                    expectWarnAssignLine: true,
                    expectDebugAssignLine: false).ConfigureAwait(false);
            });

            await RunTest("DispatchPending_UnexpectedPendingFalseReturn_WarnsAndSetsRetryFlag", async () =>
            {
                await RunDispatchPendingFalseReturnScenarioAsync(
                    MissionAssignmentStateEnum.Pending,
                    expectRetryFlag: true,
                    expectWarnAssignLine: true,
                    expectDebugAssignLine: false).ConfigureAwait(false);
            });

            await RunTest("DispatchPending_OtherUnexpectedNonWaitStates_WarnAndSetRetryFlag", async () =>
            {
                foreach (MissionAssignmentStateEnum unexpected in new[]
                {
                    MissionAssignmentStateEnum.Provisioning,
                    MissionAssignmentStateEnum.Assigned
                })
                {
                    await RunDispatchPendingFalseReturnScenarioAsync(
                        unexpected,
                        expectRetryFlag: true,
                        expectWarnAssignLine: true,
                        expectDebugAssignLine: false).ConfigureAwait(false);
                }
            });

            await RunTest("DispatchPending_MixedBatch_AllThreeExpectedWaitsPlusOneFailure_OnlyFailureSetsRetryFlag", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string logDir = Path.Combine(Path.GetTempPath(), "armada_dispatch_log_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(logDir);
                    string logPath = Path.Combine(logDir, "admiral.log");

                    try
                    {
                        LoggingModule logging = CreateFileLogging(logPath);
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                        IVoyageService voyageService = new VoyageService(logging, testDb.Driver);

                        Vessel vessel = new Vessel("triple-wait-vessel", "https://github.com/test/repo.git");
                        vessel.DefaultBranch = "main";
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("triple-wait-captain");
                        captain.State = CaptainStateEnum.Idle;
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Mission dep = new Mission("Wait dependency", "Blocked on dependency.");
                        dep.VesselId = vessel.Id;
                        dep.Status = MissionStatusEnum.Pending;
                        dep = await testDb.Driver.Missions.CreateAsync(dep).ConfigureAwait(false);

                        Mission mutex = new Mission("Wait mutex", "Blocked on vessel mutex.");
                        mutex.VesselId = vessel.Id;
                        mutex.Status = MissionStatusEnum.Pending;
                        mutex = await testDb.Driver.Missions.CreateAsync(mutex).ConfigureAwait(false);

                        Mission idle = new Mission("Wait captain", "Blocked on idle captain.");
                        idle.VesselId = vessel.Id;
                        idle.Status = MissionStatusEnum.Pending;
                        idle = await testDb.Driver.Missions.CreateAsync(idle).ConfigureAwait(false);

                        Mission failed = new Mission("Real failure", "Assignment failed.");
                        failed.VesselId = vessel.Id;
                        failed.Status = MissionStatusEnum.Pending;
                        failed = await testDb.Driver.Missions.CreateAsync(failed).ConfigureAwait(false);

                        Dictionary<string, MissionAssignmentStateEnum> falseStates = new Dictionary<string, MissionAssignmentStateEnum>
                        {
                            { dep.Id, MissionAssignmentStateEnum.WaitingForDependency },
                            { mutex.Id, MissionAssignmentStateEnum.WaitingForVesselMutex },
                            { idle.Id, MissionAssignmentStateEnum.WaitingForIdleCaptain },
                            { failed.Id, MissionAssignmentStateEnum.Failed }
                        };
                        AssignStateStubMissionService missionService = new AssignStateStubMissionService(falseStates);
                        AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                        SetRetryDispatchNeeded(admiral, false);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertTrue(GetRetryDispatchNeeded(admiral),
                            "Batch with any real failure must set retry dispatch flag regardless of how many expected waits accompany it");

                        string logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
                        AssertFalse(ContainsAssignWarnLine(logText, dep.Id), "WaitingForDependency must not WARN");
                        AssertFalse(ContainsAssignWarnLine(logText, mutex.Id), "WaitingForVesselMutex must not WARN");
                        AssertFalse(ContainsAssignWarnLine(logText, idle.Id), "WaitingForIdleCaptain must not WARN");
                        AssertTrue(ContainsAssignWarnLine(logText, failed.Id), "Failed mission must WARN");
                    }
                    finally
                    {
                        if (Directory.Exists(logDir))
                        {
                            Directory.Delete(logDir, true);
                        }
                    }
                }
            });

            await RunTest("DispatchPending_MixedBatch_OnlyRealFailureSetsRetryFlag", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string logDir = Path.Combine(Path.GetTempPath(), "armada_dispatch_log_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(logDir);
                    string logPath = Path.Combine(logDir, "admiral.log");

                    try
                    {
                        LoggingModule logging = CreateFileLogging(logPath);
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                        IVoyageService voyageService = new VoyageService(logging, testDb.Driver);

                        Vessel vessel = new Vessel("mixed-batch-vessel", "https://github.com/test/repo.git");
                        vessel.DefaultBranch = "main";
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("mixed-batch-captain");
                        captain.State = CaptainStateEnum.Idle;
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Mission waitMission = new Mission("Expected wait mission", "Deferred by mutex.");
                        waitMission.VesselId = vessel.Id;
                        waitMission.Status = MissionStatusEnum.Pending;
                        waitMission = await testDb.Driver.Missions.CreateAsync(waitMission).ConfigureAwait(false);

                        Mission failedMission = new Mission("Real failure mission", "Assignment failed.");
                        failedMission.VesselId = vessel.Id;
                        failedMission.Status = MissionStatusEnum.Pending;
                        failedMission = await testDb.Driver.Missions.CreateAsync(failedMission).ConfigureAwait(false);

                        Dictionary<string, MissionAssignmentStateEnum> falseStates = new Dictionary<string, MissionAssignmentStateEnum>
                        {
                            { waitMission.Id, MissionAssignmentStateEnum.WaitingForVesselMutex },
                            { failedMission.Id, MissionAssignmentStateEnum.Failed }
                        };
                        AssignStateStubMissionService missionService = new AssignStateStubMissionService(falseStates);
                        AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                        SetRetryDispatchNeeded(admiral, true);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertTrue(GetRetryDispatchNeeded(admiral), "Mixed batch with a real failure must set retry dispatch flag");

                        string logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
                        AssertFalse(ContainsAssignWarnLine(logText, waitMission.Id),
                            "Expected-wait mission must not emit assign WARN line");
                        AssertTrue(ContainsAssignWarnLine(logText, failedMission.Id),
                            "Real failure mission must emit assign WARN line with mission id");
                        AssertContains("AssignmentState=Failed", logText,
                            "Real failure WARN must include AssignmentState");
                    }
                    finally
                    {
                        if (Directory.Exists(logDir))
                        {
                            Directory.Delete(logDir, true);
                        }
                    }
                }
            });

            await RunTest("DispatchPending_OnlyExpectedWaits_DoesNotSetRetryFlag", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string logDir = Path.Combine(Path.GetTempPath(), "armada_dispatch_log_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(logDir);
                    string logPath = Path.Combine(logDir, "admiral.log");

                    try
                    {
                        LoggingModule logging = CreateFileLogging(logPath);
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                        ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                        IVoyageService voyageService = new VoyageService(logging, testDb.Driver);

                        Vessel vessel = new Vessel("expected-only-vessel", "https://github.com/test/repo.git");
                        vessel.DefaultBranch = "main";
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("expected-only-captain");
                        captain.State = CaptainStateEnum.Idle;
                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Mission m1 = new Mission("Wait dependency", "Blocked on dependency.");
                        m1.VesselId = vessel.Id;
                        m1.Status = MissionStatusEnum.Pending;
                        m1 = await testDb.Driver.Missions.CreateAsync(m1).ConfigureAwait(false);

                        Mission m2 = new Mission("Wait mutex", "Blocked on vessel mutex.");
                        m2.VesselId = vessel.Id;
                        m2.Status = MissionStatusEnum.Pending;
                        m2 = await testDb.Driver.Missions.CreateAsync(m2).ConfigureAwait(false);

                        Dictionary<string, MissionAssignmentStateEnum> falseStates = new Dictionary<string, MissionAssignmentStateEnum>
                        {
                            { m1.Id, MissionAssignmentStateEnum.WaitingForDependency },
                            { m2.Id, MissionAssignmentStateEnum.WaitingForVesselMutex }
                        };
                        AssignStateStubMissionService missionService = new AssignStateStubMissionService(falseStates);
                        AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                        SetRetryDispatchNeeded(admiral, true);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertFalse(GetRetryDispatchNeeded(admiral), "Batch with only expected waits must clear retry dispatch flag");

                        string logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
                        AssertFalse(ContainsAssignWarnLine(logText, m1.Id), "First expected wait must not WARN");
                        AssertFalse(ContainsAssignWarnLine(logText, m2.Id), "Second expected wait must not WARN");
                    }
                    finally
                    {
                        if (Directory.Exists(logDir))
                        {
                            Directory.Delete(logDir, true);
                        }
                    }
                }
            });
        }

        #endregion

        #region Private-Methods

        private async Task RunDispatchPendingFalseReturnScenarioAsync(
            MissionAssignmentStateEnum assignmentStateOnFalse,
            bool expectRetryFlag,
            bool expectWarnAssignLine,
            bool expectDebugAssignLine)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
            {
                string logDir = Path.Combine(Path.GetTempPath(), "armada_dispatch_log_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "admiral.log");

                try
                {
                    LoggingModule logging = CreateFileLogging(logPath);
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);

                    Vessel vessel = new Vessel("dispatch-pending-vessel", "https://github.com/test/repo.git");
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("dispatch-pending-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission mission = new Mission("Dispatch pending probe", "TryAssignAsync false return.");
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.Pending;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Dictionary<string, MissionAssignmentStateEnum> falseStates = new Dictionary<string, MissionAssignmentStateEnum>
                    {
                        { mission.Id, assignmentStateOnFalse }
                    };
                    AssignStateStubMissionService missionService = new AssignStateStubMissionService(falseStates);
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                    SetRetryDispatchNeeded(admiral, true);

                    await admiral.HealthCheckAsync().ConfigureAwait(false);

                    bool retryFlag = GetRetryDispatchNeeded(admiral);
                    AssertEqual(expectRetryFlag, retryFlag,
                        "Retry flag mismatch for AssignmentState=" + assignmentStateOnFalse);

                    string logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
                    bool hasWarn = ContainsAssignWarnLine(logText, mission.Id);
                    AssertEqual(expectWarnAssignLine, hasWarn,
                        "WARN assign line expectation mismatch for AssignmentState=" + assignmentStateOnFalse);

                    bool hasDebug = logText.Contains(
                        "pending mission " + mission.Id + " not assigned yet (AssignmentState=" + assignmentStateOnFalse + ")",
                        StringComparison.Ordinal);
                    AssertEqual(expectDebugAssignLine, hasDebug,
                        "Debug assign line expectation mismatch for AssignmentState=" + assignmentStateOnFalse);
                }
                finally
                {
                    if (Directory.Exists(logDir))
                    {
                        Directory.Delete(logDir, true);
                    }
                }
            }
        }

        private static bool GetRetryDispatchNeeded(AdmiralService service)
        {
            System.Reflection.FieldInfo? field = typeof(AdmiralService).GetField(
                "_RetryDispatchNeeded",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException("Could not find _RetryDispatchNeeded field.");
            }

            return (bool)field!.GetValue(service)!;
        }

        private static void SetRetryDispatchNeeded(AdmiralService service, bool value)
        {
            System.Reflection.FieldInfo? field = typeof(AdmiralService).GetField(
                "_RetryDispatchNeeded",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException("Could not find _RetryDispatchNeeded field.");
            }

            field!.SetValue(service, value);
        }

        private static bool ContainsAssignWarnLine(string logText, string missionId)
        {
            return logText.Contains("could not assign pending mission " + missionId, StringComparison.Ordinal);
        }

        private static LoggingModule CreateFileLogging(string logPath)
        {
            LoggingModule logging = new LoggingModule(logPath, FileLoggingMode.SingleLogFile, false);
            logging.Settings.EnableConsole = false;
            logging.Settings.MinimumSeverity = Severity.Debug;
            return logging;
        }

        private static async Task<Signal> CreateMailAsync(TestDatabase testDb, string? toCaptainId, string? missionId, string message)
        {
            Signal signal = new Signal(SignalTypeEnum.Mail, message);
            signal.ToCaptainId = toCaptainId;
            signal.MissionId = missionId;
            return await testDb.Driver.Signals.CreateAsync(signal).ConfigureAwait(false);
        }

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private sealed class RefusingAdmissionWithoutReason : IResourcePressureAdmission
        {
            public int Calls { get; private set; }
            public ResourcePressureDecision Evaluate(int activeBuildPressure)
            {
                Calls++;
                return new ResourcePressureDecision { Admit = false, Reason = String.Empty };
            }

            public void MarkOom() { }

            public bool IsCapacitySuspended() { return false; }
        }

        /// <summary>
        /// Admits every evaluation, but first holds each caller until the expected number of
        /// callers has arrived or the wait times out.
        /// </summary>
        private sealed class BarrierAdmission : IResourcePressureAdmission
        {
            private readonly int _Parties;
            private readonly TimeSpan _Timeout;
            private readonly ManualResetEventSlim _AllArrived = new ManualResetEventSlim(false);
            private int _Arrived;

            public BarrierAdmission(int parties, TimeSpan timeout)
            {
                _Parties = parties;
                _Timeout = timeout;
            }

            public ResourcePressureDecision Evaluate(int activeBuildPressure)
            {
                if (Interlocked.Increment(ref _Arrived) >= _Parties) _AllArrived.Set();
                _AllArrived.Wait(_Timeout);
                return new ResourcePressureDecision { Admit = true, Reason = "barrier admitted" };
            }

            public void MarkOom() { }

            public bool IsCapacitySuspended() { return false; }
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        /// <summary>
        /// Build a vessel backed by an on-disk bare repository so real git provisioning runs against it.
        /// The remote URL is cleared so provisioning never tries to fetch from a network host.
        /// </summary>
        private static Vessel NewRealGitVessel(string name, string bareRepoPath)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/repo.git");
            vessel.LocalPath = bareRepoPath;
            vessel.RepoUrl = String.Empty;
            vessel.DefaultBranch = "main";
            vessel.AllowConcurrentMissions = true;
            return vessel;
        }

        /// <summary>
        /// Create <paramref name="branchName"/> at main in the bare repository and advance it by one commit,
        /// so the branch head is a commit that is NOT reachable from main. Returns the new commit sha.
        /// </summary>
        private static string CommitAheadOnBranch(string bareRepoPath, string branchName, string message)
        {
            string worktree = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
            RunGitProcess(bareRepoPath, "worktree", "add", "-b", branchName, worktree, "main");
            try
            {
                RunGitProcess(worktree, "config", "user.name", "Armada Tests");
                RunGitProcess(worktree, "config", "user.email", "armada-tests@example.com");
                File.WriteAllText(Path.Combine(worktree, "work.txt"), "committed work\n");
                RunGitProcess(worktree, "add", "work.txt");
                RunGitProcess(worktree, "commit", "-m", message);
                return RunGitProcess(worktree, "rev-parse", "HEAD").Trim();
            }
            finally
            {
                RunGitProcess(bareRepoPath, "worktree", "remove", "--force", worktree);
            }
        }

        /// <summary>Run a git command in <paramref name="workingDirectory"/> and return its standard output.</summary>
        private static string RunGitProcess(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string standardOutput = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", arguments) + " failed (exit " + process.ExitCode + "): " + standardError.Trim());
                return standardOutput;
            }
        }

        #endregion

        #region Private-Types

        /// <summary>
        /// A stage-2 mission whose first checkout held uncommitted work when the admiral restarted: the mission is
        /// back to Pending with its branch and its stopped dock, and one idle captain is free to take it.
        /// </summary>
        private sealed class StageTwoRestart
        {
            public IMissionService MissionService { get; private set; } = null!;
            public FakeBoardNotePoster Poster { get; } = new FakeBoardNotePoster();
            public Vessel Vessel { get; private set; } = null!;
            public Mission Stage { get; private set; } = null!;
            public string BareRepo { get; private set; } = "";
            public string BranchHead { get; private set; } = "";

            public static async Task<StageTwoRestart> StopWithUncommittedWorkAsync(TestDatabase testDb, LoggingModule logging, ArmadaSettings settings, bool commitAfterSavingTheWork)
            {
                StageTwoRestart restart = new StageTwoRestart();
                GitService git = new GitService(logging);
                IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                missionService.ReassignmentNotePoster = restart.Poster;
                restart.MissionService = missionService;

                restart.BareRepo = global::Test.Shared.Infrastructure.TestGitRepoHelper.CreateBareRepoCopy();
                restart.Vessel = await testDb.Driver.Vessels.CreateAsync(NewRealGitVessel("stage-two-resume-vessel", restart.BareRepo)).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("chain") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);

                string branch = "armada/chain/stage-one";
                restart.BranchHead = CommitAheadOnBranch(restart.BareRepo, branch, "Stage one committed work");
                Mission predecessor = await testDb.Driver.Missions.CreateAsync(new Mission("Earlier stage", "Produced the branch")
                {
                    VesselId = restart.Vessel.Id, VoyageId = voyage.Id, Persona = "Worker", Status = MissionStatusEnum.Complete,
                    BranchName = branch, CommitHash = restart.BranchHead
                }).ConfigureAwait(false);
                restart.Stage = await testDb.Driver.Missions.CreateAsync(new Mission("Next stage", "Continue on the branch")
                {
                    VesselId = restart.Vessel.Id, VoyageId = voyage.Id, Persona = "Worker", Status = MissionStatusEnum.Pending,
                    DependsOnMissionId = predecessor.Id, BranchName = branch
                }).ConfigureAwait(false);

                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("stage-two-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);
                if (!await missionService.TryAssignAsync(restart.Stage, restart.Vessel).ConfigureAwait(false))
                    throw new InvalidOperationException("Fixture stage was not assigned");
                restart.Stage = (await testDb.Driver.Missions.ReadAsync(restart.Stage.Id).ConfigureAwait(false))!;
                Dock dock = (await testDb.Driver.Docks.ReadAsync(restart.Stage.DockId ?? "").ConfigureAwait(false))!;

                File.WriteAllText(Path.Combine(dock.WorktreePath!, "README.md"), "edited by the stopped captain\n");
                File.Delete(Path.Combine(dock.WorktreePath!, "work.txt"));
                File.WriteAllText(Path.Combine(dock.WorktreePath!, "new.txt"), "new file\n");

                if (commitAfterSavingTheWork)
                {
                    // The work was saved earlier and the captain has since committed on top, so the checkout the
                    // restart leaves is clean and its HEAD is a newer commit than the one the saved work sits on.
                    DockWorkSnapshot saved = await git.SnapshotDockWorkAsync(dock.WorktreePath!, restart.Stage.Id, "Example snapshot").ConfigureAwait(false);
                    if (!saved.HasRef) throw new InvalidOperationException("Fixture snapshot was not saved: " + saved.Message);
                    RunGitProcess(dock.WorktreePath!, "reset", "--hard", "HEAD");
                    RunGitProcess(dock.WorktreePath!, "clean", "-fd");
                    File.WriteAllText(Path.Combine(dock.WorktreePath!, "later.txt"), "committed later\n");
                    RunGitProcess(dock.WorktreePath!, "add", "later.txt");
                    RunGitProcess(dock.WorktreePath!, "-c", "user.name=Armada Tests", "-c", "user.email=armada-tests@example.com", "commit", "-m", "Committed after the snapshot");
                    restart.BranchHead = RunGitProcess(dock.WorktreePath!, "rev-parse", "HEAD").Trim();
                }

                // The restart leaves the mission Pending with its branch and its stopped dock, and frees the captain.
                restart.Stage.Status = MissionStatusEnum.Pending;
                restart.Stage.AssignmentState = MissionAssignmentStateEnum.Pending;
                restart.Stage.CaptainId = null;
                restart.Stage.ProcessId = null;
                await testDb.Driver.Missions.UpdateAsync(restart.Stage).ConfigureAwait(false);
                Captain freed = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                freed.State = CaptainStateEnum.Idle;
                freed.CurrentMissionId = null;
                freed.ProcessId = null;
                await testDb.Driver.Captains.UpdateAsync(freed).ConfigureAwait(false);
                return restart;
            }
        }

        /// <summary>
        /// Mission service stub that returns false for configured mission ids and sets AssignmentState.
        /// </summary>
        private sealed class AssignStateStubMissionService : IMissionService
        {
            private readonly Dictionary<string, MissionAssignmentStateEnum> _FalseStates;

            public AssignStateStubMissionService(Dictionary<string, MissionAssignmentStateEnum> falseStates)
            {
                _FalseStates = falseStates ?? throw new ArgumentNullException(nameof(falseStates));
            }

            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }

            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }

            public Func<string, string?>? OnGetMissionOutput { get; set; }

            public Func<Mission, bool, Task>? OnMissionOutcome { get; set; }

            public Task<bool> TryAssignAsync(Mission mission, Vessel vessel, CancellationToken token = default)
            {
                if (_FalseStates.TryGetValue(mission.Id, out MissionAssignmentStateEnum state))
                {
                    mission.AssignmentState = state;
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }

            public Task HandleCompletionAsync(Captain captain, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleCompletionAsync(Captain captain, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task ReapTerminalMissionBranchAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> ApproveReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, bool conditional = false, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> ClearOperatorReviewHoldAsync(string missionId, string operatorName, string reason, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> FailOperatorReviewHoldAsync(string missionId, string operatorName, string reason, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> ResolveHandoffOutcomeHoldAsync(string missionId, HandoffOutcomeHoldChoiceEnum choice, string operatorName, string reason, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Mission> DenyReviewAsync(string missionId, string? reviewedByUserId, string? comment = null, ReviewDenyActionEnum? actionOverride = null, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<int> RecoverDanglingHandoffsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<bool> AwaitsDefinitionOfDoneResultAsync(Mission mission, CancellationToken token = default)
                => Task.FromResult(false);

            public Task<int> StartInterruptedDefinitionOfDoneGateRerunsAsync(CancellationToken token = default)
                => Task.FromResult(0);

            public bool IsBroadScope(Mission mission) => false;

            public Task GenerateClaudeMdAsync(string worktreePath, Mission mission, Vessel vessel, Captain? captain = null, CancellationToken token = default)
                => throw new NotImplementedException();
        }

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

            public Task<string?> PrepareBranchFromRefAsync(Vessel vessel, string branchName, string startFromRef, CancellationToken token = default) => Task.FromResult<string?>(null);

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, bool detachedWorktree = false, CancellationToken token = default)
            {
                await Task.Delay(_DelayMs, CancellationToken.None).ConfigureAwait(false);
                return await _Inner.ProvisionAsync(vessel, captain, branchName, missionId, detachedWorktree, token).ConfigureAwait(false);
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default, string? caller = null, string? callerFile = null)
                => _Inner.ReclaimAsync(dockId, tenantId, token, caller, callerFile);

            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.RepairAsync(dockId, tenantId, token);

            public Task UnstickAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.UnstickAsync(dockId, tenantId, token);

            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.DeleteAsync(dockId, tenantId, token);

            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        /// <summary>
        /// Dock service that blocks inside ProvisionAsync until the test releases it. Holding the
        /// provisioning window open makes AssignmentState=Provisioning deterministically observable,
        /// where a fixed delay only made it observable if the poll happened to land inside the window.
        /// </summary>
        private sealed class GatedDockService : IDockService
        {
            private readonly IDockService _Inner;
            private readonly TaskCompletionSource _EnteredSource =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _ReleaseSource =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public GatedDockService(IDockService inner)
            {
                _Inner = inner;
            }

            /// <summary>Completes once provisioning has started and is waiting at the gate.</summary>
            public Task Entered => _EnteredSource.Task;

            /// <summary>Allow provisioning to proceed.</summary>
            public void Release() => _ReleaseSource.TrySetResult();

            public Task<string?> PrepareBranchFromRefAsync(Vessel vessel, string branchName, string startFromRef, CancellationToken token = default) => Task.FromResult<string?>(null);

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, bool detachedWorktree = false, CancellationToken token = default)
            {
                _EnteredSource.TrySetResult();
                await _ReleaseSource.Task.ConfigureAwait(false);
                return await _Inner.ProvisionAsync(vessel, captain, branchName, missionId, detachedWorktree, token).ConfigureAwait(false);
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default, string? caller = null, string? callerFile = null)
                => _Inner.ReclaimAsync(dockId, tenantId, token, caller, callerFile);

            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.RepairAsync(dockId, tenantId, token);

            public Task UnstickAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.UnstickAsync(dockId, tenantId, token);

            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.DeleteAsync(dockId, tenantId, token);

            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        /// <summary>
        /// Dock service that throws InvalidOperationException on the first ProvisionAsync call,
        /// then delegates to a real DockService on subsequent calls.
        /// </summary>
        /// <summary>Cancels one mission the first time a captain's quarantine is checked, then answers "quarantined".</summary>
        private sealed class CancellingQuarantineService : ICaptainQuarantineService
        {
            private readonly ICaptainQuarantineService _Inner;
            private readonly Armada.Core.Database.DatabaseDriver _Database;
            private readonly string _MissionId;

            public bool CancelledMission { get; private set; }

            public CancellingQuarantineService(ICaptainQuarantineService inner, Armada.Core.Database.DatabaseDriver database, string missionId)
            {
                _Inner = inner;
                _Database = database;
                _MissionId = missionId;
            }

            public bool IsQuarantined(Captain captain)
            {
                if (!CancelledMission)
                {
                    CancelledMission = true;
                    CancelMissionAsync(_Database, _MissionId).GetAwaiter().GetResult();
                }
                return true;
            }

            public Task QuarantineAsync(Captain captain, string reason, DateTime? retryAfterUtc, CancellationToken token = default) => _Inner.QuarantineAsync(captain, reason, retryAfterUtc, token);
            public Task<bool> TryQuarantineCrashLoopAsync(string captainId, string reason, DateTime untilUtc, CancellationToken token = default) => _Inner.TryQuarantineCrashLoopAsync(captainId, reason, untilUtc, token);
            public Task<CaptainQuarantineResult> QuarantineCaptainAsync(AuthContext auth, string captainId, string? reason, DateTime? untilUtc, CancellationToken token = default) => _Inner.QuarantineCaptainAsync(auth, captainId, reason, untilUtc, token);
            public Task<CaptainQuarantineResult> ReleaseCaptainAsync(AuthContext auth, string captainId, CancellationToken token = default) => _Inner.ReleaseCaptainAsync(auth, captainId, token);
            public Task RestoreExpiredQuarantinesAsync(CancellationToken token = default) => _Inner.RestoreExpiredQuarantinesAsync(token);
            public Task<bool> TryProbeRestoreAsync(Captain captain, CancellationToken token = default) => _Inner.TryProbeRestoreAsync(captain, token);
        }

        /// <summary>Cancels one mission while its dock is provisioning, then fails the provisioning.</summary>
        private sealed class CancelThenThrowDockService : IDockService
        {
            private readonly IDockService _Inner;
            private readonly Armada.Core.Database.DatabaseDriver _Database;
            private readonly string _MissionId;

            public bool CancelledMission { get; private set; }

            public CancelThenThrowDockService(IDockService inner, Armada.Core.Database.DatabaseDriver database, string missionId)
            {
                _Inner = inner;
                _Database = database;
                _MissionId = missionId;
            }

            public Task<string?> PrepareBranchFromRefAsync(Vessel vessel, string branchName, string startFromRef, CancellationToken token = default) => Task.FromResult<string?>(null);

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, bool detachedWorktree = false, CancellationToken token = default)
            {
                CancelledMission = true;
                await CancelMissionAsync(_Database, _MissionId).ConfigureAwait(false);
                throw new InvalidOperationException("Simulated dock provisioning failure after the mission was cancelled");
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default, string? caller = null, string? callerFile = null) => _Inner.ReclaimAsync(dockId, tenantId, token, caller, callerFile);
            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.RepairAsync(dockId, tenantId, token);
            public Task UnstickAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.UnstickAsync(dockId, tenantId, token);
            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.DeleteAsync(dockId, tenantId, token);
            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        /// <summary>Provisions a real dock, then runs a test action before handing the dock back.</summary>
        private sealed class AfterProvisionDockService : IDockService
        {
            private readonly IDockService _Inner;
            private readonly Func<Dock, Task> _AfterProvision;

            public string? ProvisionedDockId { get; private set; }

            public AfterProvisionDockService(IDockService inner, Func<Dock, Task> afterProvision)
            {
                _Inner = inner;
                _AfterProvision = afterProvision;
            }

            public Task<string?> PrepareBranchFromRefAsync(Vessel vessel, string branchName, string startFromRef, CancellationToken token = default) => Task.FromResult<string?>(null);

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, bool detachedWorktree = false, CancellationToken token = default)
            {
                Dock? dock = await _Inner.ProvisionAsync(vessel, captain, branchName, missionId, detachedWorktree, token).ConfigureAwait(false);
                if (dock != null)
                {
                    ProvisionedDockId = dock.Id;
                    await _AfterProvision(dock).ConfigureAwait(false);
                }
                return dock;
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default, string? caller = null, string? callerFile = null) => _Inner.ReclaimAsync(dockId, tenantId, token, caller, callerFile);
            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.RepairAsync(dockId, tenantId, token);
            public Task UnstickAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.UnstickAsync(dockId, tenantId, token);
            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.DeleteAsync(dockId, tenantId, token);
            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        /// <summary>
        /// Lets another mission claim the captain while the start ref resolves or the dock provisions, then fails
        /// that step: the start ref does not resolve and provisioning throws.
        /// </summary>
        private sealed class RivalClaimDockService : IDockService
        {
            private readonly IDockService _Inner;
            private readonly Func<Task> _ClaimForRival;

            public bool RivalClaimed { get; private set; }

            public RivalClaimDockService(IDockService inner, Func<Task> claimForRival)
            {
                _Inner = inner;
                _ClaimForRival = claimForRival;
            }

            public async Task<string?> PrepareBranchFromRefAsync(Vessel vessel, string branchName, string startFromRef, CancellationToken token = default)
            {
                await ClaimOnceAsync().ConfigureAwait(false);
                return null;
            }

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, bool detachedWorktree = false, CancellationToken token = default)
            {
                await ClaimOnceAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Simulated dock provisioning failure after another mission claimed the captain");
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default, string? caller = null, string? callerFile = null) => _Inner.ReclaimAsync(dockId, tenantId, token, caller, callerFile);
            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.RepairAsync(dockId, tenantId, token);
            public Task UnstickAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.UnstickAsync(dockId, tenantId, token);
            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.DeleteAsync(dockId, tenantId, token);
            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default) => _Inner.PurgeAsync(dockId, tenantId, token);

            private async Task ClaimOnceAsync()
            {
                if (RivalClaimed) return;
                await _ClaimForRival().ConfigureAwait(false);
                RivalClaimed = true;
            }
        }

        /// <summary>Claim a captain for another mission through the same compare-and-set assignment uses.</summary>
        private static async Task ClaimForRivalAsync(Armada.Core.Database.DatabaseDriver database, string captainId, string rivalMissionId)
        {
            bool claimed = await database.Captains.TryClaimAsync(Constants.DefaultTenantId, captainId, rivalMissionId, "dck_" + rivalMissionId).ConfigureAwait(false);
            if (!claimed) throw new InvalidOperationException("The rival claim did not land.");
        }

        /// <summary>Cancel a mission the way the voyage cancel route does: read it and write Cancelled.</summary>
        private static async Task CancelMissionAsync(Armada.Core.Database.DatabaseDriver database, string missionId)
        {
            Mission? current = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
            current!.Status = MissionStatusEnum.Cancelled;
            current.CompletedUtc = DateTime.UtcNow;
            await database.Missions.UpdateAsync(current).ConfigureAwait(false);
        }

        private sealed class HeldDockService : IDockService
        {
            private readonly IDockService _Inner;
            private readonly string _HolderPath;
            public int Calls { get; private set; }

            public HeldDockService(IDockService inner, string holderPath = "/tmp/holding-dock")
            {
                _Inner = inner;
                _HolderPath = holderPath;
            }

            public Task<string?> PrepareBranchFromRefAsync(Vessel vessel, string branchName, string startFromRef, CancellationToken token = default) => Task.FromResult<string?>(null);

            public Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, bool detachedWorktree = false, CancellationToken token = default)
            {
                Calls++;
                throw new InvalidOperationException("dock_worktree_held: branch held by worktree " + _HolderPath + " (pids 2147483647)");
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default, string? caller = null, string? callerFile = null)
                => _Inner.ReclaimAsync(dockId, tenantId, token, caller, callerFile);

            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.RepairAsync(dockId, tenantId, token);

            public Task UnstickAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.UnstickAsync(dockId, tenantId, token);

            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.DeleteAsync(dockId, tenantId, token);

            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        private sealed class ThrowOnceThenSucceedDockService : IDockService
        {
            private readonly IDockService _Inner;
            private int _CallCount = 0;

            public ThrowOnceThenSucceedDockService(IDockService inner)
            {
                _Inner = inner;
            }

            public Task<string?> PrepareBranchFromRefAsync(Vessel vessel, string branchName, string startFromRef, CancellationToken token = default) => Task.FromResult<string?>(null);

            public async Task<Dock?> ProvisionAsync(Vessel vessel, Captain captain, string branchName, string? missionId = null, bool detachedWorktree = false, CancellationToken token = default)
            {
                int call = Interlocked.Increment(ref _CallCount);
                if (call == 1)
                    throw new InvalidOperationException("Simulated dock provisioning failure (first attempt)");

                return await _Inner.ProvisionAsync(vessel, captain, branchName, missionId, detachedWorktree, token).ConfigureAwait(false);
            }

            public Task ReclaimAsync(string dockId, string? tenantId = null, CancellationToken token = default, string? caller = null, string? callerFile = null)
                => _Inner.ReclaimAsync(dockId, tenantId, token, caller, callerFile);

            public Task RepairAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.RepairAsync(dockId, tenantId, token);

            public Task UnstickAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.UnstickAsync(dockId, tenantId, token);

            public Task<bool> DeleteAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.DeleteAsync(dockId, tenantId, token);

            public Task PurgeAsync(string dockId, string? tenantId = null, CancellationToken token = default)
                => _Inner.PurgeAsync(dockId, tenantId, token);
        }

        #endregion
    }
}
