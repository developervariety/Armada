namespace Armada.Test.Unit.Suites.Services
{
    using System.Globalization;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

    public class AdmiralServiceTests : TestSuite
    {
        public override string Name => "Admiral Service";

        private static async Task SetMissionLastUpdateUtcAsync(TestDatabase testDb, string missionId, DateTime lastUpdateUtc)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE missions SET last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", missionId);
                    cmd.Parameters.AddWithValue("@last_update_utc", lastUpdateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static bool GetRetryDispatchNeeded(AdmiralService service)
        {
            System.Reflection.FieldInfo? field = typeof(AdmiralService).GetField(
                "_RetryDispatchNeeded",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Could not find _RetryDispatchNeeded field.");
            return (bool)field!.GetValue(service)!;
        }

        private static void SetRetryDispatchNeeded(AdmiralService service, bool value)
        {
            System.Reflection.FieldInfo? field = typeof(AdmiralService).GetField(
                "_RetryDispatchNeeded",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Could not find _RetryDispatchNeeded field.");
            field!.SetValue(service, value);
        }

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

        private AdmiralService CreateAdmiralService(LoggingModule logging, SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git)
        {
            IDockService dockService = new DockService(logging, db, settings, git);
            ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService,
                git: git);
        }

        private static async Task<Mission> CreateExitMissionAsync(SqliteDatabaseDriver db, Captain captain, int processId)
        {
            Voyage voyage = new Voyage("Crash exclusion voyage " + processId) { Status = VoyageStatusEnum.InProgress };
            await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);
            Mission mission = new Mission("Crash exclusion mission " + processId)
            {
                VoyageId = voyage.Id,
                Status = MissionStatusEnum.InProgress,
                AssignmentState = MissionAssignmentStateEnum.Assigned,
                ProcessId = processId,
                StartedUtc = DateTime.UtcNow.AddSeconds(-5)
            };
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);
            captain.CurrentMissionId = mission.Id;
            captain.ProcessId = processId;
            captain.State = CaptainStateEnum.Working;
            await db.Captains.UpdateAsync(captain).ConfigureAwait(false);
            return mission;
        }

        private sealed class CrashLoopExcludedFailureCase
        {
            public CrashLoopExcludedFailureCase(string name, string logText, bool benchesCaptain)
            {
                Name = name;
                LogText = logText;
                BenchesCaptain = benchesCaptain;
            }

            public string Name { get; }

            public string LogText { get; }

            // Quota and credential failures bench the captain. A provider safeguard block does not: it follows
            // the refusal rule, which excludes the blocking runtime for that mission only.
            public bool BenchesCaptain { get; }
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("HandleProcessExitAsync records the process exit event in the mission owner's scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), new StubGitService());
                    string tenantId = Armada.Core.Constants.DefaultTenantId;
                    string userId = Armada.Core.Constants.DefaultUserId;

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("exit-scope-vessel", "https://github.com/test/exit-scope.git")).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("exit-scope-captain")).ConfigureAwait(false);
                    Mission mission = new Mission("exit scope mission");
                    mission.TenantId = tenantId;
                    mission.UserId = userId;
                    mission.VesselId = vessel.Id;
                    mission.CaptainId = captain.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    captain.CurrentMissionId = mission.Id;
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    await service.HandleProcessExitAsync(4242, 0, captain.Id, mission.Id).ConfigureAwait(false);

                    EnumerationQuery query = new EnumerationQuery { MissionId = mission.Id, EventType = "captain.process_exited", PageNumber = 1, PageSize = 10 };
                    EnumerationResult<ArmadaEvent> owner = await testDb.Driver.Events.EnumerateAsync(tenantId, userId, query).ConfigureAwait(false);
                    AssertEqual(1, owner.Objects.Count, "The mission owner's scoped read must find the process exit event");
                    EnumerationResult<ArmadaEvent> other = await testDb.Driver.Events.EnumerateAsync(tenantId, "usr_other_owner", query).ConfigureAwait(false);
                    AssertEqual(0, other.Objects.Count, "Another user's scoped read must not find the process exit event");
                }
            });

            await RunTest("Constructor NullLogging Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                    new AdmiralService(null!, null!, null!, null!, null!, null!, null!));
            });

            await RunTest("Constructor NullDatabase Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                    new AdmiralService(CreateLogging(), null!, null!, null!, null!, null!, null!));
            });

            await RunTest("ArmadaSettings MaxInterruptedExitRedispatchAttempts ClampsToRangeAndDefaultsToTwo", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertEqual(2, settings.MaxInterruptedExitRedispatchAttempts, "the default budget is two re-dispatches");

                settings.MaxInterruptedExitRedispatchAttempts = -3;
                AssertEqual(0, settings.MaxInterruptedExitRedispatchAttempts, "the budget clamps to the minimum");

                settings.MaxInterruptedExitRedispatchAttempts = 99;
                AssertEqual(10, settings.MaxInterruptedExitRedispatchAttempts, "the budget clamps to the maximum");

                settings.MaxInterruptedExitRedispatchAttempts = 4;
                AssertEqual(4, settings.MaxInterruptedExitRedispatchAttempts, "an in-range budget is kept");
            });

            await RunTest("ArmadaSettings LaunchProcessIdGraceSeconds ClampsToRange", () =>
            {
                ArmadaSettings settings = CreateSettings();

                settings.LaunchProcessIdGraceSeconds = 0;
                AssertEqual(5, settings.LaunchProcessIdGraceSeconds, "Launch PID grace should clamp to the minimum");

                settings.LaunchProcessIdGraceSeconds = 999;
                AssertEqual(300, settings.LaunchProcessIdGraceSeconds, "Launch PID grace should clamp to the maximum");

                settings.LaunchProcessIdGraceSeconds = 45;
                AssertEqual(45, settings.LaunchProcessIdGraceSeconds, "Launch PID grace should preserve in-range values");
            });

            await RunTest("SpendCapBench BenchesOnlyTheCappedModel", async () =>
            {
                // A provider spend cap is scoped to the model, not the provider account: an expensive frontier
                // model can be capped while the cheap models on the SAME provider still answer. Benching the whole
                // "example-provider/" prefix took working captains offline, so only the capped model may be benched here.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Captain capped = new Captain("capped-1");
                    capped.Model = "example-provider/gpt-5.6-luna";
                    Captain sameModel = new Captain("capped-2");
                    sameModel.Model = "example-provider/gpt-5.6-luna";
                    Captain sameProviderOtherModel = new Captain("cheap-1");
                    sameProviderOtherModel.Model = "example-provider/gpt-5.6-sol";
                    Captain otherProvider = new Captain("native-1");
                    otherProvider.Model = "claude-opus-5";

                    await db.Captains.CreateAsync(capped);
                    await db.Captains.CreateAsync(sameModel);
                    await db.Captains.CreateAsync(sameProviderOtherModel);
                    await db.Captains.CreateAsync(otherProvider);

                    System.Reflection.MethodInfo? bench = typeof(AdmiralService).GetMethod(
                        "BenchProviderGroupAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (bench == null) throw new InvalidOperationException("Could not find BenchProviderGroupAsync.");

                    DateTime until = DateTime.UtcNow.AddHours(25);
                    await (Task)bench.Invoke(service, new object[]
                    {
                        capped, "Provider daily spend cap reached.", until, CancellationToken.None
                    })!;

                    Captain? benched1 = await db.Captains.ReadAsync(capped.Id);
                    Captain? benched2 = await db.Captains.ReadAsync(sameModel.Id);
                    Captain? cheap = await db.Captains.ReadAsync(sameProviderOtherModel.Id);
                    Captain? native = await db.Captains.ReadAsync(otherProvider.Id);

                    AssertTrue(benched1!.QuarantineUntilUtc.HasValue,
                        "The captain that returned the cap must be benched");
                    AssertTrue(benched2!.QuarantineUntilUtc.HasValue,
                        "A second captain on the SAME capped model must be benched in the same pass");
                    AssertFalse(cheap!.QuarantineUntilUtc.HasValue,
                        "A cheaper model on the same provider still answers and must NOT be benched");
                    AssertFalse(native!.QuarantineUntilUtc.HasValue,
                        "A captain on another provider must never be benched by this path");
                }
            });

            await RunTest("SpendCapBench KeepsWorkingSiblingOwnership", async () =>
            {
                // The group bench runs while other captains on the capped model may still be executing missions. Their
                // processes keep running, so the bench must not clear their mission, dock or process; they bench
                // themselves when their own run returns the cap. Idle siblings are held immediately.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Vessel vessel = new Vessel("spend-cap-vessel", "https://github.com/test/spend.git");
                    await db.Vessels.CreateAsync(vessel);
                    Mission running = new Mission("running on the capped model", "still working");
                    running.VesselId = vessel.Id;
                    running.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(running);

                    Captain capped = new Captain("capped-failing");
                    capped.Model = "example-provider/gpt-5.6-luna";
                    Captain idleSibling = new Captain("capped-idle");
                    idleSibling.Model = "example-provider/gpt-5.6-luna";
                    Captain workingSibling = new Captain("capped-working");
                    workingSibling.Model = "example-provider/gpt-5.6-luna";
                    await db.Captains.CreateAsync(capped);
                    await db.Captains.CreateAsync(idleSibling);
                    await db.Captains.CreateAsync(workingSibling);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = workingSibling.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_spend_cap_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/spend-cap/" + running.Id;
                    await db.Docks.CreateAsync(dock);
                    workingSibling.State = CaptainStateEnum.Working;
                    workingSibling.CurrentMissionId = running.Id;
                    workingSibling.CurrentDockId = dock.Id;
                    workingSibling.ProcessId = 9191;
                    await db.Captains.UpdateAsync(workingSibling);

                    System.Reflection.MethodInfo? bench = typeof(AdmiralService).GetMethod(
                        "BenchProviderGroupAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (bench == null) throw new InvalidOperationException("Could not find BenchProviderGroupAsync.");

                    await (Task)bench.Invoke(service, new object[]
                    {
                        capped, "Provider daily spend cap reached.", DateTime.UtcNow.AddHours(25), CancellationToken.None
                    })!;

                    Captain? failing = await db.Captains.ReadAsync(capped.Id);
                    Captain? idle = await db.Captains.ReadAsync(idleSibling.Id);
                    Captain? working = await db.Captains.ReadAsync(workingSibling.Id);

                    AssertEqual(CaptainStateEnum.Quarantined, failing!.State, "The captain that returned the cap is benched");
                    AssertEqual(CaptainStateEnum.Quarantined, idle!.State, "An idle sibling on the capped model is benched");
                    AssertEqual(CaptainStateEnum.Working, working!.State, "A working sibling keeps running");
                    AssertEqual(running.Id, working.CurrentMissionId, "A working sibling keeps its mission");
                    AssertEqual(dock.Id, working.CurrentDockId, "A working sibling keeps its dock");
                    AssertEqual(9191, working.ProcessId, "A working sibling keeps its process");
                }
            });

            await RunTest("GetStatusAsync EmptyDatabase ReturnsDefaults", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(0, status.TotalCaptains);
                    AssertEqual(0, status.IdleCaptains);
                    AssertEqual(0, status.WorkingCaptains);
                    AssertEqual(0, status.ActiveVoyages);
                    AssertEqual(0, status.Voyages.Count);
                }
            });

            await RunTest("GetStatusAsync WithCaptains CountsCorrectly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Captain c1 = new Captain("idle-1");
                    c1.State = CaptainStateEnum.Idle;
                    Captain c2 = new Captain("working-1");
                    c2.State = CaptainStateEnum.Working;
                    Captain c3 = new Captain("working-2");
                    c3.State = CaptainStateEnum.Working;

                    await db.Captains.CreateAsync(c1);
                    await db.Captains.CreateAsync(c2);
                    await db.Captains.CreateAsync(c3);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(3, status.TotalCaptains);
                    AssertEqual(1, status.IdleCaptains);
                    AssertEqual(2, status.WorkingCaptains);
                }
            });

            await RunTest("GetStatusAsync WithMissions GroupsByStatus", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Mission m1 = new Mission("Pending");
                    m1.Status = MissionStatusEnum.Pending;
                    Mission m2 = new Mission("InProgress");
                    m2.Status = MissionStatusEnum.InProgress;
                    Mission m3 = new Mission("Complete");
                    m3.Status = MissionStatusEnum.Complete;

                    await db.Missions.CreateAsync(m1);
                    await db.Missions.CreateAsync(m2);
                    await db.Missions.CreateAsync(m3);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(1, status.MissionsByStatus["Pending"]);
                    AssertEqual(1, status.MissionsByStatus["InProgress"]);
                    AssertEqual(1, status.MissionsByStatus["Complete"]);
                }
            });

            await RunTest("GetStatusAsync counts only pending missions waiting for resource pressure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), new StubGitService());

                    Mission waitingOne = new Mission("Waiting one") { Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.WaitingForResourcePressure };
                    Mission waitingTwo = new Mission("Waiting two") { Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.WaitingForResourcePressure };
                    Mission waitingForCaptain = new Mission("Waiting captain") { Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain };
                    Mission completedStale = new Mission("Completed stale") { Status = MissionStatusEnum.Complete, AssignmentState = MissionAssignmentStateEnum.WaitingForResourcePressure };

                    await db.Missions.CreateAsync(waitingOne);
                    await db.Missions.CreateAsync(waitingTwo);
                    await db.Missions.CreateAsync(waitingForCaptain);
                    await db.Missions.CreateAsync(completedStale);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(2, status.MissionsWaitingForResourcePressure, "Only pending missions whose assignment waits for resource pressure are counted");
                }
            });

            await RunTest("GetStatusAsync IncludesWorkProducedAndLandingFailedAfterLightweightCounts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Mission produced = new Mission("WP")
                    {
                        Status = MissionStatusEnum.WorkProduced,
                        AgentOutput = new string('o', 32 * 1024),
                        DiffSnapshot = new string('d', 32 * 1024)
                    };
                    Mission landingFailed1 = new Mission("LF1") { Status = MissionStatusEnum.LandingFailed };
                    Mission landingFailed2 = new Mission("LF2") { Status = MissionStatusEnum.LandingFailed };
                    Mission failed = new Mission("F1") { Status = MissionStatusEnum.Failed };

                    await db.Missions.CreateAsync(produced);
                    await db.Missions.CreateAsync(landingFailed1);
                    await db.Missions.CreateAsync(landingFailed2);
                    await db.Missions.CreateAsync(failed);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(1, status.MissionsByStatus["WorkProduced"]);
                    AssertEqual(2, status.MissionsByStatus["LandingFailed"]);
                    AssertEqual(1, status.MissionsByStatus["Failed"]);
                    AssertFalse(status.MissionsByStatus.ContainsKey("Pending"), "Empty buckets should be omitted");
                }
            });

            await RunTest("GetStatusAsync WithActiveVoyages IncludesProgress", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Voyage voyage = new Voyage("Test Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission m1 = new Mission("Done");
                    m1.VoyageId = voyage.Id;
                    m1.Status = MissionStatusEnum.Complete;
                    Mission m2 = new Mission("Working");
                    m2.VoyageId = voyage.Id;
                    m2.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(m1);
                    await db.Missions.CreateAsync(m2);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(1, status.ActiveVoyages);
                    AssertEqual(1, status.Voyages.Count);
                    AssertEqual(2, status.Voyages[0].TotalMissions);
                    AssertEqual(1, status.Voyages[0].CompletedMissions);
                    AssertEqual(1, status.Voyages[0].InProgressMissions);
                }
            });

            await RunTest("DispatchMissionAsync NullMission Throws", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    await AssertThrowsAsync<ArgumentNullException>(() => service.DispatchMissionAsync(null!));
                }
            });

            await RunTest("DispatchMissionAsync CreatesMissionInDb", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Mission mission = new Mission("Test Dispatch");
                    Mission result = await service.DispatchMissionAsync(mission);

                    AssertNotNull(result);
                    AssertEqual("Test Dispatch", result.Title);

                    Mission? fromDb = await db.Missions.ReadAsync(result.Id);
                    AssertNotNull(fromDb);
                }
            });

            await RunTest("DispatchVoyageAsync NullTitle Throws", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    await AssertThrowsAsync<ArgumentNullException>(() =>
                        service.DispatchVoyageAsync(null!, "desc", "vsl_id", new List<MissionDescription> { new MissionDescription("m1", "d1") }));
                }
            });

            await RunTest("DispatchVoyageAsync EmptyMissions Throws", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    await AssertThrowsAsync<ArgumentException>(() =>
                        service.DispatchVoyageAsync("Voyage", "desc", "vsl_id", new List<MissionDescription>()));
                }
            });

            await RunTest("DispatchVoyageAsync NonExistentVessel Throws", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    await AssertThrowsAsync<InvalidOperationException>(() =>
                        service.DispatchVoyageAsync("Voyage", "desc", "vsl_nonexistent",
                            new List<MissionDescription> { new MissionDescription("m1", "d1") }));
                }
            });

            await RunTest("DispatchVoyageAsync CreatesVoyageAndMissions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Fleet fleet = new Fleet("TestFleet");
                    await db.Fleets.CreateAsync(fleet);
                    Vessel vessel = new Vessel("TestVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    Voyage result = await service.DispatchVoyageAsync(
                        "My Voyage", "A test", vessel.Id,
                        new List<MissionDescription> { new MissionDescription("Mission 1", "Desc 1"), new MissionDescription("Mission 2", "Desc 2") });

                    AssertNotNull(result);
                    // Voyage stays Open when no captains are available to auto-assign missions
                    AssertEqual(VoyageStatusEnum.Open, result.Status);

                    List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(result.Id);
                    AssertEqual(2, missions.Count);
                }
            });

            // Dispatch ergonomics: a read-only dispatch (all missions Audit/Research)
            // must NOT inherit a multi-stage vessel default pipeline -- a four-mission diagnostic
            // probe once expanded to sixteen missions. An explicitly requested pipeline still wins.
            await RunTest("DispatchVoyageAsync ReadOnlyMissionsSkipVesselDefaultPipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 2;
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline reviewed = new Pipeline("Reviewed");
                    reviewed.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    reviewed = await db.Pipelines.CreateAsync(reviewed);

                    Vessel vessel = new Vessel("ReadOnlyVessel", "https://github.com/test/repo");
                    vessel.DefaultPipelineId = reviewed.Id;
                    await db.Vessels.CreateAsync(vessel);

                    // All-mission Audit dispatch with no explicit pipeline: single Worker mission.
                    Voyage auditVoyage = await service.DispatchVoyageAsync(
                        "Audit Probe", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe A", "inspect") { Mode = "Audit" },
                            new MissionDescription("Probe B", "inspect") { Mode = "Audit" }
                        },
                        (string?)null).ConfigureAwait(false);

                    List<Mission> auditMissions = await db.Missions.EnumerateByVoyageAsync(auditVoyage.Id).ConfigureAwait(false);
                    AssertEqual(2, auditMissions.Count, "Read-only dispatch must not expand stages from the vessel default pipeline");
                    AssertTrue(auditMissions.All(m => !String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase)),
                        "Read-only dispatch must not create the default pipeline's review stages");

                    // An explicit pipeline is always honored, even for read-only missions.
                    Voyage explicitVoyage = await service.DispatchVoyageAsync(
                        "Audit With Pipeline", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe C", "inspect") { Mode = "Research" }
                        },
                        reviewed.Id).ConfigureAwait(false);

                    List<Mission> explicitMissions = await db.Missions.EnumerateByVoyageAsync(explicitVoyage.Id).ConfigureAwait(false);
                    AssertEqual(2, explicitMissions.Count, "An explicitly requested pipeline must still expand its stages");
                }
            });

            // Read-only missions still run every declared pipeline stage. Review and verification
            // stages consume the report and must remain in the dependency chain.
            await RunTest("DispatchVoyageAsync ReadOnlyMissionPreservesTheTestEngineerStage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 2;
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline tested = new Pipeline("WorkerTestedJudged");
                    tested.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    };
                    tested = await db.Pipelines.CreateAsync(tested);

                    Vessel vessel = new Vessel("StageModeVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Voyage auditVoyage = await service.DispatchVoyageAsync(
                        "Audit With Full Pipeline", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe", "inspect") { Mode = "Audit" }
                        },
                        tested.Id).ConfigureAwait(false);

                    List<Mission> auditMissions = await db.Missions.EnumerateByVoyageAsync(auditVoyage.Id).ConfigureAwait(false);

                    List<Mission> orderedAuditMissions = auditMissions.OrderBy(m => m.StageOrder).ToList();
                    AssertEqual(3, orderedAuditMissions.Count, "Every declared pipeline stage must be materialized for a read-only mission");
                    AssertTrue(
                        orderedAuditMissions.Any(m => String.Equals(m.Persona, "TestEngineer", StringComparison.OrdinalIgnoreCase)),
                        "A TestEngineer verification stage must remain on an Audit mission");
                    AssertNull(orderedAuditMissions[0].DependsOnMissionId, "The first stage must have no dependency");
                    AssertEqual(orderedAuditMissions[0].Id, orderedAuditMissions[1].DependsOnMissionId, "The TestEngineer must depend on Worker");
                    AssertEqual(orderedAuditMissions[1].Id, orderedAuditMissions[2].DependsOnMissionId, "The Judge must depend on TestEngineer");
                    AssertTrue(
                        auditMissions.Any(m => String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase)),
                        "The review stage still has a report to read and must survive");

                    Voyage implVoyage = await service.DispatchVoyageAsync(
                        "Implementation With Full Pipeline", "build it", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Build", "implement") { Mode = "Implementation" }
                        },
                        tested.Id).ConfigureAwait(false);

                    List<Mission> implMissions = await db.Missions.EnumerateByVoyageAsync(implVoyage.Id).ConfigureAwait(false);
                    AssertEqual(3, implMissions.Count, "An implementing mission keeps every stage of its pipeline");
                }
            });

            // A stage that does not inherit the dispatch mode runs as Implementation: the gate then
            // judges report-only work by its commit, and the brief carries instructions the captain
            // cannot follow.
            await RunTest("DispatchVoyageAsync PipelineStagesInheritTheMissionMode", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Pipeline reviewed = new Pipeline("WorkerJudged");
                    reviewed.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    reviewed = await db.Pipelines.CreateAsync(reviewed);

                    Vessel vessel = new Vessel("ModeInheritVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = await service.DispatchVoyageAsync(
                        "Audit Modes", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe", "inspect") { Mode = "Audit" }
                        },
                        reviewed.Id).ConfigureAwait(false);

                    List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);

                    AssertTrue(missions.Count > 0, "the dispatch must create stages");
                    AssertTrue(
                        missions.All(m => m.Mode == MissionModeEnum.Audit),
                        "every stage of a read-only dispatch must run read-only");
                }
            });

            await RunTest("DispatchVoyageAsync PipelineResolvesStageOverridesForEachPersona", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.ModelTier.CopyFrom(FleetRoutingSettings.CreateModelTier());
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline pipeline = new Pipeline("PersonaResolvedRouting");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker") { PreferredModel = "high" },
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    };
                    pipeline = await db.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Vessel vessel = new Vessel("PersonaResolvedVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = await service.DispatchVoyageAsync(
                        "Persona resolved routing",
                        "verify persisted stage preferences",
                        vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Change code") { PreferredModel = "mid" }
                        },
                        pipeline.Id).ConfigureAwait(false);

                    List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual("mid", missions.Single(mission => mission.Persona == "Worker").PreferredModel,
                        "a Worker stage override of high must cap to its assignable mid tier");
                    AssertEqual("high", missions.Single(mission => mission.Persona == "TestEngineer").PreferredModel,
                        "a specialist stage must upgrade the inherited mission tier to high");
                    AssertEqual("high", missions.Single(mission => mission.Persona == "Judge").PreferredModel,
                        "a Judge stage must upgrade the inherited mission tier to high");
                }
            });

            await RunTest("DispatchVoyageQueuedAsync PipelinePreservesLiteralModelPins", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.ModelTier.CopyFrom(FleetRoutingSettings.CreateModelTier());
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline pipeline = new Pipeline("LiteralModelRouting");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await db.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Vessel vessel = new Vessel("LiteralModelVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = await service.DispatchVoyageQueuedAsync(
                        "Literal model routing",
                        "keep the operator pin",
                        vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Change code") { PreferredModel = "gpt-5.6-luna" }
                        },
                        pipeline.Id,
                        null).ConfigureAwait(false);

                    List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertTrue(missions.All(mission => mission.PreferredModel == "gpt-5.6-luna"),
                        "queued pipeline creation must preserve a literal model pin for Worker and Judge stages");
                }
            });

            await RunTest("DispatchVoyageQueuedAsync ReturnsBeforeAssignmentLaunchCompletes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    TaskCompletionSource<bool> launchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<int> releaseLaunch = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.OnLaunchAgent = (captain, mission, dock) =>
                    {
                        launchStarted.TrySetResult(true);
                        return releaseLaunch.Task;
                    };

                    Vessel vessel = new Vessel("QueuedVessel", "https://github.com/test/repo");
                    vessel.DefaultBranch = "main";
                    await db.Vessels.CreateAsync(vessel);

                    Captain captain = new Captain("queued-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await db.Captains.CreateAsync(captain);

                    Task<Voyage> dispatchTask = service.DispatchVoyageQueuedAsync(
                        "Queued Voyage", "A queued test", vessel.Id,
                        new List<MissionDescription> { new MissionDescription("Queued Mission", "Desc") },
                        null,
                        null);

                    Task completed = await Task.WhenAny(dispatchTask, Task.Delay(1000)).ConfigureAwait(false);
                    AssertTrue(Object.ReferenceEquals(dispatchTask, completed), "Queued dispatch should return before the launch delegate completes");

                    Voyage voyage = await dispatchTask.ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Open, voyage.Status, "Queued voyage should remain Open until background assignment advances it");

                    completed = await Task.WhenAny(launchStarted.Task, Task.Delay(2000)).ConfigureAwait(false);
                    AssertTrue(Object.ReferenceEquals(launchStarted.Task, completed), "Background assignment should still start after durable creation");

                    releaseLaunch.SetResult(424242);

                    Mission? mission = null;
                    for (int i = 0; i < 20; i++)
                    {
                        List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                        mission = missions.SingleOrDefault();
                        if (mission != null && mission.Status == MissionStatusEnum.InProgress) break;
                        await Task.Delay(100).ConfigureAwait(false);
                    }

                    AssertNotNull(mission, "Queued mission should exist");
                    AssertEqual(MissionStatusEnum.InProgress, mission!.Status, "Queued mission should finish background assignment once launch completes");
                    AssertEqual(424242, mission.ProcessId, "Queued assignment should persist the launched process id");
                }
            });

            await RunTest("RecallCaptainAsync NullId Throws", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    await AssertThrowsAsync<ArgumentNullException>(() => service.RecallCaptainAsync(null!));
                }
            });

            await RunTest("RecallCaptainAsync NonExistentCaptain Throws", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    await AssertThrowsAsync<InvalidOperationException>(() => service.RecallCaptainAsync("cpt_nonexistent"));
                }
            });

            await RunTest("RecallCaptainAsync SetsCaptainToIdle", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Captain captain = new Captain("recall-target");
                    captain.State = CaptainStateEnum.Working;
                    await db.Captains.CreateAsync(captain);

                    await service.RecallCaptainAsync(captain.Id);

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual(CaptainStateEnum.Idle, result!.State);
                    AssertNull(result.CurrentMissionId);
                    AssertNull(result.CurrentDockId);
                    AssertNull(result.ProcessId);
                }
            });

            await RunTest("RecallCaptainAsync FailsActiveMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Mission mission = new Mission("Active Mission");
                    mission.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("recall-active");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    await db.Captains.CreateAsync(captain);

                    await service.RecallCaptainAsync(captain.Id);

                    Mission? result = await db.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.Failed, result!.Status);
                    AssertNotNull(result.CompletedUtc);
                }
            });

            await RunTest("RecallAllAsync RecallsAllWorkingCaptains", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Captain c1 = new Captain("worker-1");
                    c1.State = CaptainStateEnum.Working;
                    Captain c2 = new Captain("worker-2");
                    c2.State = CaptainStateEnum.Working;
                    Captain c3 = new Captain("idle-1");
                    c3.State = CaptainStateEnum.Idle;

                    await db.Captains.CreateAsync(c1);
                    await db.Captains.CreateAsync(c2);
                    await db.Captains.CreateAsync(c3);

                    await service.RecallAllAsync();

                    Captain? r1 = await db.Captains.ReadAsync(c1.Id);
                    Captain? r2 = await db.Captains.ReadAsync(c2.Id);
                    Captain? r3 = await db.Captains.ReadAsync(c3.Id);

                    AssertEqual(CaptainStateEnum.Idle, r1!.State);
                    AssertEqual(CaptainStateEnum.Idle, r2!.State);
                    AssertEqual(CaptainStateEnum.Idle, r3!.State);
                }
            });

            await RunTest("HandleProcessExitAsync RateLimitFailure RequeuesMissionWithoutHaltingVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxRecoveryAttempts = 3;
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Quota Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Quota Judge");
                    mission.VoyageId = voyage.Id;
                    // No VesselId: drives the deterministic _RetryDispatchNeeded fallback requeue branch
                    // (QueueVoyageAssignments needs a vessel id; its absence avoids a background reassign race).
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 4242;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("quota-judge");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 4242;
                    await db.Captains.CreateAsync(captain);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] You've hit your limit and must wait for reset.\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(4242, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertNotNull(updatedCaptain, "Captain should still exist");
                    AssertNotNull(updatedVoyage, "Voyage should still exist");
                    // Old behavior marked the mission Failed and cancelled the voyage; the quota re-route requeues instead.
                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "Rate-limit (quota) failure should re-route the mission to Pending, not Failed");
                    AssertEqual(MissionAssignmentStateEnum.Pending, updatedMission.AssignmentState, "Requeued mission should reset AssignmentState to Pending");
                    AssertContains("hit your limit", updatedMission.FailureReason ?? String.Empty, "Requeued mission should preserve the failure reason for operator visibility");
                    AssertNull(updatedMission.CaptainId, "Requeued mission should clear the captain binding");
                    AssertNull(updatedMission.ProcessId, "Requeued mission should clear the stale process id");
                    AssertEqual(CaptainStateEnum.Quarantined, updatedCaptain!.State, "Failing captain should be quarantined so the retry selects a different captain");
                    AssertNull(updatedCaptain.CurrentMissionId, "Stalled captain should release its mission assignment");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Transient captain failure must NOT halt the voyage");
                    AssertTrue(GetRetryDispatchNeeded(service), "Requeue without a vessel id should flag the retry dispatch sweep");

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    AssertFalse(events.Objects.Any(e => e.EventType == "mission.failed" && e.MissionId == mission.Id), "Retryable requeue must not emit mission.failed");
                    AssertTrue(events.Objects.Any(e => e.EventType == "mission.quota_rerouted" && e.MissionId == mission.Id), "A quota re-route should emit a non-terminal mission.quota_rerouted event");
                }
            });

            await RunTest("HandleProcessExitAsync QuotaFailureOnAccountCaptain HoldsWholeAccountExhausted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxRecoveryAttempts = 3;
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    Captain failing = new Captain("account-first") { Model = "shared-model", State = CaptainStateEnum.Working, ProcessId = 5151 };
                    Captain sibling = new Captain("account-second") { Model = "shared-model", State = CaptainStateEnum.Idle };
                    Captain other = new Captain("other-account") { Model = "shared-model", State = CaptainStateEnum.Idle };
                    await db.Captains.CreateAsync(failing);
                    await db.Captains.CreateAsync(sibling);
                    await db.Captains.CreateAsync(other);
                    settings.ModelTier.UsageRouting = new UsageRoutingSettings
                    {
                        Enabled = true,
                        Accounts = new List<UsageAccountSettings>
                        {
                            new UsageAccountSettings { Id = "shared", CaptainIds = new List<string> { failing.Id, sibling.Id } },
                            new UsageAccountSettings { Id = "separate", CaptainIds = new List<string> { other.Id } }
                        },
                        PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                        {
                            ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "shared" }, new UsageRouteSettings { AccountId = "separate" } }
                        }
                    };
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Account quota voyage") { Status = VoyageStatusEnum.InProgress };
                    await db.Voyages.CreateAsync(voyage);
                    Mission mission = new Mission("Account quota mission")
                    {
                        VoyageId = voyage.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 5151
                    };
                    await db.Missions.CreateAsync(mission);
                    failing.CurrentMissionId = mission.Id;
                    await db.Captains.UpdateAsync(failing);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] You've hit your limit and must wait for reset.\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(5151, 1, failing.Id, mission.Id).ConfigureAwait(false);

                    Captain? failingAfter = await db.Captains.ReadAsync(failing.Id).ConfigureAwait(false);
                    Captain? siblingAfter = await db.Captains.ReadAsync(sibling.Id).ConfigureAwait(false);
                    Captain? otherAfter = await db.Captains.ReadAsync(other.Id).ConfigureAwait(false);
                    Mission? missionAfter = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, missionAfter!.Status, "The mission is re-routed, not failed");
                    AssertEqual(CaptainStateEnum.Quarantined, failingAfter!.State, "The failing captain is benched");
                    AssertEqual(CaptainStateEnum.Quarantined, siblingAfter!.State, "An idle captain on the same account must not receive the re-routed mission");
                    AssertEqual(CaptainStateEnum.Idle, otherAfter!.State, "A captain on a different account stays available");

                    UsageRoutingService usage = UsageRoutingService.For(settings);
                    ProviderUsageStatus status = usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[0], null, DateTime.UtcNow);
                    AssertEqual("Exhausted", status.State, "The whole account is Exhausted");
                    AssertEqual("account_provider_failure", status.Reason);
                    AssertTrue(status.ExhaustedUntilUtc > DateTime.UtcNow, "The hold lasts until the retry time");
                    AssertFalse(usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[1], null, DateTime.UtcNow).State == "Exhausted", "The other account is not held");

                    // Routing refuses the account even for a sibling that is idle and unbenched, for example one that
                    // finished a running mission after the failure.
                    Captain freed = new Captain("account-second") { Id = sibling.Id, Model = "shared-model", State = CaptainStateEnum.Idle };
                    UsageRoutingDecision decision = usage.Select(settings.ModelTier.UsageRouting, new Mission { Persona = "Worker" },
                        new List<Captain> { freed, otherAfter }, Array.Empty<string>(), DateTime.UtcNow);
                    AssertEqual(1, decision.Candidates.Count, "Only the captain on the other account is a candidate");
                    AssertEqual(other.Id, decision.Candidates[0].Id);
                }
            });

            await RunTest("HealthCheckAsync NoCaptains DoesNotThrow", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    await service.HealthCheckAsync();
                }
            });

            await RunTest("HealthCheckAsync WorkingCaptainNoProcessId NoMission ReleasesToIdle", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    // A Working captain with no process ID and no mission is orphaned
                    // and should be released to Idle by the health check.
                    Captain captain = new Captain("no-pid");
                    captain.State = CaptainStateEnum.Working;
                    captain.ProcessId = null;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual(CaptainStateEnum.Idle, result!.State);
                }
            });

            await RunTest("HealthCheckAsync CompletionVerificationMissingPid RequeuesMissionAndLeavesSiblingsUntouched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Missing PID Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage = await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Missing PID Mission");
                    mission.VoyageId = voyage.Id;
                    // No VesselId: keeps the requeue on the deterministic _RetryDispatchNeeded fallback branch.
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = null;
                    mission.StartedUtc = DateTime.UtcNow.AddMinutes(-1);
                    mission = await db.Missions.CreateAsync(mission);

                    // Sibling already finished its work; the requeue must leave it alone (no voyage halt).
                    Mission sibling = new Mission("Sibling WorkProduced");
                    sibling.VoyageId = voyage.Id;
                    sibling.Status = MissionStatusEnum.WorkProduced;
                    sibling.LastUpdateUtc = DateTime.UtcNow;
                    sibling = await db.Missions.CreateAsync(sibling);

                    Captain captain = new Captain("missing-pid-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = null;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    Mission? updatedSibling = await db.Missions.ReadAsync(sibling.Id);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    // Old behavior failed the mission loudly and cancelled the voyage; completion-verification
                    // failures are now treated as transient orchestration faults and requeued.
                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "Completion-verification (missing PID) failure should requeue the mission to Pending");
                    AssertEqual(MissionAssignmentStateEnum.Pending, updatedMission.AssignmentState, "Requeued mission should reset AssignmentState to Pending");
                    AssertContains("agent completion cannot be verified", updatedMission.FailureReason ?? String.Empty, "Requeue should record the completion-verification reason");
                    AssertNull(updatedMission.CaptainId, "Requeued mission should clear the captain binding");
                    AssertEqual(CaptainStateEnum.Stalled, updatedCaptain!.State, "Failing captain should be benched (Stalled) after a completion-verification fault");
                    AssertNull(updatedCaptain.CurrentMissionId, "Stalled captain should release its mission assignment");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Voyage must remain InProgress when a mission is requeued");
                    AssertNotNull(updatedSibling, "Sibling should still exist");
                    AssertEqual(MissionStatusEnum.WorkProduced, updatedSibling!.Status, "Sibling WorkProduced mission must be untouched by the requeue");
                }
            });

            await RunTest("HealthCheckAsync FreshActiveMissionWithoutProcessId SkipsMissingPidFailure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.LaunchProcessIdGraceSeconds = 30;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Fresh Missing PID Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage = await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Fresh Missing PID Mission");
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = null;
                    mission.StartedUtc = DateTime.UtcNow;
                    mission = await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("fresh-missing-pid-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = null;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.InProgress, updatedMission!.Status, "Fresh active mission without a PID should remain active inside launch grace");
                    AssertNull(updatedMission.FailureReason, "Fresh active mission should not receive a missing-PID failure reason inside launch grace");
                    AssertEqual(CaptainStateEnum.Working, updatedCaptain!.State, "Captain should remain working while launch PID registration is still fresh");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Voyage should remain active while missing-PID grace applies");
                }
            });

            await RunTest("HealthCheckAsync DeadProcess RecoveryExhausted StallsCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxRecoveryAttempts = 0;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Mission mission = new Mission("Test");
                    mission.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("dead-process");
                    captain.State = CaptainStateEnum.Working;
                    captain.ProcessId = 99999999;
                    captain.CurrentMissionId = mission.Id;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Captain? result = await db.Captains.ReadAsync(captain.Id);
                    AssertEqual(CaptainStateEnum.Idle, result!.State);
                }
            });

            await RunTest("HealthCheckAsync AssignedOrphanWithoutStartedProcess RevertsToPending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Vessel vessel = new Vessel("orphan-vessel", "https://github.com/test/repo.git");
                    await db.Vessels.CreateAsync(vessel);

                    Captain originalCaptain = new Captain("original");
                    originalCaptain.State = CaptainStateEnum.Idle;
                    await db.Captains.CreateAsync(originalCaptain);

                    Captain movedCaptain = new Captain("moved");
                    movedCaptain.State = CaptainStateEnum.Working;
                    await db.Captains.CreateAsync(movedCaptain);

                    Mission mission = new Mission("Assigned orphan");
                    mission.VesselId = vessel.Id;
                    mission.CaptainId = originalCaptain.Id;
                    mission.Status = MissionStatusEnum.Assigned;
                    mission.BranchName = "armada/test/orphan";
                    mission = await db.Missions.CreateAsync(mission);
                    await SetMissionLastUpdateUtcAsync(testDb, mission.Id, DateTime.UtcNow.AddMinutes(-2));

                    originalCaptain.State = CaptainStateEnum.Working;
                    originalCaptain.CurrentMissionId = "different-mission";
                    await db.Captains.UpdateAsync(originalCaptain);

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "Assigned orphan that never started should return to Pending");
                    AssertNull(updatedMission.CaptainId, "Pending orphan should be unassigned");
                    AssertNull(updatedMission.DockId, "Pending orphan should clear dock");
                }
            });

            await RunTest("HealthCheckAsync FreshAssignedMissionWithoutStartedProcess SkipsOrphanRecovery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Vessel vessel = new Vessel("fresh-assigned-vessel", "https://github.com/test/repo.git");
                    await db.Vessels.CreateAsync(vessel);

                    Captain captain = new Captain("fresh-original");
                    captain.State = CaptainStateEnum.Idle;
                    await db.Captains.CreateAsync(captain);

                    Mission mission = new Mission("Fresh assigned orphan candidate");
                    mission.VesselId = vessel.Id;
                    mission.CaptainId = captain.Id;
                    mission.Status = MissionStatusEnum.Assigned;
                    mission.BranchName = "armada/test/fresh";
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission = await db.Missions.CreateAsync(mission);

                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = "different-mission";
                    await db.Captains.UpdateAsync(captain);

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Assigned, updatedMission!.Status, "Freshly assigned mission should remain Assigned during the grace window");
                    AssertEqual(captain.Id, updatedMission.CaptainId, "Freshly assigned mission should keep its captain assignment during the grace window");
                }
            });

            await RunTest("HealthCheckAsync FreshWorkProducedMission SkipsCaptainRelease", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Mission mission = new Mission("Fresh work produced");
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission = await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("fresh-work-produced");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(updatedCaptain, "Captain should still exist");
                    AssertEqual(CaptainStateEnum.Working, updatedCaptain!.State, "Freshly WorkProduced mission should not release the captain during handoff grace");
                    AssertEqual(mission.Id, updatedCaptain.CurrentMissionId, "Freshly WorkProduced mission should keep the current mission assignment during handoff grace");
                }
            });

            await RunTest("HealthCheckAsync StaleWorkProducedMission ReleasesCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Mission mission = new Mission("Stale work produced");
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission = await db.Missions.CreateAsync(mission);
                    await SetMissionLastUpdateUtcAsync(testDb, mission.Id, DateTime.UtcNow.AddMinutes(-5));

                    Captain captain = new Captain("stale-work-produced");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id);
                    AssertNotNull(updatedCaptain, "Captain should still exist");
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State, "Stale WorkProduced mission should release the captain");
                    AssertNull(updatedCaptain.CurrentMissionId, "Stale WorkProduced mission should clear the current mission assignment when released");
                }
            });

            await RunTest("HealthCheckAsync StageWatchdogFailsStaleAssignedMissionWithoutCaptainHeartbeat", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.StageWatchdogTimeoutMinutes = 5;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Mission mission = new Mission("Stale stage mission");
                    mission.Status = MissionStatusEnum.Assigned;
                    mission.CaptainId = null;
                    mission.ProcessId = null;
                    mission = await db.Missions.CreateAsync(mission);
                    await SetMissionLastUpdateUtcAsync(testDb, mission.Id, DateTime.UtcNow.AddMinutes(-6));

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "Stage watchdog should fail stale stage missions without a captain/process heartbeat");
                    AssertEqual("stage_watchdog_no_captain_heartbeat", updatedMission.FailureReason);

                    EnumerationResult<Signal> signals = await db.Signals.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(1, signals.Objects.Count, "Stage watchdog should emit an operator-visible error signal");
                    AssertEqual(SignalTypeEnum.Error, signals.Objects[0].Type);
                    AssertContains("stage_watchdog_no_captain_heartbeat", signals.Objects[0].Payload);
                }
            });

            await RunTest("HealthCheckAsync StageWatchdogDoesNotFailStaleWorkProducedMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.StageWatchdogTimeoutMinutes = 5;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Mission mission = new Mission("Completed handoff mission");
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.CaptainId = null;
                    mission.ProcessId = null;
                    mission = await db.Missions.CreateAsync(mission);
                    await SetMissionLastUpdateUtcAsync(testDb, mission.Id, DateTime.UtcNow.AddMinutes(-60));

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.WorkProduced, updatedMission!.Status, "Stage watchdog must not fail post-work missions after captain release");
                    AssertNull(updatedMission.FailureReason, "Post-work mission should not receive a watchdog failure reason");

                    EnumerationResult<Signal> signals = await db.Signals.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(0, signals.Objects.Count, "Stage watchdog should not emit error signals for stale WorkProduced missions");
                }
            });

            await RunTest("HealthCheckAsync ChecksVoyageCompletions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Voyage voyage = new Voyage("Test Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission m1 = new Mission("Done 1");
                    m1.VoyageId = voyage.Id;
                    m1.Status = MissionStatusEnum.Complete;
                    await db.Missions.CreateAsync(m1);

                    Mission m2 = new Mission("Done 2");
                    m2.VoyageId = voyage.Id;
                    m2.Status = MissionStatusEnum.Complete;
                    await db.Missions.CreateAsync(m2);

                    await service.HealthCheckAsync();

                    Voyage? result = await db.Voyages.ReadAsync(voyage.Id);
                    AssertEqual(VoyageStatusEnum.Complete, result!.Status);
                    AssertNotNull(result.CompletedUtc);
                }
            });

            await RunTest("HealthCheckAsync VoyageNotComplete WhenMissionsStillActive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Voyage voyage = new Voyage("Active Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission m1 = new Mission("Done");
                    m1.VoyageId = voyage.Id;
                    m1.Status = MissionStatusEnum.Complete;
                    await db.Missions.CreateAsync(m1);

                    Mission m2 = new Mission("Still Working");
                    m2.VoyageId = voyage.Id;
                    m2.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(m2);

                    await service.HealthCheckAsync();

                    Voyage? result = await db.Voyages.ReadAsync(voyage.Id);
                    AssertEqual(VoyageStatusEnum.InProgress, result!.Status);
                }
            });

            await RunTest("HealthCheckAsync VoyageFailsWithMixOfCompleteFailedCancelled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Voyage voyage = new Voyage("Mixed Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission m1 = new Mission("Complete");
                    m1.VoyageId = voyage.Id;
                    m1.Status = MissionStatusEnum.Complete;
                    await db.Missions.CreateAsync(m1);

                    Mission m2 = new Mission("Failed");
                    m2.VoyageId = voyage.Id;
                    m2.Status = MissionStatusEnum.Failed;
                    await db.Missions.CreateAsync(m2);

                    Mission m3 = new Mission("Cancelled");
                    m3.VoyageId = voyage.Id;
                    m3.Status = MissionStatusEnum.Cancelled;
                    await db.Missions.CreateAsync(m3);

                    await service.HealthCheckAsync();

                    Voyage? result = await db.Voyages.ReadAsync(voyage.Id);
                    AssertEqual(VoyageStatusEnum.Failed, result!.Status);
                }
            });

            await RunTest("HealthCheckAsync ClearsRetryFlagWhenNoPendingMissionsRemain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), git);

                    SetRetryDispatchNeeded(service, true);

                    await service.HealthCheckAsync();

                    AssertFalse(GetRetryDispatchNeeded(service));
                }
            });

            await RunTest("HealthCheckAsync KeepsRetryFlagWhenPendingMissionHasNoCapacity", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Fleet fleet = new Fleet("Retry Fleet");
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("Retry Vessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    Mission mission = new Mission("Pending Retry");
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.Pending;
                    await db.Missions.CreateAsync(mission);

                    SetRetryDispatchNeeded(service, true);

                    await service.HealthCheckAsync();

                    AssertTrue(GetRetryDispatchNeeded(service));
                }
            });

            await RunTest("HandleProcessExitAsync InvalidModelFailure RequeuesViaQueueVoyageAssignments", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Model Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    // Real vessel (FK target) but no idle captains, so the QueueVoyageAssignments branch
                    // is taken and its background reassign attempt is a deterministic no-op.
                    Vessel vessel = new Vessel("Requeue Vessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Mission mission = new Mission("Invalid Model Mission");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 7100;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("invalid-model-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7100;
                    await db.Captains.CreateAsync(captain);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] invalid model: foo-bar-9000 is not a recognized model\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(7100, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "Invalid-model (captain-unavailable) failure should requeue the mission to Pending");
                    AssertContains("invalid model", updatedMission.FailureReason ?? String.Empty, "Requeued mission should preserve the model failure reason");
                    AssertEqual(CaptainStateEnum.Stalled, updatedCaptain!.State, "Failing captain should be benched (Stalled) after an invalid-model fault");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Invalid-model failure must NOT halt the voyage");
                    AssertFalse(GetRetryDispatchNeeded(service), "Requeue with a vessel id should use QueueVoyageAssignments, not the retry-sweep fallback flag");
                }
            });

            await RunTest("HandleProcessExitAsync GenuineUnrecoverableFailure HaltsVoyageAndCancelsNonTerminalSiblings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Doomed Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    // No mission log file: the reason falls back to a generic non-transient exit message.
                    Mission mission = new Mission("Genuine Failure Mission");
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = 7200;
                    await db.Missions.CreateAsync(mission);

                    Mission activeSibling = new Mission("Active Sibling");
                    activeSibling.VoyageId = voyage.Id;
                    activeSibling.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(activeSibling);

                    Mission producedSibling = new Mission("Produced Sibling");
                    producedSibling.VoyageId = voyage.Id;
                    producedSibling.Status = MissionStatusEnum.WorkProduced;
                    await db.Missions.CreateAsync(producedSibling);

                    Captain captain = new Captain("genuine-failure-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7200;
                    await db.Captains.CreateAsync(captain);

                    await service.HandleProcessExitAsync(7200, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Mission? updatedActiveSibling = await db.Missions.ReadAsync(activeSibling.Id).ConfigureAwait(false);
                    Mission? updatedProducedSibling = await db.Missions.ReadAsync(producedSibling.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "A genuine unrecoverable failure should still mark the mission Failed");
                    AssertContains("exited with code", updatedMission.FailureReason ?? String.Empty, "Failed mission should record the generic exit reason");
                    AssertEqual(VoyageStatusEnum.Cancelled, updatedVoyage!.Status, "Genuine unrecoverable failure should still halt the voyage");
                    AssertEqual(MissionStatusEnum.Cancelled, updatedActiveSibling!.Status, "Non-terminal siblings should be cancelled when the voyage halts");
                    AssertEqual(MissionStatusEnum.WorkProduced, updatedProducedSibling!.Status, "WorkProduced siblings are terminal and must not be cancelled by the halt");
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State, "A non-captain-unavailable failure should release the captain to Idle, not Stalled");

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    AssertTrue(events.Objects.Any(e => e.EventType == "mission.failed" && e.MissionId == mission.Id), "Genuine failure should emit mission.failed");
                }
            });

            // Autonomous recovery never selects a mission whose voyage is Cancelled, and the halt above
            // cancels the voyage. The terminal exit path therefore owns the incident for this failure;
            // otherwise the failure is recorded nowhere an operator triages.
            await RunTest("HandleProcessExitAsync GenuineUnrecoverableFailure OpensIncidentForHaltedVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = new Voyage("Incident Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Incident Mission");
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = 7300;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("incident-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7300;
                    await db.Captains.CreateAsync(captain);

                    await service.HandleProcessExitAsync(7300, 1, captain.Id, mission.Id).ConfigureAwait(false);
                    await service.HandleProcessExitAsync(7300, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    EnumerationResult<Incident> incidents = await new IncidentService(db).EnumerateAsync(
                        Armada.Test.Unit.TestHelpers.McpTestCaller.Operator,
                        new IncidentQuery { MissionId = mission.Id, PageNumber = 1, PageSize = 25 }).ConfigureAwait(false);
                    List<Incident> open = incidents.Objects.Where(i => i.Status != IncidentStatusEnum.Closed).ToList();
                    AssertEqual(1, open.Count, "a genuine terminal exit that halts its voyage must open exactly one incident");
                    AssertEqual(voyage.Id, open[0].VoyageId, "the incident names the halted voyage");
                    AssertContains("exited with code", open[0].RootCause ?? String.Empty, "the incident carries the failure reason");
                }
            });

            // A mission that failed with a non-zero exit but whose
            // captain branch exists in the vessel bare repo holds RECOVERABLE committed work. The
            // branch must be preserved (no reap), the voyage must NOT be cascade-cancelled, and only
            // the missions that directly depend on the failed mission are cancelled. Independent
            // siblings keep running so their work is not destroyed by the failure.
            await RunTest("HandleProcessExitAsync RecoverableWorkFailure PreservesBranchAndLeavesVoyageRunning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Vessel vessel = new Vessel("Recoverable Vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, "recoverable.git");
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = new Voyage("Recoverable Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    string branchName = "armada/claude-1/msn_recoverable";
                    git.ExistingBranches.Add(branchName);

                    Mission mission = new Mission("Recoverable Failure Mission");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.BranchName = branchName;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = 7300;
                    await db.Missions.CreateAsync(mission);

                    Mission dependent = new Mission("Dependent Stage");
                    dependent.VoyageId = voyage.Id;
                    dependent.DependsOnMissionId = mission.Id;
                    dependent.Status = MissionStatusEnum.Pending;
                    await db.Missions.CreateAsync(dependent);

                    Mission independentSibling = new Mission("Independent Sibling");
                    independentSibling.VoyageId = voyage.Id;
                    independentSibling.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(independentSibling);

                    Captain captain = new Captain("recoverable-failure-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7300;
                    await db.Captains.CreateAsync(captain);

                    await service.HandleProcessExitAsync(7300, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Mission? updatedDependent = await db.Missions.ReadAsync(dependent.Id).ConfigureAwait(false);
                    Mission? updatedSibling = await db.Missions.ReadAsync(independentSibling.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "The mission itself must still be marked Failed.");
                    AssertContains("recoverable work preserved", updatedMission.FailureReason ?? String.Empty, "The failure reason must name the preserved branch.");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "The voyage must NOT be cascade-cancelled when the failed mission has recoverable work.");
                    AssertEqual(MissionStatusEnum.Cancelled, updatedDependent!.Status, "A direct dependent of the failed mission must be cancelled (it cannot run without the upstream).");
                    AssertEqual(MissionStatusEnum.InProgress, updatedSibling!.Status, "An independent sibling must keep running when the failed mission has recoverable work.");
                    AssertFalse(
                        git.DeleteBranchCalls.Any(call => call.EndsWith(":" + branchName, StringComparison.Ordinal)),
                        "The recoverable captain branch must not be reaped.");

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    AssertTrue(events.Objects.Any(e => e.EventType == "mission.failed_recoverable_work" && e.MissionId == mission.Id), "Recoverable-work failure should emit mission.failed_recoverable_work");
                }
            });

            await RunTest("HandleProcessExitAsync InterruptedExit RedispatchesMissionAndKeepsVoyageRunning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Interrupted voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("Interrupted mission")
                    {
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 7400,
                        StartedUtc = DateTime.UtcNow.AddSeconds(-5)
                    }).ConfigureAwait(false);
                    Captain captain = new Captain("interrupted-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7400;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await service.HandleProcessExitAsync(7400, -1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "an interrupted exit re-dispatches the mission");
                    AssertNull(updatedMission.CaptainId, "the re-dispatched mission is unbound from its captain");
                    AssertContains("interrupted", updatedMission.FailureReason ?? String.Empty, "the re-dispatch records why the run ended");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "an interrupted exit does not halt the voyage");
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State, "an interruption is not a captain fault, so the captain is released, not stalled");
                    AssertEqual(1, events.Count(e => e.EventType == "mission.interrupted_redispatched"), "the re-dispatch emits one named event");
                    AssertFalse(events.Any(e => e.EventType == "mission.failed"), "an interrupted exit emits no mission failure, so recovery opens no rescue");
                }
            });

            await RunTest("HandleProcessExitAsync InterruptedExit BudgetExhausted FailsTerminally", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Repeatedly interrupted voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("Repeatedly interrupted mission")
                    {
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 7401,
                        StartedUtc = DateTime.UtcNow.AddSeconds(-5)
                    }).ConfigureAwait(false);
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        ArmadaEvent prior = new ArmadaEvent("mission.interrupted_redispatched", "prior re-dispatch " + attempt);
                        prior.MissionId = mission.Id;
                        prior.VoyageId = voyage.Id;
                        await db.Events.CreateAsync(prior).ConfigureAwait(false);
                    }
                    Captain captain = new Captain("repeatedly-interrupted-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7401;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await service.HandleProcessExitAsync(7401, -1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "an interruption past the default budget of two re-dispatches fails the mission");
                    AssertEqual(VoyageStatusEnum.Cancelled, updatedVoyage!.Status, "the exhausted mission halts the voyage like any other failure");
                    AssertEqual(2, events.Count(e => e.EventType == "mission.interrupted_redispatched"), "no further re-dispatch is emitted once the budget is spent");
                }
            });

            await RunTest("HealthCheckAsync VanishedProcess IsNotTreatedAsAnInterruption", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Vanished process voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("Vanished process mission")
                    {
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 99999998,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-5)
                    }).ConfigureAwait(false);
                    Captain captain = new Captain("vanished-process-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 99999998;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    // The health check reports a process it cannot find as -1. That value is not an exit code
                    // the runtime reported: the process may have completed normally after its exit record was
                    // pruned, so re-running the mission could repeat finished work.
                    await service.HealthCheckAsync().ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);

                    AssertFalse(events.Any(e => e.EventType == "mission.interrupted_redispatched"), "a vanished process is never re-dispatched as an interruption");
                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "a vanished process keeps the loud failure path");
                }
            });

            await RunTest("HandleProcessExitAsync QuarantinesAfterDistinctGenericCrashes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CrashLoopDetection.FailureThreshold = 2;
                    settings.CrashLoopDetection.WindowMinutes = 10;
                    settings.CrashLoopDetection.CooldownSeconds = 30;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Captain captain = new Captain("crash-loop-captain") { State = CaptainStateEnum.Working };
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    for (int index = 0; index < 2; index++)
                    {
                        Voyage voyage = new Voyage("Crash loop voyage " + index) { Status = VoyageStatusEnum.InProgress };
                        await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        Mission mission = new Mission("Generic runtime crash " + index)
                        {
                            VoyageId = voyage.Id,
                            Status = MissionStatusEnum.InProgress,
                            AssignmentState = MissionAssignmentStateEnum.Assigned,
                            ProcessId = 9100 + index,
                            StartedUtc = DateTime.UtcNow.AddSeconds(-5)
                        };
                        await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                        captain.CurrentMissionId = mission.Id;
                        captain.ProcessId = mission.ProcessId;
                        captain.State = CaptainStateEnum.Working;
                        await db.Captains.UpdateAsync(captain).ConfigureAwait(false);

                        await service.HandleProcessExitAsync(mission.ProcessId!.Value, 139, captain.Id, mission.Id).ConfigureAwait(false);
                        if (index == 0)
                        {
                            // The lifecycle callback can race a health observation. A duplicate
                            // callback for the same process must not create a second crash sample.
                            await service.HandleProcessExitAsync(mission.ProcessId!.Value, 139, captain.Id, mission.Id).ConfigureAwait(false);
                            Captain? duplicateAfter = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                            AssertNotEqual(CaptainStateEnum.Quarantined, duplicateAfter!.State, "A duplicate callback must not reach the crash threshold.");
                        }
                        captain = (await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    }

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "Repeated generic runtime crashes must quarantine the captain.");
                    AssertContains("Crash loop detected", after.QuarantineReason ?? String.Empty, "Quarantine must identify crash-loop protection.");
                    AssertTrue(after.QuarantineUntilUtc.HasValue, "Crash-loop quarantine must have a bounded cooldown.");
                }
            });

            await RunTest("HandleProcessExitAsync ExcludesProviderFailuresBeforeGenericCrashFromCrashLoop", async () =>
            {
                CrashLoopExcludedFailureCase[] cases = new CrashLoopExcludedFailureCase[]
                {
                    new CrashLoopExcludedFailureCase(
                        "quota",
                        "[stderr] You've hit your usage limit. try again at 11:57 PM\nAgent exited with code 1",
                        benchesCaptain: true),
                    new CrashLoopExcludedFailureCase(
                        "auth",
                        "[stderr] invalid_api_key\nAgent exited with code 1",
                        benchesCaptain: true),
                    new CrashLoopExcludedFailureCase(
                        "safeguard",
                        "[stderr] Safety measures that flagged this message for a cybersecurity topic\nAgent exited with code 1",
                        benchesCaptain: false)
                };

                foreach (CrashLoopExcludedFailureCase testCase in cases)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        SqliteDatabaseDriver db = testDb.Driver;
                        ArmadaSettings settings = CreateSettings();
                        settings.CrashLoopDetection.FailureThreshold = 2;
                        settings.CrashLoopDetection.WindowMinutes = 10;
                        settings.CrashLoopDetection.CooldownSeconds = 30;
                        AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                        Captain captain = new Captain("excluded-provider-" + testCase.Name)
                        {
                            State = CaptainStateEnum.Idle
                        };
                        await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Mission providerFailure = await CreateExitMissionAsync(db, captain, 9500).ConfigureAwait(false);
                        string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                        Directory.CreateDirectory(missionLogDir);
                        await File.WriteAllTextAsync(
                            Path.Combine(missionLogDir, providerFailure.Id + ".log"),
                            testCase.LogText).ConfigureAwait(false);

                        await service.HandleProcessExitAsync(
                            providerFailure.ProcessId!.Value,
                            1,
                            captain.Id,
                            providerFailure.Id).ConfigureAwait(false);

                        Captain? providerAfter = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        if (testCase.BenchesCaptain)
                        {
                            AssertEqual(
                                CaptainStateEnum.Quarantined,
                                providerAfter!.State,
                                testCase.Name + " provider failure must use the quarantine path before reassignment.");
                        }
                        else
                        {
                            // With no captain on another runtime, the refusal rule stops the mission with the reason
                            // and releases the captain instead of benching it.
                            AssertNotEqual(
                                CaptainStateEnum.Quarantined,
                                providerAfter!.State,
                                testCase.Name + " provider failure must not bench the captain.");
                            Mission? blocked = await db.Missions.ReadAsync(providerFailure.Id).ConfigureAwait(false);
                            AssertTrue(
                                (blocked!.FailureReason ?? "").StartsWith(PolicyRefusalContinuationService.StoppedReasonPrefix, StringComparison.Ordinal),
                                testCase.Name + " provider failure must stop with the refusal reason: " + blocked.FailureReason);
                        }

                        Mission genericCrash = await CreateExitMissionAsync(db, captain, 9501).ConfigureAwait(false);
                        await service.HandleProcessExitAsync(
                            genericCrash.ProcessId!.Value,
                            139,
                            captain.Id,
                            genericCrash.Id).ConfigureAwait(false);

                        Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        AssertNotEqual(
                            CaptainStateEnum.Quarantined,
                            after!.State,
                            testCase.Name + " provider failure must not contribute to the generic crash-loop threshold.");
                    }
                }
            });

            await RunTest("HandleProcessExitAsync DisabledCrashLoopDetectionDoesNotQuarantine", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.CrashLoopDetection.Enabled = false;
                    settings.CrashLoopDetection.FailureThreshold = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, settings, new StubGitService());
                    Captain captain = new Captain("disabled-crash-loop") { State = CaptainStateEnum.Idle };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    for (int index = 0; index < 3; index++)
                    {
                        Mission mission = await CreateExitMissionAsync(testDb.Driver, captain, 9200 + index).ConfigureAwait(false);
                        await service.HandleProcessExitAsync(mission.ProcessId!.Value, 139, captain.Id, mission.Id).ConfigureAwait(false);
                        captain = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    }
                    Captain? after = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotEqual(CaptainStateEnum.Quarantined, after!.State, "Disabled crash-loop detection must never quarantine.");
                }
            });

            await RunTest("HandleProcessExitAsync OomAndInterruptionDoNotCountAsCrashLoop", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.CrashLoopDetection.FailureThreshold = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, settings, new StubGitService());
                    Captain captain = new Captain("excluded-crash-loop") { State = CaptainStateEnum.Idle };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission oom = await CreateExitMissionAsync(testDb.Driver, captain, 9300).ConfigureAwait(false);
                    await service.HandleProcessExitAsync(9300, 137, captain.Id, oom.Id).ConfigureAwait(false);
                    captain = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    Mission interrupted = await CreateExitMissionAsync(testDb.Driver, captain, 9301).ConfigureAwait(false);
                    try
                    {
                        await service.HandleProcessExitAsync(9301, -1, captain.Id, interrupted.Id, new CancellationToken(true)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation before failure handling must not create crash-loop evidence.
                    }
                    captain = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    Mission generic = await CreateExitMissionAsync(testDb.Driver, captain, 9302).ConfigureAwait(false);
                    await service.HandleProcessExitAsync(9302, 139, captain.Id, generic.Id).ConfigureAwait(false);
                    Captain? after = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotEqual(CaptainStateEnum.Quarantined, after!.State, "OOM and interruption must not contribute to crash-loop threshold.");
                }
            });
        }
    }
}
