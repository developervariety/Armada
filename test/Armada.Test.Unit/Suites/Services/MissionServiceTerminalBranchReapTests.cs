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
    /// Covers <see cref="MissionService.ReapTerminalMissionBranchAsync"/>: a mission that ends
    /// terminal Failed/Cancelled never runs the successful-land cleanup, so its captain branch must
    /// be reaped here honoring BranchCleanupPolicy -- but never while an autonomous rescue/retry for
    /// that mission is still active (the rescue reuses or branches from the same captain branch).
    /// </summary>
    public sealed class MissionServiceTerminalBranchReapTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MissionService Terminal Branch Reap";

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
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_reap_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_reap_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_reap_logs_" + id);
            return settings;
        }

        private MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git)
        {
            LoggingModule logging = CreateLogging();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            return new MissionService(logging, db, settings, dockService, captainService, null, git);
        }

        private async Task<Vessel> CreateVesselAsync(
            SqliteDatabaseDriver db,
            ArmadaSettings settings,
            BranchCleanupPolicyEnum? policy)
        {
            Vessel vessel = new Vessel("reap-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/reap.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.WorkingDirectory = Path.Combine(settings.ReposDirectory, vessel.Name + "-work");
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = policy;
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private async Task<Mission> CreateMissionAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            MissionStatusEnum status,
            string branchName,
            string? parentMissionId = null)
        {
            Mission mission = new Mission("Terminal mission", "Work that ended " + status + ".");
            mission.VesselId = vessel.Id;
            mission.Persona = "Worker";
            mission.Status = status;
            mission.BranchName = branchName;
            mission.ParentMissionId = parentMissionId;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Reap_TerminallyFailed_NoActiveRescue_LocalAndRemote_DeletesLocalAndRemote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-1").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-1"),
                        "A terminally Failed mission with no active rescue must have its local captain branch reaped.");
                    AssertTrue(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-1"),
                        "Under LocalAndRemote the remote captain branch must be reaped too.");
                }
            });

            await RunTest("Reap_TerminallyCancelled_LocalOnly_DeletesLocalNotRemote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalOnly).ConfigureAwait(false);

                    Mission cancelled = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Cancelled, "armada/worker/reap-2").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(cancelled).ConfigureAwait(false);

                    AssertTrue(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-2"),
                        "A terminally Cancelled mission must have its local captain branch reaped.");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-2"),
                        "Under LocalOnly the remote captain branch must NOT be touched.");
                }
            });

            await RunTest("Reap_FailedWithActiveRescue_RetainsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-3").ConfigureAwait(false);
                    // An autonomous rescue is still in flight (ParentMissionId links back, non-terminal).
                    await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.InProgress, "armada/rescue/reap-3", parentMissionId: failed.Id).ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertFalse(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-3"),
                        "The don't-reap-while-rescuing guard must retain the branch while an active rescue depends on the mission.");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-3"),
                        "No remote delete may fire while an active rescue is in flight.");
                }
            });

            await RunTest("Reap_PolicyNone_RetainsBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.None).ConfigureAwait(false);

                    Mission failed = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.Failed, "armada/worker/reap-4").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(failed).ConfigureAwait(false);

                    AssertFalse(git.OperationCalls.Contains("delete-local-branch:armada/worker/reap-4"),
                        "Under policy None the captain branch must be retained.");
                    AssertFalse(git.OperationCalls.Contains("delete-remote-branch:armada/worker/reap-4"),
                        "Under policy None no remote delete may fire.");
                }
            });

            await RunTest("Reap_NonTerminalMission_NoOp", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService missions = CreateMissionService(testDb.Driver, settings, git);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings, BranchCleanupPolicyEnum.LocalAndRemote).ConfigureAwait(false);

                    Mission inProgress = await CreateMissionAsync(testDb.Driver, vessel, MissionStatusEnum.InProgress, "armada/worker/reap-5").ConfigureAwait(false);

                    await missions.ReapTerminalMissionBranchAsync(inProgress).ConfigureAwait(false);

                    AssertEqual(0, git.OperationCalls.Count,
                        "A non-terminal mission must never trigger any branch deletion.");
                }
            });
        }
    }
}
