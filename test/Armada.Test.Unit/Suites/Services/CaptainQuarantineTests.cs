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

                    Captain quarantined = new Captain("quota-worker");
                    quarantined.State = CaptainStateEnum.Idle;
                    quarantined.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    quarantined.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(quarantined).ConfigureAwait(false);

                    Captain healthy = new Captain("healthy-worker");
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
        }
    }
}
