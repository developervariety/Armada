namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
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
            // The retry event belongs to the mission owner. Scoped operator reads filter by tenant and user,
            // so an event written without them is invisible to every non-admin reader of the mission.
            await RunTest("RetryLandingAsync_RecordsRetryEventInTheMissionOwnersScope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    LandingService service = CreateService(testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("retry-scope-vessel", "https://github.com/test/retry.git");
                    vessel.TenantId = Armada.Core.Constants.DefaultTenantId;
                    vessel.UserId = Armada.Core.Constants.DefaultUserId;
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_retry_scope_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Mission mission = new Mission("retry scope mission", "retry");
                    mission.TenantId = Armada.Core.Constants.DefaultTenantId;
                    mission.UserId = Armada.Core.Constants.DefaultUserId;
                    mission.VesselId = vessel.Id;
                    mission.BranchName = "armada/retry-scope/branch";
                    mission.Status = MissionStatusEnum.LandingFailed;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    // The stub reports the branch missing, so the retry stops after recording its event.
                    bool retried = await service.RetryLandingAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(retried, "A retry against a missing branch does not land");

                    EnumerationResult<ArmadaEvent> owner = await testDb.Driver.Events.EnumerateAsync(
                        Armada.Core.Constants.DefaultTenantId,
                        Armada.Core.Constants.DefaultUserId,
                        new EnumerationQuery { MissionId = mission.Id, EventType = "mission.landing_retry" }).ConfigureAwait(false);
                    AssertEqual(1, owner.Objects.Count, "The mission owner's scoped read must find the retry event");

                    EnumerationResult<ArmadaEvent> colleague = await testDb.Driver.Events.EnumerateAsync(
                        Armada.Core.Constants.DefaultTenantId,
                        "usr_retry_scope_other",
                        new EnumerationQuery { MissionId = mission.Id, EventType = "mission.landing_retry" }).ConfigureAwait(false);
                    AssertEqual(0, colleague.Objects.Count, "Another user in the tenant must not see the retry event");
                }
            });

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

            await RunTest("MergeInDedicatedWorktreeAsync_LocalMergeSyncsCheckoutFromLocalRepository", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.IsWorkingDirectoryCleanResult = true;
                    git.CurrentBranchResult = "main";
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    vessel.LandingMode = LandingModeEnum.LocalMerge;

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        CreateMission("msn_local_checkout_sync"),
                        "main",
                        "armada/captain/msn_local_checkout_sync",
                        "Merge armada mission").ConfigureAwait(false);

                    AssertTrue(result, "LocalMerge landing should succeed after checkout reconciliation");
                    AssertTrue(
                        git.MergeBranchCalls.Contains("main -> " + vessel.WorkingDirectory),
                        "LocalMerge checkout should fast-forward from the local landing repository");
                    AssertFalse(
                        git.PullFastForwardOnlyCalls.Contains(vessel.WorkingDirectory!),
                        "LocalMerge checkout must not pull a potentially stale origin");
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_DirtyOrOffTargetLocalMergeCheckout_FailsLanding", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();

                    StubGitService dirtyGit = new StubGitService();
                    dirtyGit.IsWorkingDirectoryCleanResult = false;
                    dirtyGit.CurrentBranchResult = "main";
                    LandingService dirtyService = CreateService(testDb.Driver, settings, dirtyGit);
                    Vessel dirtyVessel = CreateVessel();
                    dirtyVessel.LandingMode = LandingModeEnum.LocalMerge;

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
                    offTargetVessel.LandingMode = LandingModeEnum.LocalMerge;

                    bool offTargetResult = await offTargetService.MergeInDedicatedWorktreeAsync(
                        offTargetVessel,
                        CreateMission("msn_off_target"),
                        "main",
                        "armada/captain/msn_off_target",
                        "Merge armada mission").ConfigureAwait(false);

                    AssertFalse(dirtyResult, "Dirty configured checkout must keep LocalMerge landing incomplete");
                    AssertFalse(offTargetResult, "Off-target configured checkout must keep LocalMerge landing incomplete");
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

            await RunTest("MergeInDedicatedWorktreeAsync_LocalMergeVessel_DoesNotPushAndSurvivesRemoteDivergence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    // Regression: LocalMerge means "land into the local repository"; pushing to a
                    // remote is the operator's call. When the local default branch had diverged
                    // from origin, the unconditional push was rejected as non-fast-forward,
                    // IsTargetBranchDrift misread that as target-branch drift, and the retry loop
                    // burned maxLandingRetries re-pushing the same divergence -- marking the
                    // mission LandingFailed and leaving a stray branch even though the local merge
                    // had succeeded. Standing push failures must not affect a LocalMerge landing.
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxLandingRetries = 2;
                    StubGitService git = new StubGitService();
                    git.DriftPushFailuresRemaining = 5;
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    vessel.LandingMode = LandingModeEnum.LocalMerge;
                    Mission mission = CreateMission("msn_local_merge_no_push");
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    string integrationWorktree = IntegrationWorktreePath(settings, mission);

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel,
                        mission,
                        "main",
                        "armada/captain/msn_local_merge_no_push",
                        "Merge armada mission").ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(result, "LocalMerge landing should succeed without pushing");
                    AssertFalse(git.PushCalls.Contains(integrationWorktree), "LocalMerge landing must not push to origin");
                    AssertEqual(1, git.WorktreeCalls.Count, "LocalMerge landing should not retry when no push is attempted");
                    AssertEqual(0, read!.LandingRetryCount, "LocalMerge landing should not consume landing retries");
                    AssertTrue(String.IsNullOrEmpty(read.FailureReason), "LocalMerge landing should not record a failure reason");
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

            // A worktree left holding the target branch (for example one a captain created in the
            // landing repository) made git refuse "worktree add <dir> main", so every landing for the
            // vessel failed with a generic message. The integration worktree must not need the target
            // branch to be free.
            await RunTest("MergeInDedicatedWorktreeAsync_AnotherWorktreeHoldsTarget_StillLands", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_landing_held_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    string sourceDir = Path.Combine(rootDir, "source");
                    string bareDir = Path.Combine(rootDir, "bare.git");
                    string holderDir = Path.Combine(rootDir, "holder_wt");
                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "# test\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);
                    string captainBranch = "armada/captain/msn_held_target";
                    await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(sourceDir, "feature.txt"), "feature\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "feature.txt").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Add feature").ConfigureAwait(false);
                    string captainHead = (await RunGitAsync(sourceDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await RunGitAsync(bareDir, "worktree", "add", holderDir, "main").ConfigureAwait(false);
                    string mainBefore = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ArmadaSettings settings = CreateSettings();
                        settings.DocksDirectory = Path.Combine(rootDir, "docks");
                        LandingService service = new LandingService(CreateLogging(), testDb.Driver, settings, new GitService(CreateLogging()));
                        Vessel vessel = new Vessel("held-vessel", sourceDir);
                        vessel.LocalPath = bareDir;
                        vessel.DefaultBranch = "main";
                        vessel.LandingMode = LandingModeEnum.LocalMerge;
                        Mission mission = CreateMission("msn_held_target");
                        mission.CommitHash = captainHead;
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        bool result = await service.MergeInDedicatedWorktreeAsync(
                            vessel, mission, "main", captainBranch, "Merge armada mission").ConfigureAwait(false);

                        AssertTrue(result, "landing must succeed while another worktree holds main; failure reason: " + (mission.FailureReason ?? "(none)"));
                        string mainAfter = (await RunGitAsync(bareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        AssertFalse(String.Equals(mainBefore, mainAfter, StringComparison.Ordinal), "main must advance in the landing repository");
                        await RunGitAsync(bareDir, "merge-base", "--is-ancestor", captainHead, "refs/heads/main").ConfigureAwait(false);
                        await RunGitAsync(bareDir, "merge-base", "--is-ancestor", mainBefore, "refs/heads/main").ConfigureAwait(false);
                        AssertFalse(Directory.Exists(Path.Combine(settings.DocksDirectory, "_integration", mission.Id)), "integration worktree must be removed");
                        AssertTrue(Directory.Exists(holderDir), "the other worktree must be left in place");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_GitRefusesWorktree_FailureReasonNamesBlockingPath", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.RevisionCommitShaResult = new string('a', 40);
                    git.ShouldThrowOnWorktree = true;
                    git.WorktreeFailureMessage = "git failed (exit 128): Preparing worktree (detached HEAD aaaaaaa)\nfatal: 'main' is already used by worktree at '/tmp/holder/main_wt'";
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    vessel.LandingMode = LandingModeEnum.LocalMerge;
                    Mission mission = CreateMission("msn_worktree_refused");
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel, mission, "main", mission.BranchName, "Merge armada mission").ConfigureAwait(false);

                    AssertFalse(result, "a refused worktree must fail the landing");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    string reason = read?.FailureReason ?? String.Empty;
                    AssertTrue(reason.StartsWith("worktree_conflict:", StringComparison.Ordinal), "failure class must be worktree_conflict, got: " + reason);
                    AssertContains("/tmp/holder/main_wt", reason, "failure reason must name the blocking worktree path");
                    AssertContains("already used by worktree", reason, "failure reason must carry the git stderr");
                }
            });

            await RunTest("MergeInDedicatedWorktreeAsync_AdvancesTargetOnlyFromTheTipItMerged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    vessel.LandingMode = LandingModeEnum.LocalMerge;
                    Mission mission = CreateMission("msn_cas_target");
                    string integrationWorktree = IntegrationWorktreePath(settings, mission);
                    string tip = new string('a', 40);
                    string merged = new string('b', 40);
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/main"] = tip;
                    git.RevisionCommitShas[integrationWorktree + "|HEAD"] = merged;

                    bool result = await service.MergeInDedicatedWorktreeAsync(
                        vessel, mission, "main", mission.BranchName, "Merge armada mission").ConfigureAwait(false);

                    AssertTrue(result, "merge should succeed");
                    AssertTrue(git.CompareAndSwapCalls.Contains(vessel.LocalPath + ":main:" + merged + ":" + tip),
                        "target must advance by compare-and-swap from the tip the integration worktree started at; calls: " + String.Join(", ", git.CompareAndSwapCalls));
                }
            });
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);
            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + stderr.Trim());
                return stdout;
            }
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
