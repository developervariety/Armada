namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
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
    /// Covers captain quarantine when mission execution fails with a provider credit,
    /// billing, payment, or authentication signal.
    /// </summary>
    public sealed class MissionFailureCreditQuarantineTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Failure Credit Quarantine";

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
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_credit_quarantine_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_credit_quarantine_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_credit_quarantine_logs_" + id);
            settings.CaptainQuarantine.DefaultBackoffSeconds = 120;
            settings.MinIdleCaptains = 0;
            return settings;
        }

        private static AdmiralService CreateAdmiralService(
            SqliteDatabaseDriver database,
            ArmadaSettings settings,
            ICaptainQuarantineService quarantine)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, database, settings, git);
            CaptainService captains = new CaptainService(logging, database, settings, git, docks);
            MissionService missions = new MissionService(
                logging,
                database,
                settings,
                docks,
                captains,
                captainQuarantine: quarantine);
            IVoyageService voyages = new VoyageService(logging, database);
            return new AdmiralService(
                logging,
                database,
                settings,
                captains,
                missions,
                voyages,
                docks,
                captainQuarantine: quarantine);
        }

        private static MissionService CreateMissionService(
            SqliteDatabaseDriver database,
            ArmadaSettings settings,
            ICaptainQuarantineService quarantine)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, database, settings, git);
            CaptainService captains = new CaptainService(logging, database, settings, git, docks);
            return new MissionService(
                logging,
                database,
                settings,
                docks,
                captains,
                captainQuarantine: quarantine);
        }

        private static async Task WriteMissionLogAsync(ArmadaSettings settings, string missionId, string failureLine)
        {
            string missionLogDirectory = Path.Combine(settings.LogDirectory, "missions");
            Directory.CreateDirectory(missionLogDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(missionLogDirectory, missionId + ".log"),
                failureLine + Environment.NewLine + "Agent exited with code 1").ConfigureAwait(false);
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("HandleProcessExit_InsufficientCreditFailure_QuarantinesCaptainAndRetainsFailure", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, settings, logging);
                    AdmiralService admiral = CreateAdmiralService(database, settings, quarantine);

                    Mission mission = new Mission("Credit failure mission");
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 8101;
                    mission.StartedUtc = DateTime.UtcNow.AddMinutes(-2);
                    mission = await database.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("credit-failure-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 8101;
                    captain = await database.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await WriteMissionLogAsync(
                        settings,
                        mission.Id,
                        "[stderr] Billing error: insufficient credits. try again at 11:57 PM").ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(8101, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? missionAfter = await database.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? captainAfter = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(missionAfter, "Mission should remain persisted after process-exit failure handling.");
                    AssertEqual(MissionStatusEnum.Failed, missionAfter!.Status,
                        "An insufficient-credit process exit should remain a terminal mission failure.");
                    AssertTrue(missionAfter.FailureReason!.Contains("insufficient credits", StringComparison.OrdinalIgnoreCase),
                        "The raw runtime failure should remain visible on the failed mission.");
                    AssertNotNull(captainAfter, "Assigned captain should remain persisted after quarantine.");
                    AssertEqual(CaptainStateEnum.Quarantined, captainAfter!.State,
                        "An insufficient-credit process exit should quarantine the assigned captain.");
                    AssertTrue(captainAfter.QuarantineReason!.Contains("credit, billing, payment, or authentication", StringComparison.OrdinalIgnoreCase),
                        "The quarantine should carry a provider-agnostic operator-visible reason.");
                    AssertNotNull(captainAfter.QuarantineUntilUtc,
                        "A published retry time should be retained as a quarantine deadline.");
                    AssertEqual(23, captainAfter.QuarantineUntilUtc!.Value.Hour,
                        "The published retry hour should drive quarantine duration.");
                    AssertEqual(57, captainAfter.QuarantineUntilUtc.Value.Minute,
                        "The published retry minute should drive quarantine duration.");
                    AssertNull(captainAfter.CurrentMissionId,
                        "Quarantine should clear the failed mission assignment.");
                }
            }).ConfigureAwait(false);

            await RunTest("HandleProcessExit_AuthenticationFailure_RequeuesAndQuarantinesCaptain", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, settings, logging);
                    AdmiralService admiral = CreateAdmiralService(database, settings, quarantine);

                    Mission mission = new Mission("Authentication retry mission");
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 8102;
                    mission.StartedUtc = DateTime.UtcNow.AddMinutes(-2);
                    mission = await database.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("authentication-failure-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 8102;
                    captain = await database.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await WriteMissionLogAsync(
                        settings,
                        mission.Id,
                        "[stderr] authentication failed: unauthorized. try again at 11:57 PM").ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(8102, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? missionAfter = await database.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? captainAfter = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(missionAfter, "Requeued mission should remain persisted.");
                    AssertEqual(MissionStatusEnum.Pending, missionAfter!.Status,
                        "An authentication runtime failure should retain the existing transient requeue behavior.");
                    AssertTrue(missionAfter.FailureReason!.Contains("authentication failed", StringComparison.OrdinalIgnoreCase),
                        "The requeued mission should retain the authentication failure for operators.");
                    AssertEqual(CaptainStateEnum.Quarantined, captainAfter!.State,
                        "The captain from an authentication requeue should be quarantined instead of stalled.");
                    AssertNotNull(captainAfter.QuarantineUntilUtc,
                        "Authentication quarantine should retain the published retry window.");
                    AssertNull(captainAfter.CurrentMissionId,
                        "Authentication quarantine should clear the mission assignment.");
                }
            }).ConfigureAwait(false);

            await RunTest("HandleProcessExit_OrdinaryBuildFailure_ReleasesCaptainToIdle", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, settings, logging);
                    AdmiralService admiral = CreateAdmiralService(database, settings, quarantine);

                    Mission mission = new Mission("Ordinary build failure mission");
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 8103;
                    mission.StartedUtc = DateTime.UtcNow.AddMinutes(-2);
                    mission = await database.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Captain captain = new Captain("ordinary-build-failure-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 8103;
                    captain = await database.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await WriteMissionLogAsync(
                        settings,
                        mission.Id,
                        "[stderr] Build failed with three unit-test errors").ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(8103, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? missionAfter = await database.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? captainAfter = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, missionAfter!.Status,
                        "An ordinary build failure should remain a normal terminal failure.");
                    AssertTrue(missionAfter.FailureReason!.Contains("Build failed", StringComparison.OrdinalIgnoreCase),
                        "The ordinary build failure should remain visible on the mission.");
                    AssertEqual(CaptainStateEnum.Idle, captainAfter!.State,
                        "An ordinary build failure should follow the existing captain release path.");
                    AssertNull(captainAfter.QuarantineReason,
                        "An ordinary build failure should not record a quarantine reason.");
                    AssertNull(captainAfter.QuarantineUntilUtc,
                        "An ordinary build failure should not record a quarantine window.");
                    AssertNull(captainAfter.CurrentMissionId,
                        "Normal release should clear the failed mission assignment.");
                }
            }).ConfigureAwait(false);

            await RunTest("HandleCompletion_LandingPersistsCreditFailure_QuarantineSurvivesReleaseSeam", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, settings, logging);
                    MissionService missions = CreateMissionService(database, settings, quarantine);

                    Vessel vessel = new Vessel(
                        "completion-credit-vessel-" + Guid.NewGuid().ToString("N"),
                        "https://github.com/test/completion-credit.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                    vessel.DefaultBranch = "main";
                    vessel = await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("completion-credit-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.ProcessId = 8104;
                    captain = await database.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(settings.DocksDirectory, "completion-credit-dock");
                    dock.BranchName = "armada/test/completion-credit";
                    dock.Active = true;
                    dock = await database.Docks.CreateAsync(dock).ConfigureAwait(false);

                    Mission mission = new Mission("Completion credit failure mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.CaptainId = captain.Id;
                    mission.DockId = dock.Id;
                    mission.ProcessId = 8104;
                    mission = await database.Missions.CreateAsync(mission).ConfigureAwait(false);

                    captain.CurrentMissionId = mission.Id;
                    captain.CurrentDockId = dock.Id;
                    await database.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    missions.OnMissionComplete = async (completedMission, completedDock) =>
                    {
                        completedMission.Status = MissionStatusEnum.Failed;
                        completedMission.FailureReason =
                            "Billing error: insufficient credits. try again at 11:57 PM";
                        completedMission.CompletedUtc = DateTime.UtcNow;
                        completedMission.LastUpdateUtc = DateTime.UtcNow;
                        await database.Missions.UpdateAsync(completedMission).ConfigureAwait(false);
                    };

                    await missions.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                    Mission? missionAfter = await database.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? captainAfter = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Dock? dockAfter = await database.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, missionAfter!.Status,
                        "The landing callback's credit failure should remain persisted.");
                    AssertEqual(CaptainStateEnum.Quarantined, captainAfter!.State,
                        "Completion cleanup must quarantine rather than release the assigned captain to Idle.");
                    AssertNotNull(captainAfter.QuarantineReason,
                        "Completion quarantine should persist an operator-visible reason.");
                    AssertNotNull(captainAfter.QuarantineUntilUtc,
                        "Completion quarantine should persist the published retry window.");
                    AssertNull(captainAfter.CurrentMissionId,
                        "Completion quarantine should clear the mission assignment.");
                    AssertNotNull(dockAfter, "Completion cleanup should retain the dock audit row.");
                    AssertFalse(dockAfter!.Active,
                        "The dock should be reclaimed before the captain is quarantined.");
                }
            }).ConfigureAwait(false);
        }
    }
}
