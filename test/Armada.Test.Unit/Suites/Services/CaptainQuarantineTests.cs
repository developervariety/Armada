namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>Tests for captain quota quarantine routing and auto-restore.</summary>
    public sealed class CaptainQuarantineTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Quarantine";

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_quarantine_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_quarantine_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_quarantine_logs_" + id);
            settings.CaptainQuarantine.DefaultBackoffSeconds = 120;
            return settings;
        }

        private static MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings, ICaptainQuarantineService quarantine)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64001);
            return new MissionService(logging, db, settings, dockService, captainService, captainQuarantine: quarantine);
        }

        private static AdmiralService CreateAdmiralService(SqliteDatabaseDriver db, ArmadaSettings settings, ICaptainQuarantineService quarantine)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            MissionService missionService = new MissionService(logging, db, settings, dockService, captainService, captainQuarantine: quarantine);
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService, captainQuarantine: quarantine);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TryAssign_QuarantinedCaptain_SkipsToHealthyCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Vessel vessel = new Vessel("quarantine-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/quarantine.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.DefaultBranch = "main";
                    await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // Name the quarantined captain so it sorts FIRST in the SQLite idle enumeration
                    // (ORDER BY name). If the quarantine filter were removed, selection would land on
                    // this captain and the assertions below would fail -- this gives the test teeth.
                    Captain quarantined = new Captain("aaa-quota-worker");
                    quarantined.State = CaptainStateEnum.Idle;
                    quarantined.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    quarantined.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(quarantined).ConfigureAwait(false);

                    Captain healthy = new Captain("zzz-healthy-worker");
                    healthy.State = CaptainStateEnum.Idle;
                    await db.Captains.CreateAsync(healthy).ConfigureAwait(false);

                    Mission mission = new Mission("Assign away from quarantine", "Route to healthy captain.");
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.Pending;
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "mission should assign to the healthy captain");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(healthy.Id, updated!.CaptainId, "healthy captain should be selected");
                    AssertNotEqual(quarantined.Id, updated.CaptainId, "quarantined captain must not be assigned");
                }
            });

            await RunTest("TryAssign_OnlyCaptainQuarantined_DoesNotAssign", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Vessel vessel = new Vessel("quarantine-only-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/quarantine.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.DefaultBranch = "main";
                    await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // The ONLY idle captain is quarantined. This is the strongest regression teeth:
                    // if the IsQuarantined filter in FindAvailableCaptainAsync were removed, this captain
                    // would be selected and the mission would assign -- failing the assertions below.
                    Captain quarantined = new Captain("lonely-quota-worker");
                    quarantined.State = CaptainStateEnum.Idle;
                    quarantined.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    quarantined.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(quarantined).ConfigureAwait(false);

                    Mission mission = new Mission("No healthy captain", "Quarantined captain must be skipped.");
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.Pending;
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertFalse(assigned, "no assignment is possible when the only idle captain is quarantined");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNull(updated!.CaptainId, "quarantined captain must not be assigned even when it is the only candidate");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForIdleCaptain, updated.AssignmentState, "mission should wait for an idle captain");
                }
            });

            await RunTest("HandleProcessExitAsync_QuotaFailureOnSecondCaptain_DoesNotCancelUnrelatedVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);
                    AdmiralService service = CreateAdmiralService(db, settings, quarantine);

                    Voyage unrelatedVoyage = new Voyage("Unrelated Voyage");
                    unrelatedVoyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(unrelatedVoyage).ConfigureAwait(false);

                    Mission unrelatedMission = new Mission("Unrelated Worker");
                    unrelatedMission.VoyageId = unrelatedVoyage.Id;
                    unrelatedMission.Status = MissionStatusEnum.InProgress;
                    unrelatedMission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    unrelatedMission.ProcessId = 1111;
                    await db.Missions.CreateAsync(unrelatedMission).ConfigureAwait(false);

                    Captain healthyCaptain = new Captain("healthy-captain");
                    healthyCaptain.State = CaptainStateEnum.Working;
                    healthyCaptain.CurrentMissionId = unrelatedMission.Id;
                    healthyCaptain.ProcessId = 1111;
                    await db.Captains.CreateAsync(healthyCaptain).ConfigureAwait(false);

                    Voyage quotaVoyage = new Voyage("Quota Voyage");
                    quotaVoyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(quotaVoyage).ConfigureAwait(false);

                    Mission quotaMission = new Mission("Quota Worker");
                    quotaMission.VoyageId = quotaVoyage.Id;
                    quotaMission.Status = MissionStatusEnum.InProgress;
                    quotaMission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    quotaMission.ProcessId = 2222;
                    await db.Missions.CreateAsync(quotaMission).ConfigureAwait(false);

                    Captain quotaCaptain = new Captain("quota-captain");
                    quotaCaptain.State = CaptainStateEnum.Working;
                    quotaCaptain.CurrentMissionId = quotaMission.Id;
                    quotaCaptain.ProcessId = 2222;
                    await db.Captains.CreateAsync(quotaCaptain).ConfigureAwait(false);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, quotaMission.Id + ".log"),
                        "[stderr] You've hit your usage limit. try again at 11:57 PM\nAgent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(2222, 1, quotaCaptain.Id, quotaMission.Id).ConfigureAwait(false);

                    Voyage? unrelatedAfter = await db.Voyages.ReadAsync(unrelatedVoyage.Id).ConfigureAwait(false);
                    Mission? unrelatedMissionAfter = await db.Missions.ReadAsync(unrelatedMission.Id).ConfigureAwait(false);
                    Captain? quotaCaptainAfter = await db.Captains.ReadAsync(quotaCaptain.Id).ConfigureAwait(false);

                    AssertEqual(VoyageStatusEnum.InProgress, unrelatedAfter!.Status, "unrelated voyage must keep running");
                    AssertEqual(MissionStatusEnum.InProgress, unrelatedMissionAfter!.Status, "unrelated mission must not be cancelled");
                    AssertEqual(CaptainStateEnum.Quarantined, quotaCaptainAfter!.State, "quota captain should be quarantined");
                    AssertNotNull(quotaCaptainAfter.QuarantineUntilUtc, "quota captain should have retry deadline");
                }
            });

            await RunTest("HandleProcessExitAsync_CodexUsageLimitStderr_QuarantinesAndHonorsResetDeadline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);
                    AdmiralService service = CreateAdmiralService(db, settings, quarantine);

                    Voyage voyage = new Voyage("Codex Quota Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Codex Worker");
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 7373;
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain codexCaptain = new Captain("codex-captain");
                    codexCaptain.State = CaptainStateEnum.Working;
                    codexCaptain.CurrentMissionId = mission.Id;
                    codexCaptain.ProcessId = 7373;
                    await db.Captains.CreateAsync(codexCaptain).ConfigureAwait(false);

                    // Real codex signature: process exits code 1 within seconds, leaving the ChatGPT
                    // usage-limit stderr (with a published reset time) as the last meaningful log line.
                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] You've hit your usage limit. Upgrade to Pro or try again at 11:57 PM.\nAgent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(7373, 1, codexCaptain.Id, mission.Id).ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(codexCaptain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "codex usage-limit exit-code-1 should quarantine the captain");
                    AssertNotNull(after.QuarantineUntilUtc, "quarantine should carry a retry deadline");
                    // The provider-published reset time (11:57 PM) must drive the deadline -- not the
                    // configured default backoff. Hour/Minute prove TryParseRetryAfterUtc was wired through.
                    AssertEqual(23, after.QuarantineUntilUtc!.Value.Hour, "published reset hour should drive the deadline");
                    AssertEqual(57, after.QuarantineUntilUtc.Value.Minute, "published reset minute should drive the deadline");
                    AssertNull(after.CurrentMissionId, "quarantine should clear the current mission assignment");
                }
            });

            await RunTest("RestoreExpiredQuarantines_AfterRetryWindow_ClearsQuarantine", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("restore-me");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddSeconds(-5);
                    captain.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? restored = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, restored!.State, "expired quarantine should restore captain to Idle");
                    AssertNull(restored.QuarantineUntilUtc, "retry deadline should be cleared");
                    AssertNull(restored.QuarantineReason, "quarantine reason should be cleared");
                }
            });

            await RunTest("RestoreExpiredQuarantines_WindowStillOpen_LeavesCaptainQuarantined", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("still-cooling-down");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    captain.QuarantineReason = "rate limit reached";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "captain inside the retry window must stay quarantined");
                    AssertNotNull(after.QuarantineUntilUtc, "retry deadline should be retained while window is open");
                }
            });

            await RunTest("RestoreExpiredQuarantines_NullDeadline_RestoresImmediately", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("no-deadline");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = null;
                    captain.QuarantineReason = "quota";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, after!.State, "quarantine with no deadline should be treated as expired");
                    AssertNull(after.QuarantineReason, "quarantine reason should be cleared");
                }
            });

            await RunTest("IsQuarantined_StateAndDeadlineVariants_ReportsCorrectly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain stateQuarantined = new Captain("state-flag");
                    stateQuarantined.State = CaptainStateEnum.Quarantined;
                    AssertTrue(quarantine.IsQuarantined(stateQuarantined), "Quarantined state alone should report quarantined");

                    Captain futureDeadline = new Captain("future-window");
                    futureDeadline.State = CaptainStateEnum.Idle;
                    futureDeadline.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(10);
                    AssertTrue(quarantine.IsQuarantined(futureDeadline), "future deadline should report quarantined even when Idle");

                    Captain expiredDeadline = new Captain("expired-window");
                    expiredDeadline.State = CaptainStateEnum.Idle;
                    expiredDeadline.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(-10);
                    AssertFalse(quarantine.IsQuarantined(expiredDeadline), "past deadline on an Idle captain should not report quarantined");

                    Captain clean = new Captain("clean");
                    clean.State = CaptainStateEnum.Idle;
                    AssertFalse(quarantine.IsQuarantined(clean), "idle captain with no deadline should not report quarantined");

                    AssertThrows<ArgumentNullException>(() => quarantine.IsQuarantined(null!), "null captain should throw");
                    await Task.CompletedTask;
                }
            });

            await RunTest("QuarantineAsync_UsesProviderDeadlineAndClearsAssignment", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("busy-worker");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = "msn_test";
                    captain.CurrentDockId = "dck_test";
                    captain.ProcessId = 4242;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    DateTime retryAfterUtc = DateTime.UtcNow.AddMinutes(45);
                    await quarantine.QuarantineAsync(captain, "  You've hit your usage limit  ", retryAfterUtc).ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "captain should be persisted as Quarantined");
                    AssertNotNull(after.QuarantineUntilUtc, "provider deadline should be persisted");
                    AssertTrue(
                        Math.Abs((after.QuarantineUntilUtc!.Value - retryAfterUtc).TotalSeconds) < 2,
                        "provider-published retry deadline should be honored");
                    AssertEqual("You've hit your usage limit", after.QuarantineReason, "reason should be trimmed and persisted");
                    AssertNull(after.CurrentMissionId, "current mission should be cleared on quarantine");
                    AssertNull(after.CurrentDockId, "current dock should be cleared on quarantine");
                    AssertNull(after.ProcessId, "process id should be cleared on quarantine");
                }
            });

            await RunTest("QuarantineAsync_NoDeadline_FallsBackToConfiguredBackoff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainQuarantine.DefaultBackoffSeconds = 120;
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("no-provider-deadline");
                    captain.State = CaptainStateEnum.Working;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    DateTime before = DateTime.UtcNow;
                    await quarantine.QuarantineAsync(captain, "quota exceeded", null).ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(after!.QuarantineUntilUtc, "default backoff deadline should be set");
                    double seconds = (after.QuarantineUntilUtc!.Value - before).TotalSeconds;
                    AssertTrue(seconds >= 110 && seconds <= 140, "deadline should fall ~DefaultBackoffSeconds in the future (was " + seconds + "s)");
                }
            });

            await RunTest("QuarantineAsync_PastDeadline_FallsBackToConfiguredBackoff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainQuarantine.DefaultBackoffSeconds = 120;
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("stale-deadline");
                    captain.State = CaptainStateEnum.Working;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    DateTime before = DateTime.UtcNow;
                    await quarantine.QuarantineAsync(captain, "rate limit", DateTime.UtcNow.AddMinutes(-5)).ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(after!.QuarantineUntilUtc, "deadline should be set");
                    AssertTrue(after.QuarantineUntilUtc!.Value > before, "a past provider deadline must not be persisted as-is");
                    double seconds = (after.QuarantineUntilUtc.Value - before).TotalSeconds;
                    AssertTrue(seconds >= 110 && seconds <= 140, "past deadline should fall back to default backoff (was " + seconds + "s)");
                }
            });

            await RunTest("QuarantineAsync_InvalidArguments_Throw", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("arg-check");
                    captain.State = CaptainStateEnum.Working;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await AssertThrowsAsync<ArgumentNullException>(
                        () => quarantine.QuarantineAsync(null!, "quota", null),
                        "null captain should throw");
                    await AssertThrowsAsync<ArgumentException>(
                        () => quarantine.QuarantineAsync(captain, "   ", null),
                        "whitespace reason should throw");
                    await AssertThrowsAsync<ArgumentNullException>(
                        () => quarantine.ClearQuarantineAsync(null!),
                        "null captain should throw on clear");
                }
            });

            await RunTest("ClearQuarantineAsync_ResetsStateAndFields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging);

                    Captain captain = new Captain("clear-me");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(15);
                    captain.QuarantineReason = "insufficient_quota";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await quarantine.ClearQuarantineAsync(captain).ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, after!.State, "cleared captain should return to Idle");
                    AssertNull(after.QuarantineUntilUtc, "deadline should be cleared");
                    AssertNull(after.QuarantineReason, "reason should be cleared");
                }
            });

            await RunTest("TryProbeRestore_SuccessfulProbe_RestoresQuarantinedCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    StubQuotaProbe probe = new StubQuotaProbe(true);
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging, probe);

                    // Bench window is still open: only a positive probe can restore this captain early.
                    Captain captain = new Captain("probe-recovers");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    captain.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    bool restored = await quarantine.TryProbeRestoreAsync(captain).ConfigureAwait(false);

                    AssertTrue(restored, "successful probe should report a restore");
                    AssertEqual(1, probe.CallCount, "probe should be consulted exactly once");
                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, after!.State, "probe success should restore captain to Idle before window elapses");
                    AssertNull(after.QuarantineUntilUtc, "retry deadline should be cleared on probe restore");
                    AssertNull(after.QuarantineReason, "quarantine reason should be cleared on probe restore");
                }
            });

            await RunTest("TryProbeRestore_FailedProbe_LeavesCaptainQuarantined", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    StubQuotaProbe probe = new StubQuotaProbe(false);
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging, probe);

                    Captain captain = new Captain("probe-still-limited");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    captain.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    bool restored = await quarantine.TryProbeRestoreAsync(captain).ConfigureAwait(false);

                    AssertFalse(restored, "failed probe should not restore the captain");
                    AssertEqual(1, probe.CallCount, "probe should be consulted exactly once");
                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "failed probe must keep captain benched");
                    AssertNotNull(after.QuarantineUntilUtc, "retry deadline should be retained on failed probe");
                }
            });

            await RunTest("TryProbeRestore_ProbeThrows_LeavesCaptainQuarantined", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    ThrowingQuotaProbe probe = new ThrowingQuotaProbe();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging, probe);

                    Captain captain = new Captain("probe-throws");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    captain.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    bool restored = await quarantine.TryProbeRestoreAsync(captain).ConfigureAwait(false);

                    AssertFalse(restored, "a probe that throws must not restore the captain");
                    AssertEqual(1, probe.CallCount, "probe should be consulted exactly once even when it throws");
                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "probe failure must leave the captain benched, not crash the restore loop");
                    AssertNotNull(after.QuarantineUntilUtc, "retry deadline should be retained when the probe throws");
                }
            });

            await RunTest("RestoreExpiredQuarantines_ProbeEnabledButNoProbeWired_UsesDeadlineFallback", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainQuarantine.UseProbeOnRestore = true;
                    LoggingModule logging = CreateLogging();
                    // UseProbeOnRestore is on, but no probe instance is wired -- the service must fall back
                    // to the deadline path rather than dereference a null probe.
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging, probe: null);

                    Captain expired = new Captain("expired-no-probe");
                    expired.State = CaptainStateEnum.Quarantined;
                    expired.QuarantineUntilUtc = DateTime.UtcNow.AddSeconds(-5);
                    expired.QuarantineReason = "quota";
                    await db.Captains.CreateAsync(expired).ConfigureAwait(false);

                    Captain stillOpen = new Captain("open-no-probe");
                    stillOpen.State = CaptainStateEnum.Quarantined;
                    stillOpen.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    stillOpen.QuarantineReason = "quota";
                    await db.Captains.CreateAsync(stillOpen).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? expiredAfter = await db.Captains.ReadAsync(expired.Id).ConfigureAwait(false);
                    Captain? stillOpenAfter = await db.Captains.ReadAsync(stillOpen.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, expiredAfter!.State, "expired window should restore via the deadline fallback when no probe is wired");
                    AssertEqual(CaptainStateEnum.Quarantined, stillOpenAfter!.State, "open window must stay quarantined under the deadline fallback");
                }
            });

            await RunTest("RestoreExpiredQuarantines_ProbeEnabledAndSucceeds_RestoresBeforeWindowElapses", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainQuarantine.UseProbeOnRestore = true;
                    LoggingModule logging = CreateLogging();
                    StubQuotaProbe probe = new StubQuotaProbe(true);
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging, probe);

                    Captain captain = new Captain("early-restore");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    captain.QuarantineReason = "rate limit reached";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, after!.State, "probe-enabled restore should free the captain before the window elapses");
                    AssertNull(after.QuarantineUntilUtc, "retry deadline should be cleared after early probe restore");
                }
            });

            await RunTest("RestoreExpiredQuarantines_ProbeEnabledAndFails_KeepsCaptainBenched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainQuarantine.UseProbeOnRestore = true;
                    LoggingModule logging = CreateLogging();
                    StubQuotaProbe probe = new StubQuotaProbe(false);
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, logging, probe);

                    Captain captain = new Captain("still-benched");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    captain.QuarantineReason = "rate limit reached";
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "a negative probe must keep the captain quarantined inside its window");
                    AssertNotNull(after.QuarantineUntilUtc, "retry deadline should be retained while the probe reports no recovery");
                }
            });

            await RunTest("ProviderResetQuotaProbe_HonorsPublishedDeadline", async () =>
            {
                ProviderResetQuotaProbe probe = new ProviderResetQuotaProbe();

                Captain stillLimited = new Captain("still-limited");
                stillLimited.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(10);
                AssertFalse(
                    await probe.HasRecoveredAsync(stillLimited).ConfigureAwait(false),
                    "quota should not be reported recovered before the published reset deadline");

                Captain past = new Captain("past-deadline");
                past.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(-1);
                AssertTrue(
                    await probe.HasRecoveredAsync(past).ConfigureAwait(false),
                    "quota should be reported recovered once the published reset deadline has elapsed");

                Captain noDeadline = new Captain("no-deadline");
                noDeadline.QuarantineUntilUtc = null;
                AssertTrue(
                    await probe.HasRecoveredAsync(noDeadline).ConfigureAwait(false),
                    "absent a published deadline there is nothing to wait on; treat as recovered");
            });
        }

        /// <summary>Hand-rolled quota probe double returning a fixed recovery verdict.</summary>
        private sealed class StubQuotaProbe : ICaptainQuotaProbe
        {
            internal int CallCount { get; private set; }

            private readonly bool _Recovered;

            internal StubQuotaProbe(bool recovered)
            {
                _Recovered = recovered;
            }

            public Task<bool> HasRecoveredAsync(Captain captain, CancellationToken token = default)
            {
                if (captain == null) throw new ArgumentNullException(nameof(captain));
                CallCount++;
                return Task.FromResult(_Recovered);
            }
        }

        /// <summary>Hand-rolled quota probe double that always throws to exercise the restore catch path.</summary>
        private sealed class ThrowingQuotaProbe : ICaptainQuotaProbe
        {
            internal int CallCount { get; private set; }

            public Task<bool> HasRecoveredAsync(Captain captain, CancellationToken token = default)
            {
                if (captain == null) throw new ArgumentNullException(nameof(captain));
                CallCount++;
                throw new InvalidOperationException("probe backend unavailable");
            }
        }
    }
}
