namespace Armada.Test.Unit.Suites.Services
{
    using System;
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

        private static AutomaticCheckRunOrchestrator BuildOrchestrator(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
            CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
            ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging);
            IncidentService incidents = new IncidentService(testDb.Driver);

            return new AutomaticCheckRunOrchestrator(testDb.Driver, checkRuns, releases, incidents, logging);
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
        }
    }
}
