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

    /// <summary>
    /// Covers the lazy pipeline-handoff self-heal in <see cref="MissionService.TryAssignAsync"/>:
    /// a same-vessel dependent whose handoff-eligible upstream never propagated its branch (the
    /// rescue creation-order race) is repaired in-pass rather than parked at WaitingForDependency
    /// forever. Mirrors the direct TryAssignAsync harness used by the preferred-model routing tests.
    /// </summary>
    public sealed class MissionServiceSelfHealHandoffTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MissionService SelfHeal Handoff";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_selfheal_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_selfheal_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_selfheal_logs_" + id);
            return settings;
        }

        private MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            return new MissionService(logging, db, settings, dockService, captainService);
        }

        private async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            Vessel vessel = new Vessel("selfheal-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/selfheal.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.DefaultBranch = "main";
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private async Task<Captain> CreateIdleCaptainAsync(SqliteDatabaseDriver db, string name, string model, string allowedPersonas)
        {
            Captain captain = new Captain(name);
            captain.Model = model;
            captain.AllowedPersonas = allowedPersonas;
            captain.State = CaptainStateEnum.Idle;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private async Task<Mission> CreateUpstreamAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            string persona,
            string branchName,
            MissionStatusEnum status = MissionStatusEnum.WorkProduced)
        {
            Mission mission = new Mission("Upstream " + persona, "Upstream work.");
            mission.VesselId = vessel.Id;
            mission.Persona = persona;
            mission.BranchName = branchName;
            mission.Status = status;
            mission.AgentOutput = persona + " implemented the fix.";
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private async Task<Mission> CreateDependentAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            string persona,
            string dependsOnMissionId,
            string description,
            string preferredModel = "high")
        {
            Mission mission = new Mission(persona + " stage", description);
            mission.VesselId = vessel.Id;
            mission.Persona = persona;
            mission.Status = MissionStatusEnum.Pending;
            mission.DependsOnMissionId = dependsOnMissionId;
            mission.PreferredModel = preferredModel;
            mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TryAssign_WorkerToTestEngineer_MissedHandoff_SelfHealsAndAssigns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/worker-fix").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb.Driver, "te-captain", "claude-opus-4-7", "[\"TestEngineer\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "A missed Worker->TestEngineer handoff must self-heal and assign, not park forever.");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Self-healed dependent should be launched.");
                    AssertEqual(captain.Id, readBack.CaptainId, "Self-healed dependent should take the idle eligible captain.");
                    AssertEqual("armada/worker-fix", readBack.BranchName, "The upstream branch must be stamped onto the dependent.");
                    AssertContains("## Your Role: TestEngineer", readBack.Description ?? "", "The TestEngineer persona preamble must be injected.");
                    AssertContains("## Prior Stage Output", readBack.Description ?? "", "Prior-stage context must be injected.");
                    AssertContains("Branch: armada/worker-fix", readBack.Description ?? "", "The handoff context must carry the upstream branch.");
                    AssertContains("Original TestEngineer brief.", readBack.Description ?? "", "The dependent's original brief must be preserved.");
                    AssertContains("Worker implemented the fix.", readBack.Description ?? "", "The upstream agent output must be inlined.");
                }
            });

            await RunTest("TryAssign_TestEngineerToJudge_MissedHandoff_SelfHealsGenerically", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission testEngineer = await CreateUpstreamAsync(testDb.Driver, vessel, "TestEngineer", "armada/te-stage").ConfigureAwait(false);
                    Mission judge = await CreateDependentAsync(
                        testDb.Driver, vessel, "Judge", testEngineer.Id, "Original Judge brief.").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb.Driver, "judge-captain", "claude-opus-4-7", "[\"Judge\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(judge, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "The TestEngineer->Judge handoff must self-heal generically too.");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Self-healed Judge should be launched.");
                    AssertEqual(captain.Id, readBack.CaptainId, "Self-healed Judge should take the idle eligible captain.");
                    AssertEqual("armada/te-stage", readBack.BranchName, "The upstream branch must be stamped onto the Judge.");
                    AssertContains("## Your Role: Judge (Review)", readBack.Description ?? "", "The Judge persona preamble must be injected.");
                }
            });

            await RunTest("TryAssign_MissedHandoff_NoEligibleCaptain_SelfHealIsIdempotent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/worker-idem").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original brief.").ConfigureAwait(false);
                    // No captain exists, so assignment cannot complete after the self-heal.

                    bool firstAssigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                    Mission afterFirst = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;

                    AssertFalse(firstAssigned, "With no captain the dependent cannot assign even after self-heal.");
                    AssertEqual(MissionStatusEnum.Pending, afterFirst.Status, "Unassigned dependent stays Pending.");
                    AssertEqual("armada/worker-idem", afterFirst.BranchName, "The branch must still be stamped by the self-heal.");
                    AssertEqual(1, CountOccurrences(afterFirst.Description ?? "", "## Your Role: TestEngineer"),
                        "The persona preamble must be injected exactly once.");

                    // Second pass: the handoff is now prepared, so the self-heal must not re-run and
                    // must not double-inject the preamble/context.
                    bool secondAssigned = await missions.TryAssignAsync(afterFirst, vessel).ConfigureAwait(false);
                    Mission afterSecond = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;

                    AssertFalse(secondAssigned, "Still no captain, so the second pass also cannot assign.");
                    AssertEqual(1, CountOccurrences(afterSecond.Description ?? "", "## Your Role: TestEngineer"),
                        "A second assignment pass must not re-run the handoff or double-inject context.");
                    AssertEqual(1, CountOccurrences(afterSecond.Description ?? "", "## Prior Stage Output"),
                        "Prior-stage context must not be duplicated on a re-attempt.");
                }
            });

            await RunTest("TryAssign_ArchitectDependency_NotPrepared_StillDefers", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    // An Architect upstream's handoff is the parse-and-materialize path, which cannot
                    // be reconstructed lazily; the dependent must keep deferring (no branch stamp).
                    Mission architect = await CreateUpstreamAsync(testDb.Driver, vessel, "Architect", "armada/architect-plan").ConfigureAwait(false);
                    Mission worker = await CreateDependentAsync(
                        testDb.Driver, vessel, "Worker", architect.Id, "Original worker brief.").ConfigureAwait(false);
                    // An eligible idle captain is present, so a defer can only be due to the architect gate.
                    await CreateIdleCaptainAsync(testDb.Driver, "worker-captain", "claude-opus-4-7", "[\"Worker\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(worker, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                    AssertFalse(assigned, "An unprepared Architect dependency must still defer, not lazily self-heal.");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Deferred dependent stays Pending.");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, readBack.AssignmentState, "Architect-gated dependent waits for the batch handoff.");
                    AssertNull(readBack.BranchName, "The Architect path must not stamp a branch.");
                    AssertFalse((readBack.Description ?? "").Contains("## Your Role: Worker", StringComparison.Ordinal),
                        "No persona preamble should be injected while deferring on the Architect handoff.");
                }
            });
        }
    }
}
