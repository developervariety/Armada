namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the scheduler high-tier capacity reservation: specialist (downstream)
    /// missions dispatch before Worker missions, and a configurable number of idle
    /// high-tier captains are held back so Judge / TestEngineer stages are not starved
    /// by Worker dispatch. Coverage is deliberately failure-mode oriented:
    ///  - the reserve engages only when the remaining idle pool is high-tier-only and
    ///    in-flight work could soon produce a downstream specialist,
    ///  - non-high-tier capacity is never withheld,
    ///  - a high-tier-only fleet does not deadlock when nothing is in flight to produce
    ///    a downstream specialist stage (the cold-start anti-starvation guard),
    ///  - the reservation can be disabled (ReservedHighTierSlots = 0).
    /// </summary>
    public class SchedulerCapacityTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "Scheduler Capacity";

        #endregion

        #region Public-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ReservedHighTierSlots_DefaultValue_IsOne", () =>
            {
                ModelTierSettings settings = new ModelTierSettings();
                AssertEqual(1, settings.ReservedHighTierSlots, "Default ReservedHighTierSlots should be 1");
                return Task.CompletedTask;
            });

            await RunTest("ReservedHighTierSlots_ClampedToRange", () =>
            {
                ModelTierSettings settings = new ModelTierSettings();
                settings.ReservedHighTierSlots = -5;
                AssertEqual(0, settings.ReservedHighTierSlots, "Negative value should clamp to 0");
                settings.ReservedHighTierSlots = 0;
                AssertEqual(0, settings.ReservedHighTierSlots, "Zero should be accepted (disables reservation)");
                settings.ReservedHighTierSlots = 7;
                AssertEqual(7, settings.ReservedHighTierSlots, "In-range value should be preserved");
                settings.ReservedHighTierSlots = 999;
                AssertEqual(10, settings.ReservedHighTierSlots, "Value above 10 should clamp to 10");
                return Task.CompletedTask;
            });

            // Anti-deadlock guard: a high-tier-only fleet with nothing in flight must not
            // hold its last idle captain hostage for a specialist that will never arrive.
            // Without the in-flight-demand guard the default reservation (1) would defer
            // the sole Worker every cycle and the captain would sit idle forever.
            await RunTest("ColdStart_HighTierOnlyFleet_WorkerDispatches_NoDeadlock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = BuildHarness(testDb);

                    Vessel vessel = await CreateConcurrentVesselAsync(testDb, "coldstart-vessel");
                    await CreateIdleCaptainAsync(testDb, "coldstart-opus", "claude-opus-4-8");

                    // Only a Worker mission is pending; nothing is in flight anywhere.
                    Mission worker = await CreatePendingMissionAsync(testDb, vessel, persona: null, title: "Cold-start worker");

                    await h.Admiral.HealthCheckAsync();
                    await Task.Delay(200);

                    Mission? updated = await testDb.Driver.Missions.ReadAsync(worker.Id);
                    AssertNotNull(updated, "Worker mission should still exist");
                    AssertTrue(IsDispatched(updated!.Status),
                        "Cold-start Worker must claim the idle high-tier captain instead of deadlocking (got: " + updated.Status + ")");
                }
            });

            // Reserve engages: after a specialist claims a high-tier captain, the remaining
            // idle pool is high-tier-only and within the reserve, and the specialist is now
            // in flight (real demand). The Worker must be deferred and the reserved captain
            // must stay idle for the next incoming specialist stage.
            await RunTest("ReserveEngages_ConstrainedHighTierWithInFlightDemand_DefersWorker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = BuildHarness(testDb);
                    // Default reserve = 1.

                    Vessel vessel = await CreateConcurrentVesselAsync(testDb, "reserve-vessel");
                    await CreateIdleCaptainAsync(testDb, "reserve-opus-a", "claude-opus-4-8");
                    await CreateIdleCaptainAsync(testDb, "reserve-opus-b", "claude-opus-4-8");

                    Mission judge = await CreatePendingMissionAsync(testDb, vessel, persona: "Judge", title: "Judge review");
                    Mission worker = await CreatePendingMissionAsync(testDb, vessel, persona: null, title: "Worker");

                    await h.Admiral.HealthCheckAsync();
                    await Task.Delay(200);

                    Mission? judgeAfter = await testDb.Driver.Missions.ReadAsync(judge.Id);
                    AssertTrue(IsDispatched(judgeAfter!.Status),
                        "Specialist (Judge) must dispatch first, claiming a high-tier captain (got: " + judgeAfter.Status + ")");

                    Mission? workerAfter = await testDb.Driver.Missions.ReadAsync(worker.Id);
                    AssertEqual(MissionStatusEnum.Pending, workerAfter!.Status,
                        "Worker must be deferred (left Pending) while the only idle captain is the reserved high-tier one");

                    List<Captain> idle = await testDb.Driver.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle);
                    AssertEqual(1, idle.Count, "One high-tier captain must stay idle in reserve for the next specialist");
                    AssertTrue(IsHighTier(idle[0].Model),
                        "The reserved idle captain must be high-tier (got: " + idle[0].Model + ")");
                }
            });

            // ReservedHighTierSlots = 0 disables the reservation entirely: the Worker
            // claims the remaining high-tier captain even though a specialist is in flight.
            await RunTest("ZeroReservation_ConstrainedHighTier_WorkerDispatches", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = BuildHarness(testDb);
                    h.Settings.ModelTier.ReservedHighTierSlots = 0;

                    Vessel vessel = await CreateConcurrentVesselAsync(testDb, "zero-reserve-vessel");
                    await CreateIdleCaptainAsync(testDb, "zero-opus-a", "claude-opus-4-8");
                    await CreateIdleCaptainAsync(testDb, "zero-opus-b", "claude-opus-4-8");

                    Mission judge = await CreatePendingMissionAsync(testDb, vessel, persona: "Judge", title: "Judge review");
                    Mission worker = await CreatePendingMissionAsync(testDb, vessel, persona: null, title: "Worker");

                    await h.Admiral.HealthCheckAsync();
                    await Task.Delay(200);

                    Mission? workerAfter = await testDb.Driver.Missions.ReadAsync(worker.Id);
                    AssertTrue(IsDispatched(workerAfter!.Status),
                        "With ReservedHighTierSlots=0 the Worker must claim the second high-tier captain (got: " + workerAfter.Status + ")");

                    List<Captain> idle = await testDb.Driver.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle);
                    AssertEqual(0, idle.Count, "With the reservation disabled no high-tier captain may be held back");
                }
            });

            // Non-high-tier capacity is never withheld by the reservation: while idle
            // mid-tier capacity exists the gate does not engage, so the Worker dispatches
            // onto the mid-tier captain and the high-tier captain is left free.
            await RunTest("NonHighTierIdle_WorkerNotDeferred_UsesMidTierLeavesHighIdle", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = BuildHarness(testDb);
                    // Default reserve = 1.

                    Vessel vessel = await CreateConcurrentVesselAsync(testDb, "mixed-fleet-vessel");
                    await CreateIdleCaptainAsync(testDb, "mixed-opus", "claude-opus-4-8");
                    await CreateIdleCaptainAsync(testDb, "mixed-sonnet", "claude-sonnet-4-6");

                    Mission worker = await CreatePendingMissionAsync(testDb, vessel, persona: null, title: "Worker");

                    await h.Admiral.HealthCheckAsync();
                    await Task.Delay(200);

                    Mission? workerAfter = await testDb.Driver.Missions.ReadAsync(worker.Id);
                    AssertTrue(IsDispatched(workerAfter!.Status),
                        "Worker must dispatch onto idle mid-tier capacity, not be deferred by the high-tier reservation (got: " + workerAfter.Status + ")");

                    List<Captain> idle = await testDb.Driver.Captains.EnumerateByStateAsync(CaptainStateEnum.Idle);
                    AssertEqual(1, idle.Count, "The high-tier captain must remain idle (Worker preferred mid-tier)");
                    AssertTrue(IsHighTier(idle[0].Model),
                        "The remaining idle captain must be the high-tier one (got: " + idle[0].Model + ")");
                }
            });
        }

        #endregion

        #region Private-Methods

        private Harness BuildHarness(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_capacity_docks_" + System.Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_capacity_repos_" + System.Guid.NewGuid().ToString("N"));

            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
            IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
            IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
            IAdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
            admiral.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);

            return new Harness(admiral, settings);
        }

        private static async Task<Vessel> CreateConcurrentVesselAsync(TestDatabase testDb, string name)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            vessel.AllowConcurrentMissions = true;
            return await testDb.Driver.Vessels.CreateAsync(vessel);
        }

        private static async Task CreateIdleCaptainAsync(TestDatabase testDb, string name, string model)
        {
            Captain captain = new Captain(name);
            captain.State = CaptainStateEnum.Idle;
            captain.Model = model;
            await testDb.Driver.Captains.CreateAsync(captain);
        }

        private static async Task<Mission> CreatePendingMissionAsync(TestDatabase testDb, Vessel vessel, string? persona, string title)
        {
            Mission mission = new Mission(title, title);
            mission.VesselId = vessel.Id;
            mission.Persona = persona;
            mission.Status = MissionStatusEnum.Pending;
            mission.AssignmentState = MissionAssignmentStateEnum.Pending;
            return await testDb.Driver.Missions.CreateAsync(mission);
        }

        private static bool IsDispatched(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Assigned || status == MissionStatusEnum.InProgress;
        }

        private static bool IsHighTier(string? model)
        {
            return string.Equals(
                PreferredModelTierSelector.ClassifyModel(model),
                PreferredModelTierSelector.HighTier,
                System.StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Private-Types

        private sealed class Harness
        {
            public IAdmiralService Admiral { get; }

            public ArmadaSettings Settings { get; }

            public Harness(IAdmiralService admiral, ArmadaSettings settings)
            {
                Admiral = admiral;
                Settings = settings;
            }
        }

        #endregion
    }
}
