namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
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
    /// Covers a provider safeguard block that ends the captain process. It follows the one refusal rule: the block
    /// is recorded, the mission gets at most one continuation on an approved captain of a different runtime, and
    /// it is never retried on the runtime that blocked. A second block, or no alternate runtime, fails the mission
    /// with the reason. Detection is model-neutral (no provider name in the logic).
    /// </summary>
    public sealed class MissionSafeguardRerouteTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Safeguard Reroute";

        // Provider safeguard wording as a captain log records it; detection must not key on the model name.
        private const string SafeguardLine =
            "[stderr] API Error: example-model has safety measures that flagged this message for a cybersecurity topic";

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
            MissionService missions = new MissionService(logging, database, settings, docks, captains, captainQuarantine: quarantine, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
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

        private static async Task<Captain> CreateCaptainAsync(SqliteDatabaseDriver db, string name, AgentRuntimeEnum runtime)
        {
            Captain captain = new Captain(name);
            captain.Runtime = runtime;
            captain.State = CaptainStateEnum.Idle;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static async Task<(Voyage voyage, Mission mission)> SeedMissionAsync(SqliteDatabaseDriver db)
        {
            Vessel vessel = await db.Vessels.CreateAsync(new Vessel("safeguard-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

            Voyage voyage = new Voyage("safeguard-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("[Judge] format review", "review the ExampleFormat reader");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = "Judge";
            mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);
            return (voyage, mission);
        }

        // Puts a mission to work on a captain with a live process, the state a process exit is reported against.
        private static async Task<Mission> RunOnAsync(SqliteDatabaseDriver db, Mission mission, Captain captain, int processId)
        {
            Mission current = (await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
            current.Status = MissionStatusEnum.InProgress;
            current.AssignmentState = MissionAssignmentStateEnum.Assigned;
            current.CaptainId = captain.Id;
            current.ProcessId = processId;
            current.StartedUtc = DateTime.UtcNow.AddMinutes(-1);
            current = await db.Missions.UpdateAsync(current).ConfigureAwait(false);

            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = current.Id;
            captain.ProcessId = processId;
            await db.Captains.UpdateAsync(captain).ConfigureAwait(false);
            return current;
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("SafeguardBlock_ContinuesOnceOnAnAlternateRuntime_VoyageStaysAlive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());
                    AdmiralService admiral = CreateAdmiralService(db, settings, quarantine);

                    Captain blocked = await CreateCaptainAsync(db, "blocked-captain", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                    Captain sameRuntime = await CreateCaptainAsync(db, "same-runtime-captain", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                    Captain alternate = await CreateCaptainAsync(db, "alternate-captain", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    (Voyage voyage, Mission mission) = await SeedMissionAsync(db).ConfigureAwait(false);
                    await RunOnAsync(db, mission, blocked, 9201).ConfigureAwait(false);
                    await WriteMissionLogAsync(settings, mission.Id, SafeguardLine).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(9201, 1, blocked.Id, mission.Id).ConfigureAwait(false);

                    Mission? m = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? c = await db.Captains.ReadAsync(blocked.Id).ConfigureAwait(false);
                    Voyage? v = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Pending, m!.Status, "a safeguard block continues the mission, not fails it");
                    AssertNull(m.CaptainId, "the continuation is unbound from the blocking captain");
                    AssertTrue(PolicyRefusalContinuationService.IsContinuation(m), "the mission is marked as a refusal continuation");
                    AssertTrue(MissionService.IsExcludedForAssignment(m, blocked), "the blocking captain is excluded");
                    AssertTrue(MissionService.IsExcludedForAssignment(m, sameRuntime), "every captain on the blocking runtime is excluded");
                    AssertFalse(MissionService.IsExcludedForAssignment(m, alternate), "the alternate-runtime captain stays eligible");
                    AssertTrue(c!.State != CaptainStateEnum.Quarantined, "the blocking captain is not benched for unrelated missions");
                    AssertTrue(v!.Status != VoyageStatusEnum.Failed && v.Status != VoyageStatusEnum.Cancelled,
                        "the voyage is NOT cascade-cancelled by a provider safeguard block");

                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 50).ConfigureAwait(false);
                    AssertEqual(1, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.ContinuedEventType), "one continuation is recorded");
                    AssertContains("ProviderSafeguardBlock", events.First(evt => evt.EventType == PolicyRefusalContinuationService.RefusalEventType).Payload ?? "",
                        "the block is recorded with its typed kind");
                }
            }).ConfigureAwait(false);

            await RunTest("SafeguardBlock_AgainAfterTheContinuation_FailsWithTheReasonInsteadOfRetrying", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());
                    AdmiralService admiral = CreateAdmiralService(db, settings, quarantine);

                    Captain blocked = await CreateCaptainAsync(db, "blocked-captain", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                    Captain alternate = await CreateCaptainAsync(db, "alternate-captain", AgentRuntimeEnum.Codex).ConfigureAwait(false);
                    (Voyage voyage, Mission mission) = await SeedMissionAsync(db).ConfigureAwait(false);
                    await RunOnAsync(db, mission, blocked, 9202).ConfigureAwait(false);
                    await WriteMissionLogAsync(settings, mission.Id, SafeguardLine).ConfigureAwait(false);
                    await admiral.HandleProcessExitAsync(9202, 1, blocked.Id, mission.Id).ConfigureAwait(false);

                    await RunOnAsync(db, mission, alternate, 9203).ConfigureAwait(false);
                    await admiral.HandleProcessExitAsync(9203, 1, alternate.Id, mission.Id).ConfigureAwait(false);

                    Mission? m = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, m!.Status, "a second block after the continuation fails the mission");
                    AssertTrue((m.FailureReason ?? "").StartsWith(PolicyRefusalContinuationService.StoppedReasonPrefix, StringComparison.Ordinal),
                        "the failure carries the refusal reason: " + m.FailureReason);

                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 50).ConfigureAwait(false);
                    AssertEqual(1, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.ContinuedEventType), "there is never a second continuation");
                }
            }).ConfigureAwait(false);

            await RunTest("SafeguardBlock_WithNoApprovedAlternateRuntime_FailsWithoutRetrying", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());
                    AdmiralService admiral = CreateAdmiralService(db, settings, quarantine);

                    Captain blocked = await CreateCaptainAsync(db, "blocked-captain", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                    await CreateCaptainAsync(db, "same-runtime-captain", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                    (Voyage voyage, Mission mission) = await SeedMissionAsync(db).ConfigureAwait(false);
                    await RunOnAsync(db, mission, blocked, 9204).ConfigureAwait(false);
                    await WriteMissionLogAsync(settings, mission.Id, SafeguardLine).ConfigureAwait(false);

                    await admiral.HandleProcessExitAsync(9204, 1, blocked.Id, mission.Id).ConfigureAwait(false);

                    Mission? m = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, m!.Status, "with no alternate runtime the blocked work is not retried");
                    AssertContains("No approved captain on a runtime other than ClaudeCode", m.FailureReason ?? "", "the failure names why no continuation ran");
                }
            }).ConfigureAwait(false);
        }
    }
}
