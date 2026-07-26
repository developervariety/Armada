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
    /// Covers provider quota/credit/balance re-routing: when a mission's captain fails on a usage limit or an
    /// "insufficient balance" billing error, the captain is benched and the mission is requeued to a compatible
    /// peer with remaining quota (the voyage stays alive) -- never cascade-cancelled. Once every compatible
    /// captain has hit the wall, the mission fails with an operator-actionable reason instead of looping.
    /// </summary>
    public sealed class MissionQuotaRerouteTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Quota Reroute";

        // A real opencode "insufficient balance" billing failure -- must be treated as a recoverable quota/credit
        // limit (re-route), not a cascade-cancel.
        private const string BalanceLine =
            "[stderr] {\"type\":\"error\",\"error\":{\"name\":\"APIError\",\"data\":{\"message\":\"Insufficient balance. Manage your billing here: https://opencode.ai/workspace/x/billing\"}}}";

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
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_quota_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_quota_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_quota_logs_" + id);
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
            Vessel vessel = await db.Vessels.CreateAsync(new Vessel("quota-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

            Voyage voyage = new Voyage("quota-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("[Worker] port signer", "port a seed-key signer");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.InProgress;
            mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
            mission.ProcessId = processId;
            mission.RecoveryAttempts = recoveryAttempts;
            mission.StartedUtc = DateTime.UtcNow.AddMinutes(-1);
            mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            Captain captain = new Captain("opencode-glm52-x");
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = mission.Id;
            captain.ProcessId = processId;
            captain = await db.Captains.CreateAsync(captain).ConfigureAwait(false);

            return (voyage, mission, captain);
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BalanceFailure_UnderCap_RequeuesMissionAndBenchesCaptain_VoyageStaysAlive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());
                    AdmiralService admiral = CreateAdmiralService(db, settings, quarantine);

                    (Voyage voyage, Mission mission, Captain captain) = await SeedAsync(db, 9301, recoveryAttempts: 0).ConfigureAwait(false);
                    await WriteMissionLogAsync(settings, mission.Id, BalanceLine).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(9301, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? m = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? c = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? v = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Pending, m!.Status, "an insufficient-balance failure re-routes (requeues) the mission, not fails it");
                    AssertEqual(1, m.RecoveryAttempts, "the re-route increments the recovery attempt count");
                    AssertNull(m.CaptainId, "the requeued mission is unbound from the out-of-balance captain");
                    AssertEqual(CaptainStateEnum.Quarantined, c!.State, "the out-of-balance captain is benched so re-dispatch routes to a peer with quota");
                    AssertTrue(v!.Status != VoyageStatusEnum.Failed && v.Status != VoyageStatusEnum.Cancelled,
                        "the voyage is NOT cascade-cancelled by a captain running out of balance");
                }
            }).ConfigureAwait(false);

            await RunTest("BalanceFailure_AtCap_FailsWithOperatorActionableReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());
                    AdmiralService admiral = CreateAdmiralService(db, settings, quarantine);

                    (Voyage voyage, Mission mission, Captain captain) = await SeedAsync(db, 9302, recoveryAttempts: 5).ConfigureAwait(false);
                    await WriteMissionLogAsync(settings, mission.Id, BalanceLine).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(9302, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? m = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, m!.Status, "once re-routes are exhausted the mission fails instead of looping");
                    AssertTrue(m.FailureReason!.Contains("top up balance", StringComparison.OrdinalIgnoreCase)
                        || m.FailureReason.Contains("remaining quota", StringComparison.OrdinalIgnoreCase),
                        "the failure surfaces an operator-actionable quota/balance action, not a generic error");
                }
            }).ConfigureAwait(false);
        }
    }
}
