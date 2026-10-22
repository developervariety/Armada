namespace Armada.Test.Unit
{
    using System.Diagnostics;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for <see cref="MergeQueueService"/> code-index refresh hooks after a land.
    /// </summary>
    public sealed class MergeQueueServiceTests : TestSuite
    {
        public override string Name => "Merge Queue Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("LandEntry_FiresIndexRefresh_WhenVesselIdSet", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_idx_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);
                        RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();

                        Vessel vessel = new Vessel("mqidx-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier(),
                            null,
                            recordingIndex);

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "Expected Landed merge entry");

                        await WaitUntilAsync(() => recordingIndex.HasUpdateForVessel(vessel.Id)).ConfigureAwait(false);
                        AssertTrue(recordingIndex.HasUpdateForVessel(vessel.Id), "Code index refresh should observe vessel id");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("LandEntry_DoesNotThrow_WhenCodeIndexServiceNull", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_idx_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mqidx-null-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier());

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "Expected Landed without code index service");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("LandEntry_SynchronizesBareTargetBranchAfterPush", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_sync_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    string initialLocalMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                    string captainHead = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/" + repos.CaptainBranch).ConfigureAwait(false)).Trim();
                    AssertTrue(!String.Equals(initialLocalMain, captainHead, StringComparison.OrdinalIgnoreCase),
                        "Fixture should start with captain branch ahead of local main");

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mqsync-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier());

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "Expected Landed merge entry");

                        string localMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        string originMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();

                        AssertEqual(originMain, localMain, "Bare repo local main must be synchronized to origin/main after land");
                        AssertTrue(!String.Equals(initialLocalMain, localMain, StringComparison.OrdinalIgnoreCase),
                            "Local main should advance from the pre-land commit");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("ProcessSingle_FailsNoOpIdentityMerge", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_noop_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    string noOpBranch = "armada/captain-1/msn_noop";
                    await RunGitAsync(repos.BareDir, "update-ref", "refs/heads/" + noOpBranch, "refs/heads/main").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mqnoop-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Mission mission = new Mission("no-op merge queue mission");
                        mission.VesselId = vessel.Id;
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.MissionId = mission.Id;
                        entry.BranchName = noOpBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier());

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "No-op merge entry must fail, not land");
                        AssertContains("No-op merge queue entry", afterProcess.TestOutput ?? "", "Failure reason should be structured");

                        Mission? readMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(readMission, "Mission should still exist");
                        AssertEqual(MissionStatusEnum.LandingFailed, readMission!.Status, "Linked mission should become LandingFailed");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("ProcessSingle_MissingBranchWhoseCommitAlreadyLanded_IsCancelledNotFailed", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_landed_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    string landedCommit = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mqlanded-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier());

                        // The branch was deleted after its work landed; the mission commit is on main. An earlier
                        // stage on the same branch still reads WorkProduced.
                        Mission earlierStage = new Mission("earlier stage");
                        earlierStage.VesselId = vessel.Id;
                        earlierStage.Status = MissionStatusEnum.WorkProduced;
                        earlierStage.BranchName = "armada/captain-1/msn_gone";
                        earlierStage = await testDb.Driver.Missions.CreateAsync(earlierStage).ConfigureAwait(false);

                        Mission landed = new Mission("landed straggler");
                        landed.VesselId = vessel.Id;
                        landed.Status = MissionStatusEnum.WorkProduced;
                        landed.CommitHash = landedCommit;
                        landed.BranchName = "armada/captain-1/msn_gone";
                        landed.DependsOnMissionId = earlierStage.Id;
                        landed = await testDb.Driver.Missions.CreateAsync(landed).ConfigureAwait(false);

                        MergeEntry landedEntry = new MergeEntry();
                        landedEntry.VesselId = vessel.Id;
                        landedEntry.MissionId = landed.Id;
                        landedEntry.BranchName = "armada/captain-1/msn_gone";
                        landedEntry.TargetBranch = "main";
                        landedEntry.Status = MergeStatusEnum.Queued;
                        landedEntry.CreatedUtc = DateTime.UtcNow;
                        landedEntry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(landedEntry).ConfigureAwait(false);

                        MergeEntry? afterLanded = await service.ProcessSingleAsync(landedEntry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Cancelled, afterLanded!.Status, "an entry whose work already landed is cancelled, not failed");
                        AssertContains("already in main", afterLanded.TestOutput ?? "", "the reason names the landed commit");
                        // The landed work's missions end Complete, so the landing drain does not enqueue them again.
                        Mission? landedAfter = await testDb.Driver.Missions.ReadAsync(landed.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Complete, landedAfter!.Status, "the mission whose commit landed is Complete");
                        Mission? earlierAfter = await testDb.Driver.Missions.ReadAsync(earlierStage.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Complete, earlierAfter!.Status, "an earlier stage on the same branch is Complete");

                        // A missing branch with no landed commit still fails.
                        Mission lost = new Mission("lost branch");
                        lost.VesselId = vessel.Id;
                        lost.Status = MissionStatusEnum.WorkProduced;
                        lost = await testDb.Driver.Missions.CreateAsync(lost).ConfigureAwait(false);

                        MergeEntry lostEntry = new MergeEntry();
                        lostEntry.VesselId = vessel.Id;
                        lostEntry.MissionId = lost.Id;
                        lostEntry.BranchName = "armada/captain-1/msn_lost";
                        lostEntry.TargetBranch = "main";
                        lostEntry.Status = MergeStatusEnum.Queued;
                        lostEntry.CreatedUtc = DateTime.UtcNow;
                        lostEntry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(lostEntry).ConfigureAwait(false);

                        MergeEntry? afterLost = await service.ProcessSingleAsync(lostEntry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Failed, afterLost!.Status, "a missing branch whose work never landed still fails");
                        AssertContains("was not found", afterLost.TestOutput ?? "");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("ReconcilePullRequest_FiresIndexRefresh_WhenMissionComplete", async () =>
            {
                // Pins the second call site added by the Worker: after the PR reconciler
                // transitions a PullRequestOpen entry to Landed, the code-index refresh
                // must fire for that vessel (mirrors the LandEntryAsync hook).
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    // CreateSettings zeroes PostLandRefreshDebounceSeconds. Its 30-second default
                    // is a production coalescing window; waiting it out cost this suite a full
                    // 30 seconds per test while asserting nothing extra.
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();

                    MergeQueueService service = new MergeQueueService(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new MergeFailureClassifier(),
                        null,
                        recordingIndex);

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mqidx-pr-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Mission mergedMission = new Mission("merged via pr", "");
                    mergedMission.VesselId = vessel.Id;
                    mergedMission.Status = MissionStatusEnum.Complete;
                    mergedMission.PrUrl = "https://github.com/test/repo/pull/700";
                    mergedMission = await testDb.Driver.Missions.CreateAsync(mergedMission).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = vessel.Id;
                    entry.MissionId = mergedMission.Id;
                    entry.BranchName = "armada/captain/pr-merged";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.PullRequestOpen;
                    entry.PrUrl = mergedMission.PrUrl;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    int reconciled = await service.ReconcilePullRequestEntriesAsync().ConfigureAwait(false);
                    AssertEqual(1, reconciled, "Reconciler must land the entry whose mission is Complete");

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Landed, readBack!.Status, "Entry should be Landed after reconcile");

                    await WaitUntilAsync(() => recordingIndex.HasUpdateForVessel(vessel.Id)).ConfigureAwait(false);
                    AssertTrue(recordingIndex.HasUpdateForVessel(vessel.Id),
                        "PR reconciler must fire code-index refresh for the landed entry's vessel");
                }
            });

            await RunTest("Reconcile_DoesNotInvokeCodeIndex_WhenVesselIdEmpty", async () =>
            {
                // Pins the FireIndexRefreshForVessel early-return guard: when the landed
                // entry has no VesselId, no UpdateAsync call should ever be scheduled.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    // CreateSettings zeroes PostLandRefreshDebounceSeconds. Its 30-second default
                    // is a production coalescing window; waiting it out cost this suite a full
                    // 30 seconds per test while asserting nothing extra.
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();

                    MergeQueueService service = new MergeQueueService(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new MergeFailureClassifier(),
                        null,
                        recordingIndex);

                    Mission mergedMission = new Mission("merged without vessel", "");
                    mergedMission.Status = MissionStatusEnum.Complete;
                    mergedMission.PrUrl = "https://github.com/test/repo/pull/701";
                    mergedMission = await testDb.Driver.Missions.CreateAsync(mergedMission).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = "";
                    entry.MissionId = mergedMission.Id;
                    entry.BranchName = "armada/captain/no-vessel";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.PullRequestOpen;
                    entry.PrUrl = mergedMission.PrUrl;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    int reconciled = await service.ReconcilePullRequestEntriesAsync().ConfigureAwait(false);
                    AssertEqual(1, reconciled, "Reconciler should still land the entry even without a VesselId");

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Landed, readBack!.Status, "Entry should be Landed regardless of vessel-id");

                    // Give any incorrectly-scheduled background task time to fire.
                    await Task.Delay(500).ConfigureAwait(false);
                    AssertEqual(0, recordingIndex.UpdateAsyncVesselIds.Count,
                        "FireIndexRefreshForVessel must short-circuit when VesselId is empty");
                }
            });

            await RunTest("Reconcile_LandsEntry_WhenCodeIndexUpdateThrows", async () =>
            {
                // Pins the try/catch inside the fire-and-forget Task.Run: a throwing
                // code-index service must not propagate out of the merge-queue tick
                // and must not affect the entry's Landed status.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    ThrowingCodeIndexService throwingIndex = new ThrowingCodeIndexService();

                    MergeQueueService service = new MergeQueueService(
                        logging,
                        testDb.Driver,
                        settings,
                        git,
                        new MergeFailureClassifier(),
                        null,
                        throwingIndex);

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mqidx-throw-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Mission mergedMission = new Mission("merged into throwing index", "");
                    mergedMission.VesselId = vessel.Id;
                    mergedMission.Status = MissionStatusEnum.Complete;
                    mergedMission.PrUrl = "https://github.com/test/repo/pull/702";
                    mergedMission = await testDb.Driver.Missions.CreateAsync(mergedMission).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = vessel.Id;
                    entry.MissionId = mergedMission.Id;
                    entry.BranchName = "armada/captain/throwing";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.PullRequestOpen;
                    entry.PrUrl = mergedMission.PrUrl;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    int reconciled = await service.ReconcilePullRequestEntriesAsync().ConfigureAwait(false);
                    AssertEqual(1, reconciled, "Reconciler must land the entry even when the refresh throws");

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Landed, readBack!.Status,
                        "A throwing code-index refresh must not flip the entry status");

                    await WaitUntilAsync(() => throwingIndex.UpdateAttempts > 0).ConfigureAwait(false);
                    AssertTrue(throwingIndex.UpdateAttempts > 0,
                        "UpdateAsync should have been attempted before throwing");
                }
            });

            await RunTest("ProcessSingle_BoundaryViolation_FailsBeforeTestsRun", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_boundary_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    // Captain branch modifies a built-in protected path (_briefing/spec.md)
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "_briefing/spec.md").ConfigureAwait(false);
                    string markerPath = Path.Combine(rootDir, "tests-ran.txt");

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mq-boundary-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.TestCommand = BuildMarkerCommand(markerPath);
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(
                            logging,
                            testDb.Driver,
                            settings,
                            git,
                            new MergeFailureClassifier());

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "Boundary violation must fail the entry");
                        AssertFalse(File.Exists(markerPath), "Boundary validation must block before merge-queue tests run");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });
            await RunTest("ProcessSingle_TestCommandFillingStderrWithStdoutOpen_CompletesAndReleasesTestLock", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_stderr_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mq-stderr-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        // Far more stderr than a pipe buffer holds, written while stdout stays open. A
                        // runner that reads stdout to the end before stderr waits forever here.
                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.TestCommand = "yes stderr-line | head -c 1048576 1>&2; echo tests-done";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        Task<MergeEntry?> processing = service.ProcessSingleAsync(entry.Id);
                        Task finished = await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false);
                        AssertTrue(finished == processing, "A child filling stderr while stdout is open must not block the merge-queue test run");

                        MergeEntry? afterProcess = await processing.ConfigureAwait(false);
                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "Passing tests still land the entry");

                        using (CancellationTokenSource lockWait = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        using (await HostWideCommandLock.AcquireAsync(lockWait.Token).ConfigureAwait(false))
                        {
                            // Acquired: the test run released the host lock.
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            // A post-merge test failure is surfaced to the operator, never routed to a rebase captain: the
            // test context the queue records carries no git exit code, so it classifies as a surfaced test
            // failure.
            // The landing sweep must not repeat a step the entry's own processing pass is taking: a second
            // test run of an entry in Testing outlives the landing and then fails the landed entry.
            await RunTest("ReconcileLandingStateMachine_SkipsAnEntryWhoseVesselAProcessingPassHolds", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_sweep_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    string runs = Path.Combine(rootDir, "test-runs.txt");

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mq-sweep-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.TestCommand = "echo run >> '" + runs + "'; sleep 3; echo tests-done";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        Task<MergeEntry?> processing = service.ProcessSingleAsync(entry.Id);

                        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                        while (!File.Exists(runs))
                        {
                            AssertTrue(DateTime.UtcNow < deadline, "the processing pass never started its test run");
                            await Task.Delay(50).ConfigureAwait(false);
                        }

                        Task<int> sweep = service.ReconcileLandingStateMachineAsync();
                        MergeEntry? landed = await processing.ConfigureAwait(false);
                        Task sweepDone = await Task.WhenAny(sweep, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
                        AssertTrue(ReferenceEquals(sweepDone, sweep), "the sweep finishes");

                        AssertEqual(MergeStatusEnum.Landed, landed!.Status, "the processing pass lands the entry");
                        AssertEqual(1, File.ReadAllLines(runs).Length, "the sweep does not start a second test run of the entry");
                        MergeEntry? after = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, after!.Status, "the landed entry stays landed");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("ProcessSingle_LandedEntry_RecordsFetchLocksSlotValidationLandingAndProcessedCount", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_metrics_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mq-metrics-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.TestCommand = "echo tests-done";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        using (ArmadaMetricsRecorder metrics = new ArmadaMetricsRecorder())
                        {
                            MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                            AssertNotNull(afterProcess, "Entry after process");
                            AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "The entry lands");

                            AssertTrue(metrics.Of("armada.mergequeue.processed").Any(m => m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeLanded)),
                                "the landed entry is counted as processed with its terminal state");
                            AssertTrue(metrics.Of("armada.landing.seconds").Any(m => m.Has(ArmadaMetrics.TagPath, ArmadaMetrics.LandingMergeQueue)
                                && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeLanded)
                                && m.Has(ArmadaMetrics.TagRetried, "false")),
                                "the landing pass is recorded as landed on the first attempt");
                            AssertTrue(metrics.Of("armada.validation.seconds").Any(m => m.Has(ArmadaMetrics.TagKind, ArmadaMetrics.ValidationMergeQueueTest)
                                && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomePassed)),
                                "the merge-queue test command is recorded as passed");
                            AssertTrue(metrics.Of("armada.hostslot.wait.seconds").Any(m => m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeGranted)),
                                "the test command's host-slot wait is recorded");
                            AssertTrue(metrics.Of("armada.git.fetch.seconds").Any(m => m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeSuccess)),
                                "the integration fetch is recorded");
                            List<RecordedMeasurement> lockWaits = metrics.Of("armada.git.repository_lock.wait.seconds");
                            AssertTrue(lockWaits.Any(m => m.Has(ArmadaMetrics.TagLock, ArmadaMetrics.LockVesselLanding) && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeAcquired)),
                                "the vessel landing lock wait is recorded");
                            AssertTrue(lockWaits.Any(m => m.Has(ArmadaMetrics.TagLock, ArmadaMetrics.LockGitWorktree) && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeAcquired)),
                                "the integration worktree's repository-lock wait is recorded");
                            foreach (RecordedMeasurement measurement in metrics.All())
                            {
                                foreach (KeyValuePair<string, string> tag in measurement.Tags)
                                {
                                    AssertFalse(tag.Value.Contains(entry.Id, StringComparison.Ordinal) || tag.Value.Contains(vessel.Id, StringComparison.Ordinal),
                                        "no label carries an entry or vessel id: " + measurement.Instrument + " " + tag.Key);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("ProcessSingle_PostMergeTestFailure_IsSurfacedNeverRoutedToARebaseCaptain", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_testfail_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mq-testfail-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.TestCommand = "echo '  Failed Api.Routes.DeleteVessel [3 ms]'; echo 'Failed: 1, Passed: 40'; exit 1";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        MergeEntry? afterProcess;
                        using (ArmadaMetricsRecorder metrics = new ArmadaMetricsRecorder())
                        {
                            afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);
                            AssertTrue(metrics.Of("armada.validation.seconds").Any(m => m.Has(ArmadaMetrics.TagKind, ArmadaMetrics.ValidationMergeQueueTest)
                                && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeFailed)),
                                "the failing test command is recorded as failed");
                            AssertTrue(metrics.Of("armada.mergequeue.processed").Any(m => m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeFailed)),
                                "the failed entry is counted as processed with its terminal state");
                        }

                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "A failing post-merge test run fails the entry");
                        AssertEqual(MergeFailureClassEnum.TestFailureBeforeMerge, afterProcess.MergeFailureClass,
                            "The recorded test context carries no git exit code, so it classifies as a surfaced test failure");
                        RecoveryAction action = new RecoveryRouter().Route(afterProcess.MergeFailureClass!.Value, false, 0);
                        AssertTrue(action is RecoveryAction.Surface, "A post-merge test failure is surfaced to the operator, got " + action.GetType().Name);
                        AssertFalse(action is RecoveryAction.RebaseCaptain, "A post-merge test failure is never routed to a rebase captain");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("ProcessSingle_TestCommandPastTimeout_StopsProcessTreeAndFailsEntry", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_timeout_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    string pidFile = Path.Combine(rootDir, "child.pid");

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.MergeQueueTestTimeoutSeconds = 2;
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mq-timeout-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        string preRemoteHead = (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.TestCommand = "sleep 300 & echo $! > '" + pidFile + "'; wait";
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());
                        MergeEntry? afterProcess;
                        using (ArmadaMetricsRecorder metrics = new ArmadaMetricsRecorder())
                        {
                            Task<MergeEntry?> processing = service.ProcessSingleAsync(entry.Id);
                            Task finished = await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false);
                            AssertTrue(finished == processing, "A test command past its timeout must be stopped");

                            afterProcess = await processing.ConfigureAwait(false);
                            AssertTrue(metrics.Of("armada.validation.seconds").Any(m => m.Has(ArmadaMetrics.TagKind, ArmadaMetrics.ValidationMergeQueueTest)
                                && m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeTimeout)),
                                "the timed-out test command is recorded as a timeout");
                        }

                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "A timed-out test run fails the entry");
                        AssertContains("merge_queue_test_timeout", afterProcess.TestOutput ?? "", "The failure names the timeout");
                        AssertEqual(preRemoteHead, (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(),
                            "A timed-out test run does not land");

                        AssertTrue(File.Exists(pidFile), "The test command started its child");
                        int childPid = Int32.Parse((await File.ReadAllTextAsync(pidFile).ConfigureAwait(false)).Trim());
                        bool childGone = false;
                        for (int attempt = 0; attempt < 50 && !childGone; attempt++)
                        {
                            try
                            {
                                using (Process child = Process.GetProcessById(childPid))
                                {
                                    childGone = child.HasExited;
                                }
                            }
                            catch (ArgumentException)
                            {
                                childGone = true;
                            }

                            if (!childGone) await Task.Delay(100).ConfigureAwait(false);
                        }
                        AssertTrue(childGone, "The timeout kills the whole process tree, including the command's child");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            // The queue measures the merged candidate, not the branch. A branch that passes alone but breaks
            // the target once merged with newer target work fails the entry by name and publishes nothing.
            await RunTest("ProcessSingle_CombinedTreeFailsChecks_FailsEntryAndLeavesTargetsUnchanged", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_icv_red_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir, "right.flag").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(repos.WorkingDir, "left.flag"), "left\n").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "add", "left.flag").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "commit", "-m", "Add left.flag").ConfigureAwait(false);
                    await RunGitAsync(repos.WorkingDir, "push", "origin", "main").ConfigureAwait(false);
                    string remoteBefore = (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                    QueueCheckFixture checks = await CreateQueueCheckFixtureAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        Vessel vessel = await CreateQueueVesselAsync(testDb.Driver, repos, "mq-icv-red-vessel").ConfigureAwait(false);
                        await CreateQueueCheckProfileAsync(testDb.Driver, vessel, checks).ConfigureAwait(false);
                        MergeEntry entry = await CreateQueuedEntryAsync(testDb.Driver, vessel, repos).ConfigureAwait(false);
                        MergeQueueService service = CreateValidatedQueue(logging, testDb.Driver, settings);

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);

                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "A failing combined tree fails the entry");
                        AssertStartsWith("integration_validation_failed:", afterProcess.TestOutput ?? "", "The refusal is named");
                        AssertEqual(remoteBefore, (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(),
                            "Nothing is published to the remote target");
                        AssertEqual(remoteBefore, (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(),
                            "The local target holds no unpublished candidate");
                        AssertEqual(1, CountQueueCheckRuns(checks), "The check ran once, against the merged candidate");

                        List<IntegrationValidationResult> evidence = await ReadEntryEvidenceAsync(testDb.Driver, entry.Id).ConfigureAwait(false);
                        AssertEqual(1, evidence.Count, "The refusal is recorded as evidence");
                        AssertEqual(IntegrationValidationOutcomeEnum.Failed, evidence[0].Outcome, "The evidence names the failure");
                        AssertEqual(remoteBefore, evidence[0].Candidate.TargetBaseCommit, "The evidence names the target base");
                        AssertEqual(1, evidence[0].CheckRunIds.Count, "The evidence links the deciding check");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            // An entry with no merge-queue test command and no Build or UnitTest command in any profile cannot be
            // measured. It fails by name instead of landing unmeasured.
            await RunTest("ProcessSingle_NoRequiredCommandAnywhere_FailsByNameInsteadOfLanding", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_icv_missing_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    string remoteBefore = (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        Vessel vessel = await CreateQueueVesselAsync(testDb.Driver, repos, "mq-icv-missing-vessel").ConfigureAwait(false);
                        MergeEntry entry = await CreateQueuedEntryAsync(testDb.Driver, vessel, repos).ConfigureAwait(false);
                        MergeQueueService service = CreateValidatedQueue(logging, testDb.Driver, settings);

                        MergeEntry? afterProcess = await service.ProcessSingleAsync(entry.Id).ConfigureAwait(false);

                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "An unmeasurable candidate does not land");
                        AssertStartsWith("integration_validation_command_missing:", afterProcess.TestOutput ?? "", "The missing command is named");
                        AssertEqual(remoteBefore, (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(),
                            "Nothing is published to the remote target");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            // A remote target that moves after the candidate was measured makes that candidate stale. The queue does
            // not publish it; it rebuilds the candidate on the new target, measures it, and publishes that one.
            await RunTest("ProcessSingle_RemoteTargetMovesAfterValidation_RebuildsAndRevalidatesBeforePublishing", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_icv_drift_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    string moverDir = Path.Combine(rootDir, "mover");
                    await RunGitAsync(rootDir, "clone", repos.RemoteDir, moverDir).ConfigureAwait(false);
                    await RunGitAsync(moverDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(moverDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(moverDir, "mover.txt"), "mover\n").ConfigureAwait(false);
                    await RunGitAsync(moverDir, "add", "mover.txt").ConfigureAwait(false);
                    await RunGitAsync(moverDir, "commit", "-m", "Move the target").ConfigureAwait(false);
                    string moverTip = (await RunGitAsync(moverDir, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    string captainHead = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/" + repos.CaptainBranch).ConfigureAwait(false)).Trim();

                    QueueCheckFixture checks = await CreateQueueCheckFixtureAsync(rootDir).ConfigureAwait(false);
                    await File.WriteAllTextAsync(checks.HookFile, "git -C " + moverDir + " push origin " + moverTip + ":refs/heads/main\n").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.MaxLandingRetries = 2;
                        Vessel vessel = await CreateQueueVesselAsync(testDb.Driver, repos, "mq-icv-drift-vessel").ConfigureAwait(false);
                        await CreateQueueCheckProfileAsync(testDb.Driver, vessel, checks).ConfigureAwait(false);
                        MergeEntry entry = await CreateQueuedEntryAsync(testDb.Driver, vessel, repos).ConfigureAwait(false);
                        MergeQueueService service = CreateValidatedQueue(logging, testDb.Driver, settings);

                        MergeEntry? afterProcess = await ProcessUntilSettledAsync(service, testDb.Driver, entry.Id).ConfigureAwait(false);

                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "The rebuilt candidate lands: " + (afterProcess.TestOutput ?? ""));
                        LandingJob? job = await testDb.Driver.LandingJobs.ReadByMergeEntryAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(1, job?.RetryCount ?? -1, "The stale candidate cost exactly one bounded retry");
                        AssertEqual(2, CountQueueCheckRuns(checks), "The stale and the rebuilt candidate were each measured");

                        string remoteAfter = (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        await RunGitAsync(repos.RemoteDir, "merge-base", "--is-ancestor", moverTip, remoteAfter).ConfigureAwait(false);
                        await RunGitAsync(repos.RemoteDir, "merge-base", "--is-ancestor", captainHead, remoteAfter).ConfigureAwait(false);

                        List<IntegrationValidationResult> evidence = await ReadEntryEvidenceAsync(testDb.Driver, entry.Id).ConfigureAwait(false);
                        AssertEqual(2, evidence.Count, "Each candidate has its own evidence");
                        AssertNotEqual(evidence[0].Candidate.IntegrationCommit, evidence[1].Candidate.IntegrationCommit,
                            "Evidence for one candidate is never reused for another");
                        AssertEqual(remoteAfter, evidence[1].Candidate.IntegrationCommit, "The published commit is the one the latest evidence measured");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            // Publication reads the recorded evidence, so a fresh service after a restart publishes only the commit
            // the evidence measured. An integration branch that gained an unmeasured commit is rebuilt and measured.
            await RunTest("Reconcile_AfterRestartEvidenceNamesAnotherCandidate_RebuildsInsteadOfPublishing", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_icv_restart_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);
                    QueueCheckFixture checks = await CreateQueueCheckFixtureAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.MaxLandingRetries = 2;
                        Vessel vessel = await CreateQueueVesselAsync(testDb.Driver, repos, "mq-icv-restart-vessel").ConfigureAwait(false);
                        await CreateQueueCheckProfileAsync(testDb.Driver, vessel, checks).ConfigureAwait(false);
                        MergeEntry entry = await CreateQueuedEntryAsync(testDb.Driver, vessel, repos).ConfigureAwait(false);

                        MergeQueueService beforeRestart = CreateValidatedQueue(logging, testDb.Driver, settings);
                        MergeEntry? current = null;
                        for (int step = 0; step < 6; step++)
                        {
                            await beforeRestart.ReconcileLandingStateMachineAsync().ConfigureAwait(false);
                            current = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                            if (current == null || current.Status == MergeStatusEnum.Passed || current.Status == MergeStatusEnum.Failed) break;
                        }
                        AssertNotNull(current, "Entry before restart");
                        AssertEqual(MergeStatusEnum.Passed, current!.Status, "The candidate is measured and waits to be published: " + (current.TestOutput ?? ""));

                        string integrationPath = Path.Combine(settings.DocksDirectory, "_merge-queue", entry.Id);
                        await RunGitAsync(integrationPath, "commit", "--allow-empty", "-m", "Unmeasured commit").ConfigureAwait(false);
                        string unmeasured = (await RunGitAsync(integrationPath, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                        MergeQueueService afterRestart = CreateValidatedQueue(logging, testDb.Driver, settings);
                        MergeEntry? afterProcess = await ProcessUntilSettledAsync(afterRestart, testDb.Driver, entry.Id).ConfigureAwait(false);

                        AssertNotNull(afterProcess, "Entry after process");
                        AssertEqual(MergeStatusEnum.Landed, afterProcess!.Status, "The rebuilt candidate lands: " + (afterProcess.TestOutput ?? ""));
                        string captainHead = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/" + repos.CaptainBranch).ConfigureAwait(false)).Trim();
                        string published = await RunGitAsync(repos.RemoteDir, "rev-list", "refs/heads/main").ConfigureAwait(false);
                        AssertFalse(published.Contains(unmeasured, StringComparison.OrdinalIgnoreCase), "The unmeasured commit is never published");
                        AssertEqual(captainHead, (await RunGitAsync(repos.RemoteDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim(),
                            "The rebuilt fast-forward candidate is published");

                        // The rebuilt candidate is the commit that was measured before the restart, so its passing
                        // check decides it again; the unmeasured commit was never measured and never decides anything.
                        AssertEqual(1, CountQueueCheckRuns(checks), "The measured commit's passing check is reused, not run twice");
                        List<IntegrationValidationResult> evidence = await ReadEntryEvidenceAsync(testDb.Driver, entry.Id).ConfigureAwait(false);
                        AssertEqual(2, evidence.Count, "The rebuilt candidate has its own evidence");
                        AssertTrue(evidence.All(e => e.Candidate.IntegrationCommit == captainHead), "No evidence names the unmeasured commit");
                        AssertEqual(1, evidence[1].ReusedCheckRunIds.Count, "The rebuilt candidate reused the check that measured that commit");
                        LandingJob? job = await testDb.Driver.LandingJobs.ReadByMergeEntryAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(1, job?.RetryCount ?? -1, "The rebuild is counted against the bounded retry");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });
        }

        private static MergeQueueService CreateValidatedQueue(LoggingModule logging, DatabaseDriver database, ArmadaSettings settings)
        {
            MergeQueueService service = new MergeQueueService(logging, database, settings, new GitService(logging), new MergeFailureClassifier());
            WorkflowProfileService profiles = new WorkflowProfileService(database, logging);
            VesselReadinessService readiness = new VesselReadinessService(database, profiles, logging);
            CheckRunService checkRuns = new CheckRunService(database, profiles, readiness, logging);
            service.SetIntegrationValidator(new IntegrationCandidateValidator(logging, database, settings, checkRuns, profiles));
            return service;
        }

        private static async Task<Vessel> CreateQueueVesselAsync(DatabaseDriver database, GitRepoSetup repos, string name)
        {
            Vessel vessel = new Vessel(name, repos.RemoteDir);
            vessel.LocalPath = repos.BareDir;
            vessel.WorkingDirectory = repos.WorkingDir;
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<MergeEntry> CreateQueuedEntryAsync(DatabaseDriver database, Vessel vessel, GitRepoSetup repos)
        {
            MergeEntry entry = new MergeEntry();
            entry.VesselId = vessel.Id;
            entry.BranchName = repos.CaptainBranch;
            entry.TargetBranch = "main";
            entry.Status = MergeStatusEnum.Queued;
            entry.CreatedUtc = DateTime.UtcNow;
            entry.LastUpdateUtc = DateTime.UtcNow;
            return await database.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
        }

        private static async Task CreateQueueCheckProfileAsync(DatabaseDriver database, Vessel vessel, QueueCheckFixture checks)
        {
            await database.WorkflowProfiles.CreateAsync(new WorkflowProfile
            {
                Name = "Queue Integration Check Profile",
                Scope = WorkflowProfileScopeEnum.Vessel,
                VesselId = vessel.Id,
                UnitTestCommand = "sh " + checks.CheckScript,
                IsDefault = true,
                Active = true
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// A check that fails only when both <c>left.flag</c> and <c>right.flag</c> are in the tree, counts its runs,
        /// and runs a one-shot hook script when one is present.
        /// </summary>
        private static async Task<QueueCheckFixture> CreateQueueCheckFixtureAsync(string rootDir)
        {
            QueueCheckFixture checks = new QueueCheckFixture(rootDir);
            await File.WriteAllTextAsync(checks.CheckScript,
                "echo run >> " + checks.CounterFile + "\n"
                + "if [ -f " + checks.HookFile + " ]; then mv " + checks.HookFile + " " + checks.HookFile + ".ran; sh " + checks.HookFile + ".ran; fi\n"
                + "if [ -f left.flag ] && [ -f right.flag ]; then echo 'left.flag and right.flag break the combined tree'; exit 1; fi\n"
                + "exit 0\n").ConfigureAwait(false);
            return checks;
        }

        private static int CountQueueCheckRuns(QueueCheckFixture checks)
        {
            return File.Exists(checks.CounterFile) ? File.ReadAllLines(checks.CounterFile).Length : 0;
        }

        private static async Task<List<IntegrationValidationResult>> ReadEntryEvidenceAsync(DatabaseDriver database, string entryId)
        {
            List<ArmadaEvent> events = await database.Events.EnumerateByEntityAsync("merge_entry", entryId, 50).ConfigureAwait(false);
            List<IntegrationValidationResult> results = new List<IntegrationValidationResult>();
            foreach (ArmadaEvent evt in events.Where(e => e.EventType == IntegrationCandidateValidator.EvidenceEventType).OrderBy(e => e.CreatedUtc))
            {
                IntegrationValidationResult? result = JsonSerializer.Deserialize<IntegrationValidationResult>(evt.Payload ?? "null");
                if (result != null) results.Add(result);
            }
            return results;
        }

        private static async Task<MergeEntry?> ProcessUntilSettledAsync(MergeQueueService service, DatabaseDriver database, string entryId)
        {
            MergeEntry? current = null;
            for (int pass = 0; pass < 3; pass++)
            {
                await service.ProcessSingleAsync(entryId).ConfigureAwait(false);
                current = await database.MergeEntries.ReadAsync(entryId).ConfigureAwait(false);
                if (current == null
                    || current.Status == MergeStatusEnum.Landed
                    || current.Status == MergeStatusEnum.Failed
                    || current.Status == MergeStatusEnum.Cancelled)
                {
                    break;
                }
            }
            return current;
        }

        private sealed class QueueCheckFixture
        {
            public QueueCheckFixture(string rootDir)
            {
                CheckScript = Path.Combine(rootDir, "check.sh");
                CounterFile = Path.Combine(rootDir, "check-runs.log");
                HookFile = Path.Combine(rootDir, "check-hook.sh");
            }

            public string CheckScript { get; }
            public string CounterFile { get; }
            public string HookFile { get; }
        }

        private static string BuildMarkerCommand(string markerPath)
        {
            string escaped = markerPath.Replace("'", "''", StringComparison.Ordinal);
            return "pwsh -NoProfile -Command \"Set-Content -LiteralPath '" + escaped + "' -Value ran\"";
        }

        /// <summary>
        /// Hand-rolled <see cref="ICodeIndexService"/> double that throws on
        /// <see cref="UpdateAsync"/> -- exercises the fire-and-forget catch block.
        /// </summary>
        private sealed class ThrowingCodeIndexService : ICodeIndexService
        {
            private int _UpdateAttempts;

            public int UpdateAttempts => System.Threading.Volatile.Read(ref _UpdateAttempts);

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId ?? "" });

            public Task<bool> IsIndexedAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(true);

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
            {
                System.Threading.Interlocked.Increment(ref _UpdateAttempts);
                throw new InvalidOperationException("simulated code-index refresh failure");
            }

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeSearchResponse());

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetCodeSearchResponse());

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new ContextPackResponse());

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetContextPackResponse());

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphSymbolSearchResponse());

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphImpactResponse());

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphAffectedTestsResponse());

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);
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
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_idx_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_idx_repos_" + Guid.NewGuid().ToString("N"));
            settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
            settings.CodeIndex.PostLandRefreshDebounceSeconds = 0;
            return settings;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int maxAttempts = 300, int delayMs = 100)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (condition()) return;
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Timed out waiting for background code-index refresh");
        }

        private static async Task<GitRepoSetup> CreateGitSetupAsync(string rootDir, string captainFilePath = "feature.txt")
        {
            string remoteDir = Path.Combine(rootDir, "remote.git");
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");
            string workingDir = Path.Combine(rootDir, "working");

            Directory.CreateDirectory(sourceDir);
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "receive.denyCurrentBranch", "ignore").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(sourceDir, "README.md"), "# test\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string captainBranch = "armada/captain-1/msn_idx001";
            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
            string captainFileAbsolutePath = Path.Combine(sourceDir, captainFilePath);
            string? captainFileDirectory = Path.GetDirectoryName(captainFileAbsolutePath);
            if (!String.IsNullOrEmpty(captainFileDirectory))
            {
                Directory.CreateDirectory(captainFileDirectory);
            }
            await File.WriteAllTextAsync(captainFileAbsolutePath, "feature\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", captainFilePath).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Add feature").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "remote", "add", "origin", remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", captainBranch).ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", remoteDir, bareDir).ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", remoteDir, workingDir).ConfigureAwait(false);
            await RunGitAsync(workingDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(workingDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            return new GitRepoSetup(remoteDir, bareDir, workingDir, captainBranch);
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

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }

                return stdout;
            }
        }

        private sealed class GitRepoSetup
        {
            public string RemoteDir { get; }
            public string BareDir { get; }
            public string WorkingDir { get; }
            public string CaptainBranch { get; }

            public GitRepoSetup(string remoteDir, string bareDir, string workingDir, string captainBranch)
            {
                RemoteDir = remoteDir;
                BareDir = bareDir;
                WorkingDir = workingDir;
                CaptainBranch = captainBranch;
            }
        }
    }
}
