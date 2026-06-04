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

            await RunTest("MergeInDedicatedWorktreeAsync_TargetDrift_RetriesAndPersistsCount", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxLandingRetries = 2;
                    StubGitService git = new StubGitService();
                    git.DriftPushFailuresRemaining = 1;
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    Mission mission = CreateMission("msn_drift_retry");
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    string integrationWorktree = IntegrationWorktreePath(settings, mission);

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        mission,
                        "main",
                        "armada/captain/msn_drift_retry",
                        "Merge armada mission").ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(result, "Drift retry should succeed within the bound");
                    AssertEqual(2, git.WorktreeCalls.Count, "Initial attempt plus one retry should create two worktrees");
                    AssertEqual(2, git.RemoveWorktreeCalls.Count, "Each attempt should clean up its integration worktree");
                    AssertTrue(git.PushCalls.Contains(integrationWorktree), "Retry should push after rebuilding the integration worktree");
                    AssertEqual(1, read!.LandingRetryCount, "Retry count should persist on the mission");
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_PersistentTargetDrift_StopsAtMaxRetriesAndFailsCleanly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxLandingRetries = 2;
                    StubGitService git = new StubGitService();
                    git.DriftPushFailuresRemaining = 5;
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    Mission mission = CreateMission("msn_drift_exhausted");
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        mission,
                        "main",
                        "armada/captain/msn_drift_exhausted",
                        "Merge armada mission").ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(result, "Persistent drift should fail after exhausting the retry bound");
                    AssertEqual(3, git.WorktreeCalls.Count, "Initial attempt plus two retries should be attempted");
                    AssertEqual(2, read!.LandingRetryCount, "Retry count should stop at the configured maximum");
                    AssertContains("target_branch_drift_retry_exhausted", read.FailureReason ?? String.Empty);
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_TargetDriftWithPersistedRetryBudget_ExhaustsWithoutExtraRetry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxLandingRetries = 2;
                    StubGitService git = new StubGitService();
                    git.DriftPushFailuresRemaining = 5;
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    Mission mission = CreateMission("msn_drift_budget_persisted");
                    mission.LandingRetryCount = 2;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        mission,
                        "main",
                        "armada/captain/msn_drift_budget_persisted",
                        "Merge armada mission").ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(result, "Persisted exhausted retry budget should fail on the next drift");
                    AssertEqual(1, git.WorktreeCalls.Count, "Exhausted persisted budget should allow only the current attempt");
                    AssertEqual(1, git.RemoveWorktreeCalls.Count, "Failed exhausted attempt should still clean up its integration worktree");
                    AssertEqual(2, read!.LandingRetryCount, "Persisted retry count must not increment beyond the configured maximum");
                    AssertContains("target_branch_drift_retry_exhausted", read.FailureReason ?? String.Empty);
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
