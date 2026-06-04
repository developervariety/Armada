namespace Armada.Test.Unit.Suites.Services
{
    using System.IO;
    using System.Linq;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for LandingService integration worktree landing behavior.
    /// </summary>
    public class LandingServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Landing Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("MergeInDedicatedWorktreeAsync_CleanMerge_PushesFromTempWorktreeAndCleansUp", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    Mission mission = CreateMission();
                    string integrationWorktree = IntegrationWorktreePath(settings, mission);

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        mission,
                        "main",
                        "armada/captain/msn_landing_service",
                        "Merge armada mission").ConfigureAwait(false);

                    AssertTrue(result, "Merge should succeed");
                    AssertEqual(integrationWorktree, git.WorktreeCalls[0], "Integration worktree path should be used");
                    AssertTrue(git.MergeBranchCalls.Contains("armada/captain/msn_landing_service -> " + integrationWorktree), "Merge should target integration worktree");
                    AssertTrue(git.PushCalls.Contains(integrationWorktree), "Push should come from integration worktree");
                    AssertTrue(git.RemoveWorktreeCalls.Contains(integrationWorktree), "Integration worktree should be removed");
                    AssertTrue(git.PruneWorktreeCalls.Contains(vessel.LocalPath!), "Bare repo worktrees should be pruned");
                    AssertFalse(git.MergeBranchCalls.Any(c => c.EndsWith(" -> " + vessel.WorkingDirectory, StringComparison.Ordinal)), "User working directory must not be merge target");
                    AssertFalse(git.PushCalls.Contains(vessel.WorkingDirectory!), "User working directory must not be pushed");
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_MergeConflict_CleansUpAndLeavesUserWorkingDirectoryAlone", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.ShouldThrowOnMergeLocal = true;
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    Mission mission = CreateMission();
                    string integrationWorktree = IntegrationWorktreePath(settings, mission);

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        mission,
                        "main",
                        "armada/captain/msn_landing_service",
                        "Merge armada mission").ConfigureAwait(false);

                    AssertFalse(result, "Conflict should fail landing");
                    AssertTrue(git.RemoveWorktreeCalls.Contains(integrationWorktree), "Integration worktree should be removed after conflict");
                    AssertTrue(git.PruneWorktreeCalls.Contains(vessel.LocalPath!), "Bare repo worktrees should be pruned after conflict");
                    AssertFalse(git.PushCalls.Contains(integrationWorktree), "Failed merge should not push");
                    AssertFalse(git.MergeBranchCalls.Any(c => c.EndsWith(" -> " + vessel.WorkingDirectory, StringComparison.Ordinal)), "User working directory must not be merge target");
                    AssertFalse(git.PushCalls.Contains(vessel.WorkingDirectory!), "User working directory must not be pushed");
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_CleanUserWorkingDirectoryOnTarget_FastForwardPulls", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.IsWorkingDirectoryCleanResult = true;
                    git.CurrentBranchResult = "main";
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    Mission mission = CreateMission();

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        mission,
                        "main",
                        "armada/captain/msn_landing_service",
                        "Merge armada mission").ConfigureAwait(false);

                    AssertTrue(result, "Merge should succeed");
                    AssertTrue(git.PullFastForwardOnlyCalls.Contains(vessel.WorkingDirectory!), "Clean target checkout should be synced with ff-only pull");
                    AssertFalse(git.MergeBranchCalls.Any(c => c.EndsWith(" -> " + vessel.WorkingDirectory, StringComparison.Ordinal)), "User working directory must not be merge target");
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_DirtyOrOffTargetUserWorkingDirectory_LeavesItAlone", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();

                    StubGitService dirtyGit = new StubGitService();
                    dirtyGit.IsWorkingDirectoryCleanResult = false;
                    dirtyGit.CurrentBranchResult = "main";
                    LandingService dirtyService = CreateService(testDb.Driver, settings, dirtyGit);
                    Vessel dirtyVessel = CreateVessel();

                    bool dirtyResult = await dirtyService.MergeInDedicatedWorktreeAsync(
                        dirtyVessel,
                        CreateMission("msn_dirty"),
                        "main",
                        "armada/captain/msn_dirty",
                        "Merge armada mission").ConfigureAwait(false);

                    StubGitService offTargetGit = new StubGitService();
                    offTargetGit.IsWorkingDirectoryCleanResult = true;
                    offTargetGit.CurrentBranchResult = "feature";
                    LandingService offTargetService = CreateService(testDb.Driver, settings, offTargetGit);
                    Vessel offTargetVessel = CreateVessel();

                    bool offTargetResult = await offTargetService.MergeInDedicatedWorktreeAsync(
                        offTargetVessel,
                        CreateMission("msn_off_target"),
                        "main",
                        "armada/captain/msn_off_target",
                        "Merge armada mission").ConfigureAwait(false);

                    AssertTrue(dirtyResult, "Dirty user checkout should not fail integration landing");
                    AssertTrue(offTargetResult, "Off-target user checkout should not fail integration landing");
                    AssertEqual(0, dirtyGit.PullFastForwardOnlyCalls.Count, "Dirty user checkout should not be pulled");
                    AssertEqual(0, offTargetGit.PullFastForwardOnlyCalls.Count, "Off-target user checkout should not be pulled");
                    AssertFalse(dirtyGit.MergeBranchCalls.Any(c => c.EndsWith(" -> " + dirtyVessel.WorkingDirectory, StringComparison.Ordinal)), "Dirty user working directory must not be merge target");
                    AssertFalse(offTargetGit.MergeBranchCalls.Any(c => c.EndsWith(" -> " + offTargetVessel.WorkingDirectory, StringComparison.Ordinal)), "Off-target user working directory must not be merge target");
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private static LandingService CreateService(SqliteDatabaseDriver database, ArmadaSettings settings, StubGitService git)
        {
            return new LandingService(CreateLogging(), database, settings, git);
        }

        private static Vessel CreateVessel()
        {
            Vessel vessel = new Vessel("test-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_user_wd_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            return vessel;
        }

        private static Mission CreateMission(string id = "msn_landing_service")
        {
            Mission mission = new Mission("Test dedicated worktree landing");
            mission.Id = id;
            mission.BranchName = "armada/captain/" + id;
            return mission;
        }

        private static string IntegrationWorktreePath(ArmadaSettings settings, Mission mission)
        {
            return Path.Combine(settings.DocksDirectory, "_integration", mission.Id);
        }
    }
}
