namespace Armada.Test.Unit
{
    using System.Diagnostics;
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

            await RunTest("ProcessSingle_FailingTestCommand_LeavesTargetRefsUnchanged", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_testfail_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    await RunGitAsync(repos.BareDir, "config", "--replace-all", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*").ConfigureAwait(false);
                    await RunGitAsync(repos.BareDir, "fetch", "--all", "--prune").ConfigureAwait(false);
                    string initialLocalMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                    string initialOriginMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();
                    AssertEqual(initialOriginMain, initialLocalMain, "Fixture should start with local main matching origin/main");

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mqtestfail-vessel", repos.RemoteDir);
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
                        entry.TestCommand = "exit 7";
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
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "Failing gate command should fail the entry");
                        AssertEqual(7, afterProcess.TestExitCode, "Exit code should be recorded");

                        string localMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        string originMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();

                        AssertEqual(initialLocalMain, localMain, "Bare repo local main must remain at the initial target head");
                        AssertEqual(initialOriginMain, originMain, "Bare repo origin/main must remain at the initial target head");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { /* best-effort */ }
                }
            });

            await RunTest("ProcessSingle_PostPushLocalSyncFailure_RollsBackAdvancedTarget", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_rollback_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GitRepoSetup repos = await CreateGitSetupAsync(rootDir).ConfigureAwait(false);

                    await RunGitAsync(repos.BareDir, "config", "--replace-all", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*").ConfigureAwait(false);
                    await RunGitAsync(repos.BareDir, "fetch", "--all", "--prune").ConfigureAwait(false);
                    string initialLocalMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                    string initialOriginMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();
                    AssertEqual(initialOriginMain, initialLocalMain, "Fixture should start with local main matching origin/main");

                    string checkedOutMainDir = Path.Combine(rootDir, "checked-out-main");
                    await RunGitAsync(rootDir, "--git-dir", repos.BareDir, "worktree", "add", checkedOutMainDir, "main").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);

                        Vessel vessel = new Vessel("mqrollback-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.WorkingDirectory = repos.WorkingDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalOnly;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Mission mission = new Mission("post-push rollback mission");
                        mission.VesselId = vessel.Id;
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.MissionId = mission.Id;
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
                        AssertEqual(MergeStatusEnum.Failed, afterProcess!.Status, "Local target sync failure should fail the entry");

                        string localMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/heads/main").ConfigureAwait(false)).Trim();
                        string originMain = (await RunGitAsync(repos.BareDir, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false)).Trim();

                        AssertEqual(initialLocalMain, localMain, "Bare repo local main must be rolled back to the initial target head");
                        AssertEqual(initialOriginMain, originMain, "Bare repo origin/main must be rolled back to the initial target head");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("merge_queue.failed_target_advanced").ConfigureAwait(false);
                        AssertEqual(1, events.Count, "Exactly one failed-target-advanced event should be persisted");
                        AssertContains(entry.Id, events[0].Payload ?? "", "Audit payload should include entry id");
                        AssertContains(initialOriginMain, events[0].Payload ?? "", "Audit payload should include previous target head");
                        AssertContains("rolled_back", events[0].Payload ?? "", "Audit payload should include rollback result");
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

            await RunTest("ReconcilePullRequest_FiresIndexRefresh_WhenMissionComplete", async () =>
            {
                // Pins the second call site added by the Worker: after the PR reconciler
                // transitions a PullRequestOpen entry to Landed, the code-index refresh
                // must fire for that vessel (mirrors the LandEntryAsync hook).
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = new ArmadaSettings();
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
                    ArmadaSettings settings = new ArmadaSettings();
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
                    ArmadaSettings settings = new ArmadaSettings();
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

            await RunTest("CodeIndexRefreshScheduler_CoalescesRapidRequestsForSameVessel", async () =>
            {
                RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();
                ArmadaSettings settings = CreateSettings();
                settings.CodeIndex.PostLandRefreshDebounceSeconds = 1;
                string vesselId = "vsl_debounce_" + Guid.NewGuid().ToString("N");

                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings.CodeIndex, CreateLogging(), "[test] ", vesselId, "first landing");
                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings.CodeIndex, CreateLogging(), "[test] ", vesselId, "second landing");
                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings.CodeIndex, CreateLogging(), "[test] ", vesselId, "third landing");

                await WaitUntilAsync(() => recordingIndex.HasUpdateForVessel(vesselId), maxAttempts: 40, delayMs: 100).ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);
                AssertEqual(1, recordingIndex.UpdateAsyncVesselIds.Count(id => id == vesselId),
                    "rapid refresh requests for one vessel should be coalesced into one update");
            });
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
