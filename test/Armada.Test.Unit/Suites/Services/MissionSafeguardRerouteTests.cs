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
    /// Covers provider-safeguard-block re-routing: when a mission's captain is refused by its provider's
    /// content/cyber safety gate, the captain is benched and the mission is requeued to a different-provider
    /// peer (the voyage stays alive) -- never cascade-cancelled. This is model-neutral (no provider name in the
    /// logic). Once re-routes are exhausted the mission fails with an operator-actionable reason instead of
    /// looping.
    /// </summary>
    public sealed class MissionSafeguardRerouteTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Safeguard Reroute";

        // The real Claude cyber-safeguard block observed on seed-key review stages; provider-neutral detection
        // must recognize it without keying on the model name.
        private const string SafeguardLine =
            "[stderr] API Error: claude-opus-5 has safety measures that flagged this message for a cybersecurity topic";

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
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_safeguard_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_safeguard_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_safeguard_logs_" + id);
            settings.MinIdleCaptains = 0;
            return settings;
        }

        private static AdmiralService CreateAdmiralService(SqliteDatabaseDriver database, ArmadaSettings settings, ICaptainQuarantineService quarantine)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, database, settings, git);
            CaptainService captains = new CaptainService(logging, database, settings, git, docks);
            MissionService missions = new MissionService(logging, database, settings, docks, captains, captainQuarantine: quarantine);
            IVoyageService voyages = new VoyageService(logging, database);
            return new AdmiralService(logging, database, settings, captains, missions, voyages, docks, captainQuarantine: quarantine);
        }

        private static async Task WriteMissionLogAsync(ArmadaSettings settings, string missionId, string failureLine)
        {
            string dir = Path.Combine(settings.LogDirectory, "missions");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, missionId + ".log"),
                failureLine + Environment.NewLine + "Agent exited with code 1").ConfigureAwait(false);
        }

        private static async Task<(Voyage voyage, Mission mission, Captain captain)> SeedAsync(
            SqliteDatabaseDriver db, int processId, int recoveryAttempts)
        {
            Vessel vessel = await db.Vessels.CreateAsync(new Vessel("safeguard-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

            Voyage voyage = new Voyage("safeguard-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("[Judge] seed-key review", "review decompiled seed-key source");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = "Judge";
            mission.Status = MissionStatusEnum.InProgress;
            mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
            mission.ProcessId = processId;
            mission.RecoveryAttempts = recoveryAttempts;
            mission.StartedUtc = DateTime.UtcNow.AddMinutes(-1);
            mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            Captain captain = new Captain("safeguard-captain");
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = mission.Id;
            captain.ProcessId = processId;
            captain = await db.Captains.CreateAsync(captain).ConfigureAwait(false);

            return (voyage, mission, captain);
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("SafeguardBlock_UnderCap_RequeuesMissionAndBenchesCaptain_VoyageStaysAlive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());
                    AdmiralService admiral = CreateAdmiralService(db, settings, quarantine);

                    (Voyage voyage, Mission mission, Captain captain) = await SeedAsync(db, 9201, recoveryAttempts: 0).ConfigureAwait(false);
                    await WriteMissionLogAsync(settings, mission.Id, SafeguardLine).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(9201, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? m = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? c = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? v = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Pending, m!.Status, "a safeguard block re-routes (requeues) the mission, not fails it");
                    AssertEqual(1, m.RecoveryAttempts, "the re-route increments the recovery attempt count");
                    AssertNull(m.CaptainId, "the requeued mission is unbound from the blocking captain");
                    AssertEqual(CaptainStateEnum.Quarantined, c!.State, "the blocking captain is benched so re-dispatch routes to a different provider");
                    AssertTrue(v!.Status != VoyageStatusEnum.Failed && v.Status != VoyageStatusEnum.Cancelled,
                        "the voyage is NOT cascade-cancelled by a provider safeguard block");
                }
            }).ConfigureAwait(false);

            await RunTest("SafeguardBlock_AtCap_FailsWithOperatorActionableReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());
                    AdmiralService admiral = CreateAdmiralService(db, settings, quarantine);

                    // RecoveryAttempts already at the re-route cap: every eligible provider has blocked.
                    (Voyage voyage, Mission mission, Captain captain) = await SeedAsync(db, 9202, recoveryAttempts: 5).ConfigureAwait(false);
                    await WriteMissionLogAsync(settings, mission.Id, SafeguardLine).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(9202, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? m = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, m!.Status, "once re-routes are exhausted the mission fails instead of looping");
                    AssertTrue(m.FailureReason!.Contains("operator routing decision is required", StringComparison.OrdinalIgnoreCase),
                        "the failure surfaces an operator-actionable routing decision, not a generic error");
                }
            }).ConfigureAwait(false);
        }
    }
}
