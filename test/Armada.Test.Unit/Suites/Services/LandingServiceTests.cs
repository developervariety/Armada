namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using Armada.Core;
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

            // Only WorkProduced and LandingFailed missions land through a retry. A Review mission is refused whether
            // or not it has a review gate, so the dashboard offers no Land action for it.
            await RunTest("RetryLandingAsync_ReviewMissionWithoutReviewGate_IsRefusedAndStaysInReview", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    LandingService service = CreateService(testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("retry-review-vessel", "https://github.com/test/retry-review.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_retry_review_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Mission mission = new Mission("retry review mission", "retry");
                    mission.VesselId = vessel.Id;
                    mission.BranchName = "armada/retry-review/branch";
                    mission.Status = MissionStatusEnum.Review;
                    mission.RequiresReview = false;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    bool retried = await service.RetryLandingAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(retried, "A Review mission is not landed by a retry");

                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(after, "The refused mission still exists");
                    AssertEqual(MissionStatusEnum.Review, after!.Status, "A refused retry leaves the mission in Review");

                    EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(
                        new EnumerationQuery { MissionId = mission.Id, EventType = "mission.landing_retry" }).ConfigureAwait(false);
                    AssertEqual(0, events.Objects.Count, "A refused retry records no retry event");
                }
            });

            // A failed landing's dock is reclaimed. The retry must land from the mission branch, not
            // from the deleted dock path, or it is refused as landing_evidence_unavailable.
            await RunTest("RetryLandingAsync_DockReclaimed_LandsFromATemporaryWorktreeAtTheBranchTipAndRemovesIt", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    const string branch = "armada/retry-reclaimed/branch";
                    const string tip = "6666666666666666666666666666666666666666";

                    Vessel vessel = new Vessel("retry-reclaimed-vessel", "https://github.com/test/retry-reclaimed.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_retry_reclaimed_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    git.ExistingBranches.Add(branch);
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + branch] = tip;

                    string reclaimedPath = Path.Combine(Path.GetTempPath(), "armada_retry_reclaimed_dock_" + Guid.NewGuid().ToString("N"));
                    Dock dock = new Dock(vessel.Id);
                    dock.WorktreePath = reclaimedPath;
                    dock.BranchName = branch;
                    dock.Active = false;
                    dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    Mission mission = new Mission("retry reclaimed mission", "retry");
                    mission.VesselId = vessel.Id;
                    mission.BranchName = branch;
                    mission.DockId = dock.Id;
                    mission.Status = MissionStatusEnum.LandingFailed;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    string? landedFrom = null;
                    string? landedDockId = null;
                    service.OnPerformLanding = async (Mission m, Dock d) =>
                    {
                        landedFrom = d.WorktreePath;
                        landedDockId = d.Id;
                        m.Status = MissionStatusEnum.Complete;
                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    };

                    bool retried = await service.RetryLandingAsync(mission.Id).ConfigureAwait(false);

                    AssertTrue(retried, "The retry lands");
                    AssertNotNull(landedFrom, "The landing handler ran");
                    AssertTrue(!String.Equals(landedFrom, reclaimedPath, StringComparison.Ordinal), "The retry does not read the reclaimed dock path");
                    AssertContains("armada-landing-retry", landedFrom!, "The retry reads a temporary worktree");
                    AssertTrue(git.WorktreeCalls.Contains(landedFrom!), "The temporary worktree is created before the landing");
                    AssertTrue(git.ExistingBranches.Contains(tip), "The temporary worktree is placed at the branch tip commit");
                    AssertContains(landedFrom!, String.Join(" | ", git.RemoveWorktreeCalls), "The temporary worktree is removed after the landing");
                    AssertEqual(dock.Id, landedDockId, "The landing still names the mission's dock");
                }
            });

            // A retry resets the mission to WorkProduced before it lands, so a second retry started while the first
            // runs passes every status check. Only one retry per mission may run, or the same work lands twice.
            await RunTest("RetryLandingAsync_SecondRetryWhileOneRuns_IsRefusedAndTheWorkLandsOnce", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    const string branch = "armada/retry-once/branch";

                    Vessel vessel = new Vessel("retry-once-vessel", "https://github.com/test/retry-once.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_retry_once_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    git.ExistingBranches.Add(branch);
                    git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/" + branch] = "7777777777777777777777777777777777777777";

                    Mission mission = new Mission("retry once mission", "retry");
                    mission.VesselId = vessel.Id;
                    mission.BranchName = branch;
                    mission.Status = MissionStatusEnum.LandingFailed;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    TaskCompletionSource<bool> landingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int landings = 0;
                    service.OnPerformLanding = async (Mission m, Dock d) =>
                    {
                        Interlocked.Increment(ref landings);
                        landingStarted.TrySetResult(true);
                        await release.Task.ConfigureAwait(false);
                        m.Status = MissionStatusEnum.Complete;
                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    };

                    Task<bool> first = service.RetryLandingAsync(mission.Id);
                    try
                    {
                        AssertTrue(await Task.WhenAny(landingStarted.Task, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false) == landingStarted.Task,
                            "The first retry reaches the landing");
                        AssertTrue(service.IsRetryInFlight(mission.Id), "The running retry is reported in flight");

                        Task<bool> second = service.RetryLandingAsync(mission.Id);
                        bool answered = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) == second;
                        AssertTrue(answered, "A second retry while the first runs is refused at once, not run into a second landing");
                        AssertFalse(await second.ConfigureAwait(false), "The refused retry reports false");
                    }
                    finally
                    {
                        release.TrySetResult(true);
                    }

                    AssertTrue(await first.ConfigureAwait(false), "The first retry lands");
                    AssertEqual(1, landings, "The work lands once");
                    AssertFalse(service.IsRetryInFlight(mission.Id), "The finished retry is no longer in flight");
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

                    bool result;
                    List<RecordedMeasurement> landings;
                    using (ArmadaMetricsRecorder metrics = new ArmadaMetricsRecorder())
                    {
                        result = await service.MergeInDedicatedWorktreeAsync(
                            vessel,
                            mission,
                            "main",
                            "armada/captain/msn_drift_retry",
                            "Merge armada mission").ConfigureAwait(false);
                        landings = metrics.Of("armada.landing.seconds");
                    }

                    AssertTrue(landings.Any(m => m.Has(ArmadaMetrics.TagPath, ArmadaMetrics.LandingLocalMerge)
                        && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeLanded)
                        && m.Has(ArmadaMetrics.TagRetried, "true")),
                        "a landing that survived one drift is recorded once as landed and retried");

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

                    bool result;
                    List<RecordedMeasurement> landings;
                    using (ArmadaMetricsRecorder metrics = new ArmadaMetricsRecorder())
                    {
                        result = await service.MergeInDedicatedWorktreeAsync(
                            vessel,
                            mission,
                            "main",
                            "armada/captain/msn_drift_exhausted",
                            "Merge armada mission").ConfigureAwait(false);
                        landings = metrics.Of("armada.landing.seconds");
                    }

                    AssertTrue(landings.Any(m => m.Has(ArmadaMetrics.TagPath, ArmadaMetrics.LandingLocalMerge)
                        && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeFailure)
                        && m.Has(ArmadaMetrics.TagRetried, "true")),
                        "an exhausted landing keeps its false result and is recorded as a retried failure");

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(result, "Persistent drift should fail after exhausting the retry bound");
                    AssertEqual(3, git.WorktreeCalls.Count, "Initial attempt plus two retries should be attempted");
                    AssertEqual(2, read!.LandingRetryCount, "Retry count should stop at the configured maximum");
                    AssertContains("target_branch_drift_retry_exhausted", read.FailureReason ?? String.Empty);
                }
            });

            // Advancing the bare target before the push left a merge only the bare held whenever origin
            // refused the push; exhausting the retries said so only in a log line.
            await RunTest("MergeInDedicatedWorktreeAsync_RejectedPush_NeverAdvancesTheBareTargetAndRaisesOneIncidentWhenExhausted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxLandingRetries = 1;
                    StubGitService git = new StubGitService();
                    git.DriftPushFailuresRemaining = 5;
                    LandingService service = CreateService(testDb.Driver, settings, git);
                    Vessel vessel = CreateVessel();
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission mission = CreateMission("msn_push_first");
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    bool result = await service.MergeInDedicatedWorktreeAsync(vessel, mission, "main", "armada/captain/msn_push_first", "Merge armada mission").ConfigureAwait(false);

                    AssertFalse(result, "The landing fails while origin refuses the push");
                    AssertEqual(0, git.CompareAndSwapCalls.Count, "The bare target never moves for a push origin refused");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("landing.origin_push_failed").ConfigureAwait(false);
                    AssertEqual(1, events.Count, "Exhausting the retries records the failure");
                    EnumerationResult<Incident> incidents = await new IncidentService(testDb.Driver).EnumerateAsync(
                        AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, true, true, "UnitTest"),
                        new IncidentQuery { VesselId = vessel.Id, ExcludeTerminal = true, PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(1L, incidents.TotalRecords, "One incident names the failed push");
                    AssertContains("Never reset the bare target", incidents.Objects[0].RecoveryNotes ?? String.Empty);
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

            // Two branches that each pass the vessel's checks alone can still break the target together. The
            // merged commit is measured before the target moves, so the second landing is refused by name and
            // the target and the refused branch stay exactly as they were.
            await RunTest("MergeInDedicatedWorktreeAsync_CombinedTreeFailsChecks_RefusesAndLeavesTargetUnchanged", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_landing_icv_red_" + Guid.NewGuid().ToString("N"));
                try
                {
                    IntegrationFixture fixture = await CreateIntegrationFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ArmadaSettings settings = CreateSettings();
                        settings.DocksDirectory = Path.Combine(rootDir, "docks");
                        LandingService service = CreateValidatedService(testDb.Driver, settings);
                        Vessel vessel = await CreateIntegrationVesselAsync(testDb.Driver, fixture).ConfigureAwait(false);
                        await CreateCheckProfileAsync(testDb.Driver, vessel, fixture).ConfigureAwait(false);

                        Mission left = CreateMission("msn_icv_left");
                        await testDb.Driver.Missions.CreateAsync(left).ConfigureAwait(false);
                        bool leftLanded = await service.MergeInDedicatedWorktreeAsync(
                            vessel, left, "main", fixture.LeftBranch, "Merge armada mission").ConfigureAwait(false);
                        AssertTrue(leftLanded, "the left branch passes alone and lands; failure reason: " + (left.FailureReason ?? "(none)"));
                        string mainAfterLeft = (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        string rightTip = (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/" + fixture.RightBranch).ConfigureAwait(false)).Trim();

                        Mission right = CreateMission("msn_icv_right");
                        await testDb.Driver.Missions.CreateAsync(right).ConfigureAwait(false);
                        bool rightLanded = await service.MergeInDedicatedWorktreeAsync(
                            vessel, right, "main", fixture.RightBranch, "Merge armada mission").ConfigureAwait(false);

                        AssertFalse(rightLanded, "a merged commit whose checks fail must not land");
                        Mission? read = await testDb.Driver.Missions.ReadAsync(right.Id).ConfigureAwait(false);
                        AssertStartsWith("integration_validation_failed:", read?.FailureReason ?? "", "the refusal is named");
                        AssertEqual(mainAfterLeft, (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(),
                            "the refused candidate leaves the target where it was");
                        AssertEqual(rightTip, (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/" + fixture.RightBranch).ConfigureAwait(false)).Trim(),
                            "the refused branch is left in place");
                        AssertEqual(2, CountCheckRuns(fixture), "each landing ran the check once against its merged commit");

                        List<IntegrationValidationResult> evidence = await ReadMissionEvidenceAsync(testDb.Driver, right.Id).ConfigureAwait(false);
                        AssertEqual(1, evidence.Count, "the refusal is recorded as evidence");
                        AssertEqual(IntegrationValidationOutcomeEnum.Failed, evidence[0].Outcome, "the evidence names the failure");
                        AssertEqual(mainAfterLeft, evidence[0].Candidate.TargetBaseCommit, "the evidence names the target base");
                        AssertEqual(rightTip, evidence[0].Candidate.SourceCommit, "the evidence names the source commit");
                        AssertEqual(1, evidence[0].CheckRunIds.Count, "the evidence links the check that decided it");

                        CheckRun? decidingRun = await testDb.Driver.CheckRuns.ReadAsync(evidence[0].CheckRunIds[0]).ConfigureAwait(false);
                        AssertNotNull(decidingRun, "the linked check exists");
                        AssertEqual(CheckRunStatusEnum.Failed, decidingRun!.Status, "the linked check failed");
                        AssertEqual(evidence[0].Candidate.IntegrationCommit, decidingRun.CommitHash, "the check measured the merged commit");
                        AssertNull(decidingRun.MissionId, "an integration check links no mission, so it never decides a branch-review gate");
                        AssertNull(decidingRun.VoyageId, "an integration check links no voyage");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            // A vessel whose policy requires checks but whose profile defines no Build or UnitTest command cannot
            // be measured; that is a named refusal, never a silent pass. An explicit no-checks policy lands.
            await RunTest("MergeInDedicatedWorktreeAsync_RequiredCommandMissing_RefusesByNameUnlessPolicyRequiresNone", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_landing_icv_missing_" + Guid.NewGuid().ToString("N"));
                try
                {
                    IntegrationFixture fixture = await CreateIntegrationFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ArmadaSettings settings = CreateSettings();
                        settings.DocksDirectory = Path.Combine(rootDir, "docks");
                        LandingService service = CreateValidatedService(testDb.Driver, settings);
                        Vessel vessel = await CreateIntegrationVesselAsync(testDb.Driver, fixture).ConfigureAwait(false);
                        await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                        {
                            Name = "Lint Only Profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vessel.Id,
                            LintCommand = "sh " + fixture.CheckScript,
                            IsDefault = true,
                            Active = true
                        }).ConfigureAwait(false);
                        string mainBefore = (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                        Mission mission = CreateMission("msn_icv_missing");
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                        bool refusedLanded = await service.MergeInDedicatedWorktreeAsync(
                            vessel, mission, "main", fixture.LeftBranch, "Merge armada mission").ConfigureAwait(false);

                        AssertFalse(refusedLanded, "a candidate that cannot be measured does not land");
                        AssertStartsWith("integration_validation_command_missing:", mission.FailureReason ?? "", "the missing command is named");
                        AssertEqual(mainBefore, (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(),
                            "the target does not move");
                        AssertEqual(0, CountCheckRuns(fixture), "no check ran");

                        settings.IntegrationValidation.NoChecksRequiredVesselIds.Add(vessel.Id);
                        bool landed = await service.MergeInDedicatedWorktreeAsync(
                            vessel, mission, "main", fixture.LeftBranch, "Merge armada mission").ConfigureAwait(false);

                        AssertTrue(landed, "a vessel whose policy requires no checks lands; failure reason: " + (mission.FailureReason ?? "(none)"));
                        List<IntegrationValidationResult> evidence = await ReadMissionEvidenceAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                        AssertTrue(evidence.Any(e => e.Outcome == IntegrationValidationOutcomeEnum.CommandMissing), "the refusal is recorded");
                        AssertTrue(evidence.Any(e => e.Outcome == IntegrationValidationOutcomeEnum.NotRequired), "the explicit no-checks policy is recorded separately");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            // A target that moves while a candidate is measured makes that candidate stale. It is not published;
            // the bounded retry rebuilds the merge on the new tip and measures the new candidate.
            await RunTest("MergeInDedicatedWorktreeAsync_TargetMovesDuringValidation_RevalidatesTheNewCandidate", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_landing_icv_drift_" + Guid.NewGuid().ToString("N"));
                try
                {
                    IntegrationFixture fixture = await CreateIntegrationFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ArmadaSettings settings = CreateSettings();
                        settings.DocksDirectory = Path.Combine(rootDir, "docks");
                        settings.MaxLandingRetries = 2;
                        LandingService service = CreateValidatedService(testDb.Driver, settings);
                        Vessel vessel = await CreateIntegrationVesselAsync(testDb.Driver, fixture).ConfigureAwait(false);
                        await CreateCheckProfileAsync(testDb.Driver, vessel, fixture).ConfigureAwait(false);

                        string baseTip = (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        string moverTip = (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/" + fixture.MoverBranch).ConfigureAwait(false)).Trim();
                        await File.WriteAllTextAsync(fixture.HookFile,
                            "git -C " + fixture.BareDir + " update-ref refs/heads/main " + moverTip + " " + baseTip + "\n").ConfigureAwait(false);

                        Mission mission = CreateMission("msn_icv_drift");
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                        bool landed = await service.MergeInDedicatedWorktreeAsync(
                            vessel, mission, "main", fixture.LeftBranch, "Merge armada mission").ConfigureAwait(false);

                        AssertTrue(landed, "the rebuilt candidate lands; failure reason: " + (mission.FailureReason ?? "(none)"));
                        AssertEqual(1, mission.LandingRetryCount, "the stale candidate cost exactly one bounded retry");
                        AssertEqual(2, CountCheckRuns(fixture), "the stale candidate and the rebuilt candidate were each measured");
                        await RunGitAsync(fixture.BareDir, "merge-base", "--is-ancestor", moverTip, "refs/heads/main").ConfigureAwait(false);

                        List<IntegrationValidationResult> evidence = await ReadMissionEvidenceAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                        AssertEqual(2, evidence.Count, "each candidate has its own evidence");
                        AssertTrue(evidence.Any(e => e.Candidate.TargetBaseCommit == baseTip), "the stale candidate was built on the old tip");
                        AssertTrue(evidence.Any(e => e.Candidate.TargetBaseCommit == moverTip), "the rebuilt candidate was built on the new tip");
                        AssertNotEqual(evidence[0].Candidate.IntegrationCommit, evidence[1].Candidate.IntegrationCommit,
                            "evidence for one candidate is never reused for another");
                        string mainAfter = (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        AssertTrue(evidence.Any(e => e.Candidate.IntegrationCommit == mainAfter), "the published commit is the one the last evidence measured");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            // A Check that already passed at exactly the candidate commit with the same command decides the
            // candidate, so a fast-forward landing of a measured tip runs no second gate.
            await RunTest("MergeInDedicatedWorktreeAsync_FastForwardOfMeasuredTip_ReusesThePassingCheck", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_landing_icv_reuse_" + Guid.NewGuid().ToString("N"));
                try
                {
                    IntegrationFixture fixture = await CreateIntegrationFixtureAsync(rootDir).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        ArmadaSettings settings = CreateSettings();
                        settings.DocksDirectory = Path.Combine(rootDir, "docks");
                        LandingService service = CreateValidatedService(testDb.Driver, settings);
                        Vessel vessel = await CreateIntegrationVesselAsync(testDb.Driver, fixture).ConfigureAwait(false);
                        WorkflowProfile profile = await CreateCheckProfileAsync(testDb.Driver, vessel, fixture).ConfigureAwait(false);
                        string leftTip = (await RunGitAsync(fixture.BareDir, "rev-parse", "refs/heads/" + fixture.LeftBranch).ConfigureAwait(false)).Trim();
                        CheckRun earlier = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            VesselId = vessel.Id,
                            WorkflowProfileId = profile.Id,
                            Type = CheckRunTypeEnum.UnitTest,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Passed,
                            Command = profile.UnitTestCommand!,
                            CommitHash = leftTip,
                            ExitCode = 0,
                            CreatedUtc = DateTime.UtcNow,
                            LastUpdateUtc = DateTime.UtcNow
                        }).ConfigureAwait(false);

                        Mission mission = CreateMission("msn_icv_reuse");
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                        bool landed = await service.MergeInDedicatedWorktreeAsync(
                            vessel, mission, "main", fixture.LeftBranch, "Merge armada mission").ConfigureAwait(false);

                        AssertTrue(landed, "the measured tip lands; failure reason: " + (mission.FailureReason ?? "(none)"));
                        AssertEqual(0, CountCheckRuns(fixture), "no second check ran");
                        List<IntegrationValidationResult> evidence = await ReadMissionEvidenceAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                        AssertEqual(1, evidence.Count, "the landing is recorded");
                        AssertEqual(IntegrationValidationOutcomeEnum.Passed, evidence[0].Outcome, "the reused check passes the candidate");
                        AssertTrue(evidence[0].ReusedCheckRunIds.Contains(earlier.Id), "the evidence names the reused check");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
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

        private static LandingService CreateValidatedService(SqliteDatabaseDriver database, ArmadaSettings settings)
        {
            LoggingModule logging = CreateLogging();
            LandingService service = new LandingService(logging, database, settings, new GitService(logging));
            WorkflowProfileService profiles = new WorkflowProfileService(database, logging);
            VesselReadinessService readiness = new VesselReadinessService(database, profiles, logging);
            CheckRunService checkRuns = new CheckRunService(database, profiles, readiness, logging);
            service.SetIntegrationValidator(new IntegrationCandidateValidator(logging, database, settings, checkRuns, profiles));
            return service;
        }

        private static async Task<Vessel> CreateIntegrationVesselAsync(SqliteDatabaseDriver database, IntegrationFixture fixture)
        {
            Vessel vessel = new Vessel("integration-vessel", fixture.SourceDir);
            vessel.LocalPath = fixture.BareDir;
            vessel.WorkingDirectory = fixture.WorkingDir;
            vessel.DefaultBranch = "main";
            vessel.LandingMode = LandingModeEnum.LocalMerge;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<WorkflowProfile> CreateCheckProfileAsync(SqliteDatabaseDriver database, Vessel vessel, IntegrationFixture fixture)
        {
            return await database.WorkflowProfiles.CreateAsync(new WorkflowProfile
            {
                Name = "Integration Check Profile",
                Scope = WorkflowProfileScopeEnum.Vessel,
                VesselId = vessel.Id,
                UnitTestCommand = "sh " + fixture.CheckScript,
                IsDefault = true,
                Active = true
            }).ConfigureAwait(false);
        }

        private static async Task<List<IntegrationValidationResult>> ReadMissionEvidenceAsync(SqliteDatabaseDriver database, string missionId)
        {
            List<ArmadaEvent> events = await database.Events.EnumerateByEntityAsync("mission", missionId, 50).ConfigureAwait(false);
            List<IntegrationValidationResult> results = new List<IntegrationValidationResult>();
            foreach (ArmadaEvent evt in events.Where(e => e.EventType == IntegrationCandidateValidator.EvidenceEventType).OrderBy(e => e.CreatedUtc))
            {
                IntegrationValidationResult? result = JsonSerializer.Deserialize<IntegrationValidationResult>(evt.Payload ?? "null");
                if (result != null) results.Add(result);
            }
            return results;
        }

        private static int CountCheckRuns(IntegrationFixture fixture)
        {
            return File.Exists(fixture.CounterFile) ? File.ReadAllLines(fixture.CounterFile).Length : 0;
        }

        /// <summary>
        /// A landing repository whose check fails only when both <c>left.flag</c> and <c>right.flag</c> are in
        /// the tree, so the left and right branches each pass alone and fail together. The check counts its
        /// runs and runs a one-shot hook script when one is present.
        /// </summary>
        private static async Task<IntegrationFixture> CreateIntegrationFixtureAsync(string rootDir)
        {
            IntegrationFixture fixture = new IntegrationFixture(rootDir);
            Directory.CreateDirectory(fixture.SourceDir);
            await RunGitAsync(fixture.SourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(fixture.SourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(fixture.SourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(fixture.SourceDir, "README.md"), "# integration\n").ConfigureAwait(false);
            await RunGitAsync(fixture.SourceDir, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(fixture.SourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            await CommitBranchFileAsync(fixture.SourceDir, fixture.LeftBranch, "left.flag").ConfigureAwait(false);
            await CommitBranchFileAsync(fixture.SourceDir, fixture.RightBranch, "right.flag").ConfigureAwait(false);
            await CommitBranchFileAsync(fixture.SourceDir, fixture.MoverBranch, "mover.txt").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", fixture.SourceDir, fixture.BareDir).ConfigureAwait(false);
            await RunGitAsync(fixture.BareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(fixture.BareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await RunGitAsync(rootDir, "clone", fixture.BareDir, fixture.WorkingDir).ConfigureAwait(false);
            await RunGitAsync(fixture.WorkingDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(fixture.WorkingDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(fixture.CheckScript,
                "echo run >> " + fixture.CounterFile + "\n"
                + "if [ -f " + fixture.HookFile + " ]; then mv " + fixture.HookFile + " " + fixture.HookFile + ".ran; sh " + fixture.HookFile + ".ran; fi\n"
                + "if [ -f left.flag ] && [ -f right.flag ]; then echo 'left.flag and right.flag break the combined tree'; exit 1; fi\n"
                + "exit 0\n").ConfigureAwait(false);
            return fixture;
        }

        private static async Task CommitBranchFileAsync(string repoDir, string branch, string fileName)
        {
            await RunGitAsync(repoDir, "checkout", "-b", branch, "main").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repoDir, fileName), fileName + "\n").ConfigureAwait(false);
            await RunGitAsync(repoDir, "add", fileName).ConfigureAwait(false);
            await RunGitAsync(repoDir, "commit", "-m", "Add " + fileName).ConfigureAwait(false);
            await RunGitAsync(repoDir, "checkout", "main").ConfigureAwait(false);
        }

        private sealed class IntegrationFixture
        {
            public IntegrationFixture(string rootDir)
            {
                SourceDir = Path.Combine(rootDir, "source");
                BareDir = Path.Combine(rootDir, "bare.git");
                WorkingDir = Path.Combine(rootDir, "working");
                CheckScript = Path.Combine(rootDir, "check.sh");
                CounterFile = Path.Combine(rootDir, "check-runs.log");
                HookFile = Path.Combine(rootDir, "check-hook.sh");
            }

            public string SourceDir { get; }
            public string BareDir { get; }
            public string WorkingDir { get; }
            public string CheckScript { get; }
            public string CounterFile { get; }
            public string HookFile { get; }
            public string LeftBranch { get; } = "armada/captain/left";
            public string RightBranch { get; } = "armada/captain/right";
            public string MoverBranch { get; } = "armada/captain/mover";
        }
    }
}
