namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the eligibility and targeting of Checks armed at dispatch.
    /// </summary>
    /// <remarks>
    /// A voyage-armed Check exists to gate the voyage: a Judge PASS is rejected without a green
    /// independent Check. Waiting for the voyage to COMPLETE before running it is therefore a
    /// condition the record itself prevents, so these tests pin the earlier trigger -- work
    /// committed to a branch -- and pin that the record is pointed at that branch before it runs.
    /// </remarks>
    public class ArmedCheckEligibilityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Armed Check Eligibility";

        private static AutomaticCheckRunOrchestrator BuildOrchestrator(TestDatabase testDb, DispatchHold? dispatchHold = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
            CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
            ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging);
            IncidentService incidents = new IncidentService(testDb.Driver);

            return new AutomaticCheckRunOrchestrator(testDb.Driver, checkRuns, releases, incidents, logging, dispatchHold);
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("armed-check-vessel", "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<CheckRun> ArmCheckAsync(TestDatabase testDb, Vessel vessel, Voyage voyage)
        {
            CheckRun run = new CheckRun
            {
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Pending,
                Label = "Build (armed at dispatch)"
            };

            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateWorkMissionAsync(
            TestDatabase testDb, Vessel vessel, Voyage voyage, MissionStatusEnum status, string? branch, string? commit)
        {
            Mission mission = new Mission("[Worker] Do the work", "Do the work");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = "Worker";
            mission.Status = status;
            mission.BranchName = branch;
            mission.CommitHash = commit;
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>Run all armed-check eligibility tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A pending armed check of an already cancelled voyage is discarded, not left Pending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = new Voyage("ended-voyage");
                    voyage.Status = VoyageStatusEnum.Cancelled;
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    CheckRun? after = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                    AssertFalse(eligible, "a check of an ended voyage never runs");
                    AssertEqual(CheckRunStatusEnum.Canceled, after!.Status, "a check of an ended voyage must not stay Pending and count as required");
                    AssertContains(VoyageCheckDiscard.VoyageCancelledReason, after.Summary ?? String.Empty, "the discard names its reason");
                }
            }).ConfigureAwait(false);

            await RunTest("Armed check is eligible once a stage commits work to a branch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertTrue(eligible, "Work committed to a branch must make the armed check runnable before the voyage completes");
                }
            });

            await RunTest("Armed check is not eligible while no stage has produced work", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.InProgress, null, null).ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertFalse(eligible, "A check with nothing committed to measure must not run");
                }
            });

            await RunTest("Armed check on a cancelled voyage never becomes eligible", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = new Voyage("armed-voyage");
                    voyage.Status = VoyageStatusEnum.Cancelled;
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    bool eligible = await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false);

                    AssertFalse(eligible, "A cancelled voyage has no gate left to feed");
                }
            });

            await RunTest("Armed check is pointed at the work branch before it runs", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    CheckRun stamped = await orchestrator.StampWorkUnderReviewAsync(armed, default).ConfigureAwait(false);

                    AssertEqual("armada/worker/msn-1", stamped.BranchName, "An unstamped check would measure the default branch, not the work");
                    AssertEqual("abc123", stamped.CommitHash, "The commit under review must be recorded on the check");
                }
            });

            await RunTest("Sweep stamps the work branch onto an armed check before executing it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The vessel has no working directory, so execution fails immediately without
                    // running a command. Stamping happens BEFORE execution, so the persisted record
                    // still proves the sweep pointed the check at the work. Asserting the stamping
                    // METHOD alone would pass even if nothing ever called it.
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    int executed = await orchestrator.RunSweepAsync(default).ConfigureAwait(false);

                    AssertEqual(1, executed, "The armed check should have been picked up by the sweep");

                    CheckRun? reloaded = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                    AssertNotNull(reloaded, "The armed check should remain readable");
                    AssertEqual("armada/worker/msn-1", reloaded!.BranchName, "The sweep must point the check at the work before running it");
                }
            });

            await RunTest("Sweep finishes eligible live-voyage checks under the dispatch hold and defers unrelated checks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel voyageVessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("held-voyage")).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, voyageVessel, voyage).ConfigureAwait(false);
                        Mission producedWork = await CreateWorkMissionAsync(
                            testDb, voyageVessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-held", "held123").ConfigureAwait(false);

                        Voyage finishingVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("finishing-held-voyage")).ConfigureAwait(false);
                        CheckRun finishingCheck = await ArmRunnableCheckAsync(testDb, voyageVessel, finishingVoyage).ConfigureAwait(false);
                        Mission finishingWork = await CreateWorkMissionAsync(
                            testDb, voyageVessel, finishingVoyage, MissionStatusEnum.InProgress, null, null).ConfigureAwait(false);

                        Vessel idleVessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory, "armed-check-idle-vessel").ConfigureAwait(false);
                        CheckRun idleCheck = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            VesselId = idleVessel.Id,
                            Type = CheckRunTypeEnum.Build,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Pending,
                            Command = "echo " + _DefaultBranchMarker,
                            Label = "Build (idle-vessel check)"
                        }).ConfigureAwait(false);

                        DispatchHold hold = new DispatchHold();
                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb, hold);
                        AssertTrue(await orchestrator.IsEligibleAsync(idleCheck, default).ConfigureAwait(false),
                            "The idle-vessel check is otherwise eligible, so its deferral is due to the hold");
                        hold.Engage("Managed-vessel execution is paused.", "operator-session");

                        // A mission already running when the hold is engaged may finish and produce its
                        // branch during the hold. Its pre-armed voyage check must then be allowed to drain.
                        finishingWork.Status = MissionStatusEnum.WorkProduced;
                        finishingWork.BranchName = "armada/worker/msn-finishing-held";
                        finishingWork.CommitHash = "finishing123";
                        await testDb.Driver.Missions.UpdateAsync(finishingWork).ConfigureAwait(false);

                        AssertEqual(2, await orchestrator.RunSweepAsync(default).ConfigureAwait(false),
                            "Checks for committed work on existing voyages must run so those voyages can drain");

                        CheckRun? checkedVoyage = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                        AssertNotNull(checkedVoyage, "The voyage check should remain readable");
                        AssertEqual(CheckRunStatusEnum.Passed, checkedVoyage!.Status, "The eligible voyage check must finish under the hold");
                        AssertEqual(producedWork.BranchName, checkedVoyage.BranchName, "The check still measures the committed voyage work");
                        AssertEqual("held123", checkedVoyage.CommitHash, "The check remains pinned to that work's commit");

                        CheckRun? checkedFinishingVoyage = await testDb.Driver.CheckRuns.ReadAsync(finishingCheck.Id).ConfigureAwait(false);
                        AssertNotNull(checkedFinishingVoyage, "The check for work produced during the hold should remain readable");
                        AssertEqual(CheckRunStatusEnum.Passed, checkedFinishingVoyage!.Status, "The check for work produced during the hold must run");
                        AssertEqual(finishingWork.BranchName, checkedFinishingVoyage.BranchName, "The check is stamped at the new work branch");
                        AssertEqual("finishing123", checkedFinishingVoyage.CommitHash, "The check is stamped at the new work commit");

                        CheckRun? deferredIdle = await testDb.Driver.CheckRuns.ReadAsync(idleCheck.Id).ConfigureAwait(false);
                        AssertNotNull(deferredIdle, "The unrelated idle-vessel check should remain readable");
                        AssertEqual(CheckRunStatusEnum.Pending, deferredIdle!.Status, "The dispatch hold still defers unrelated vessel checks");
                        AssertNull(deferredIdle.StartedUtc, "An unrelated check must not start under the hold");

                        AssertEqual(0, await orchestrator.RunSweepAsync(default).ConfigureAwait(false), "A second sweep must not repeat a completed voyage check");

                        List<ArmadaEvent> deferred = await testDb.Driver.Events.EnumerateByTypeAsync(
                            AutomaticCheckRunOrchestrator.DeferredByDispatchHoldEvent, 50).ConfigureAwait(false);
                        AssertEqual(1, deferred.Count, "One event per hold engagement names why unrelated checks wait");
                        AssertContains("operator-session", deferred[0].Message ?? String.Empty, "The event names who holds dispatch");

                        hold.Clear();
                        AssertEqual(1, await orchestrator.RunSweepAsync(default).ConfigureAwait(false), "The unrelated check runs once the hold clears");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("A check armed while an earlier check waits for the host slot joins the slot queue without waiting for it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    IDisposable? hostSlot = null;
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = new Voyage("landed-voyage");
                        voyage.Status = VoyageStatusEnum.Complete;
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        CheckRun first = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                        // Another caller (a definition-of-done gate or a long suite) holds the host slot,
                        // so the first check waits in it for as long as the test decides.
                        hostSlot = await HostWideCommandLock.AcquireAsync().ConfigureAwait(false);
                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        orchestrator.TriggerBackgroundSweep();
                        bool firstQueued = await WaitUntilAsync(async () =>
                            (await testDb.Driver.CheckRuns.ReadAsync(first.Id).ConfigureAwait(false))!.SlotRequestedUtc.HasValue).ConfigureAwait(false);
                        AssertTrue(firstQueued, "the first check must reach the host slot queue");

                        // Work moves on: a second check is armed while the first still waits.
                        CheckRun second = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                        orchestrator.TriggerBackgroundSweep();
                        bool secondQueued = await WaitUntilAsync(async () =>
                            (await testDb.Driver.CheckRuns.ReadAsync(second.Id).ConfigureAwait(false))!.SlotRequestedUtc.HasValue).ConfigureAwait(false);

                        CheckRun? firstWhileHeld = await testDb.Driver.CheckRuns.ReadAsync(first.Id).ConfigureAwait(false);
                        AssertTrue(secondQueued, "a newly armed check must be started by the next sweep, not wait until the earlier check finishes");
                        AssertEqual(CheckRunStatusEnum.Pending, firstWhileHeld!.Status, "the host slot still serializes commands: nothing runs while another caller holds it");

                        hostSlot.Dispose();
                        hostSlot = null;
                        bool bothDone = await WaitUntilAsync(async () =>
                        {
                            CheckRun? a = await testDb.Driver.CheckRuns.ReadAsync(first.Id).ConfigureAwait(false);
                            CheckRun? b = await testDb.Driver.CheckRuns.ReadAsync(second.Id).ConfigureAwait(false);
                            return a!.Status == CheckRunStatusEnum.Passed && b!.Status == CheckRunStatusEnum.Passed;
                        }).ConfigureAwait(false);
                        AssertTrue(bothDone, "both checks run once the host slot is free");
                    }
                    finally
                    {
                        hostSlot?.Dispose();
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("Sweep finds an eligible check behind two hundred ineligible checks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("starved-armed-voyage")).ConfigureAwait(false);
                    CheckRun armed = new CheckRun
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Type = CheckRunTypeEnum.Build,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Pending,
                        Label = "Old eligible check",
                        CreatedUtc = DateTime.UtcNow.AddHours(-4)
                    };
                    armed = await testDb.Driver.CheckRuns.CreateAsync(armed).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-old", "old123").ConfigureAwait(false);

                    DateTime newerBaseUtc = DateTime.UtcNow.AddHours(-3);
                    for (int index = 0; index < 200; index++)
                    {
                        CheckRun ineligible = new CheckRun
                        {
                            VesselId = vessel.Id,
                            DeploymentId = "dpl_not-ready-" + index,
                            Type = CheckRunTypeEnum.Build,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Pending,
                            Label = "Newer ineligible check " + index,
                            CreatedUtc = newerBaseUtc.AddMilliseconds(index)
                        };
                        await testDb.Driver.CheckRuns.CreateAsync(ineligible).ConfigureAwait(false);
                    }

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    int executed = await orchestrator.RunSweepAsync(default).ConfigureAwait(false);

                    AssertEqual(1, executed, "An older eligible check must not be hidden by a full page of newer ineligible checks");
                    CheckRun? reloaded = await testDb.Driver.CheckRuns.ReadAsync(armed.Id).ConfigureAwait(false);
                    AssertNotNull(reloaded, "The older eligible check should remain readable");
                    AssertEqual("armada/worker/msn-old", reloaded!.BranchName, "The sweep must execute and stamp the older eligible check");
                }
            });

            await RunTest("Armed check on a completed voyage keeps measuring the default branch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = new Voyage("armed-voyage");
                    voyage.Status = VoyageStatusEnum.Complete;
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    CheckRun armed = await ArmCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                    await CreateWorkMissionAsync(
                        testDb, vessel, voyage, MissionStatusEnum.Complete, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                    AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                    CheckRun stamped = await orchestrator.StampWorkUnderReviewAsync(armed, default).ConfigureAwait(false);

                    AssertNull(stamped.BranchName, "Work on a completed voyage is on the default branch, which is the correct subject");
                }
            });

            await RunTest("An armed check whose voyage fails before stamping is cancelled, not run against the default branch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("window-voyage")).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                        Mission work = await CreateWorkMissionAsync(
                            testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false), "the record is eligible while the voyage has work");

                        // The voyage fails between the eligibility read and the stamping read.
                        voyage.Status = VoyageStatusEnum.Failed;
                        await testDb.Driver.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
                        work.Status = MissionStatusEnum.Failed;
                        await testDb.Driver.Missions.UpdateAsync(work).ConfigureAwait(false);

                        CheckRun result = await orchestrator.ExecutePendingAsync(BuildSystemAuth(), armed, default).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Canceled, result.Status, "an unstamped record of an ended voyage must not execute");
                        AssertFalse((result.Output ?? String.Empty).Contains(_DefaultBranchMarker), "the default branch must not be measured");
                        AssertContains("no branch or commit", result.Summary ?? String.Empty, "the cancel names the missing stamp");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("An unstamped armed check of a live voyage whose work disappeared is left Pending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("live-voyage")).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);
                        Mission work = await CreateWorkMissionAsync(
                            testDb, vessel, voyage, MissionStatusEnum.WorkProduced, "armada/worker/msn-1", "abc123").ConfigureAwait(false);

                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        AssertTrue(await orchestrator.IsEligibleAsync(armed, default).ConfigureAwait(false), "the record is eligible while the voyage has work");

                        work.Status = MissionStatusEnum.Failed;
                        await testDb.Driver.Missions.UpdateAsync(work).ConfigureAwait(false);

                        CheckRun result = await orchestrator.ExecutePendingAsync(BuildSystemAuth(), armed, default).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Pending, result.Status, "a live voyage's unstamped record waits for a stage to commit");
                        AssertNull(result.StartedUtc, "the waiting record must not be started");
                        AssertFalse((result.Output ?? String.Empty).Contains(_DefaultBranchMarker), "the default branch must not be measured");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });

            await RunTest("An unstamped armed check of a completed voyage still executes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = CreateWorkingDirectory();
                    try
                    {
                        Vessel vessel = await CreateLiveDirectoryVesselAsync(testDb, workingDirectory).ConfigureAwait(false);
                        Voyage voyage = new Voyage("complete-voyage");
                        voyage.Status = VoyageStatusEnum.Complete;
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        CheckRun armed = await ArmRunnableCheckAsync(testDb, vessel, voyage).ConfigureAwait(false);

                        AutomaticCheckRunOrchestrator orchestrator = BuildOrchestrator(testDb);
                        CheckRun result = await orchestrator.ExecutePendingAsync(BuildSystemAuth(), armed, default).ConfigureAwait(false);

                        AssertEqual(CheckRunStatusEnum.Passed, result.Status, "a completed voyage's work is on the default branch");
                        AssertContains(_DefaultBranchMarker, result.Output ?? String.Empty, "the command must have run");
                    }
                    finally
                    {
                        DeleteDirectory(workingDirectory);
                    }
                }
            });
        }

        private const string _DefaultBranchMarker = "measured-live-directory";

        /// <summary>
        /// Poll a condition until it holds or the deadline passes. The deadline only bounds a failing
        /// run; a passing run returns as soon as the condition holds.
        /// </summary>
        private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition().ConfigureAwait(false)) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }

            return await condition().ConfigureAwait(false);
        }

        private static AuthContext BuildSystemAuth()
        {
            return AuthContext.Authenticated(
                Armada.Core.Constants.DefaultTenantId,
                Armada.Core.Constants.DefaultUserId,
                isAdmin: true,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Automated checks");
        }

        private static string CreateWorkingDirectory()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada-iso-armed-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  could not delete test directory " + path + ": " + ex.Message);
            }
        }

        private static async Task<Vessel> CreateLiveDirectoryVesselAsync(TestDatabase testDb, string workingDirectory, string name = "armed-check-live-vessel")
        {
            Vessel vessel = new Vessel
            {
                Name = name,
                RepoUrl = String.Empty,
                LocalPath = String.Empty,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<CheckRun> ArmRunnableCheckAsync(TestDatabase testDb, Vessel vessel, Voyage voyage)
        {
            CheckRun run = new CheckRun
            {
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Pending,
                Command = "echo " + _DefaultBranchMarker,
                Label = "Build (armed at dispatch)"
            };

            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }
    }
}
