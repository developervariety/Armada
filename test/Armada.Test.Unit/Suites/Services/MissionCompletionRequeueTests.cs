namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.Suites.Recovery;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// A completion is de-duplicated only against the launch it belongs to. Each test hands a mission
    /// back for another attempt through one requeue path, launches it again, and reports a second
    /// completion a few milliseconds after the first one was handled: the second completion must be
    /// processed, not dropped as a duplicate.
    /// </summary>
    public sealed class MissionCompletionRequeueTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Completion Requeue";

        private const string NoVerdictOutput = "Reviewed the change; the run ended before the verdict line.";
        private const string SafeguardLine =
            "[stderr] API Error: example-model has safety measures that flagged this message for a cybersecurity topic";
        private const string QuotaLine = "[stderr] You've hit your limit and must wait for reset.";
        private const string ReviewedCommit = "1111111111111111111111111111111111111111";
        private const string WorkBranch = "armada/worker/example-work";
        private const string SectionedPass =
            "## Completeness\nEvery requested change is present.\n\n" +
            "## Correctness\nThe change does what the brief asks.\n\n" +
            "## Tests\nThe new tests cover the change.\n\n" +
            "## Failure Modes\nNo unhandled failure mode remains.\n\n" +
            "[ARMADA:VERDICT] PASS";

        private sealed class Rig
        {
            public SqliteDatabaseDriver Db = null!;
            public ArmadaSettings Settings = null!;
            public LoggingModule Logging = null!;
            public CaptainService Captains = null!;
            public MissionService Missions = null!;
            public AdmiralService Admiral = null!;
            public Vessel Vessel = null!;
            public Voyage Voyage = null!;
            public Mission Judge = null!;
            public Captain Primary = null!;
            public Captain Alternate = null!;
            public string Output = NoVerdictOutput;
            public int Launches;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<Rig> CreateRigAsync(TestDatabase testDb, bool withVessel = false)
        {
            string id = Guid.NewGuid().ToString("N");
            Rig rig = new Rig();
            rig.Db = testDb.Driver;
            rig.Logging = CreateLogging();
            rig.Settings = new ArmadaSettings();
            rig.Settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_requeue_docks_" + id);
            rig.Settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_requeue_repos_" + id);
            rig.Settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_requeue_logs_" + id);
            rig.Settings.MinIdleCaptains = 0;

            StubGitService git = new StubGitService();
            IDockService docks = new DockService(rig.Logging, rig.Db, rig.Settings, git);
            rig.Captains = new CaptainService(rig.Logging, rig.Db, rig.Settings, git, docks);
            rig.Missions = new MissionService(rig.Logging, rig.Db, rig.Settings, docks, rig.Captains, git: git);
            rig.Missions.OnGetMissionOutput = _ => rig.Output;
            rig.Admiral = new AdmiralService(rig.Logging, rig.Db, rig.Settings, rig.Captains, rig.Missions,
                new VoyageService(rig.Logging, rig.Db), docks, git: git);

            Vessel vessel = new Vessel("requeue-vessel-" + id, "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            vessel.LocalPath = Path.Combine(rig.Settings.ReposDirectory, "requeue.git");
            rig.Vessel = await rig.Db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("requeue-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            rig.Voyage = await rig.Db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission judge = new Mission("[Judge] Review", "review the example change");
            judge.VoyageId = rig.Voyage.Id;
            judge.Persona = "Judge";
            // Without a vessel no background voyage assignment races the test's own relaunch.
            if (withVessel) judge.VesselId = rig.Vessel.Id;
            rig.Judge = await rig.Db.Missions.CreateAsync(judge).ConfigureAwait(false);

            rig.Primary = await CreateCaptainAsync(rig.Db, "primary-captain", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
            rig.Alternate = await CreateCaptainAsync(rig.Db, "alternate-captain", AgentRuntimeEnum.Codex).ConfigureAwait(false);
            return rig;
        }

        private static async Task<Captain> CreateCaptainAsync(SqliteDatabaseDriver db, string name, AgentRuntimeEnum runtime)
        {
            Captain captain = new Captain(name);
            captain.Runtime = runtime;
            captain.State = CaptainStateEnum.Idle;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        // Launches the mission as assignment does: a new process and a new start time on a working captain.
        private static async Task<int> LaunchAsync(Rig rig, Captain captain, int? processId = null)
        {
            rig.Launches++;
            int pid = processId ?? 91000 + rig.Launches;
            Mission mission = (await rig.Db.Missions.ReadAsync(rig.Judge.Id).ConfigureAwait(false))!;
            mission.Status = MissionStatusEnum.InProgress;
            mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
            mission.CaptainId = captain.Id;
            mission.ProcessId = pid;
            mission.StartedUtc = DateTime.UtcNow.AddMinutes(-5).AddSeconds(rig.Launches);
            await rig.Db.Missions.UpdateAsync(mission).ConfigureAwait(false);

            Captain current = (await rig.Db.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
            current.State = CaptainStateEnum.Working;
            current.CurrentMissionId = mission.Id;
            current.ProcessId = pid;
            current.LastHeartbeatUtc = DateTime.UtcNow;
            await rig.Db.Captains.UpdateAsync(current).ConfigureAwait(false);
            return pid;
        }

        private static Task ExitAsync(Rig rig, Captain captain, int pid, int exitCode)
        {
            return rig.Admiral.HandleProcessExitAsync(pid, exitCode, captain.Id, rig.Judge.Id);
        }

        private static async Task<Mission> ReadJudgeAsync(Rig rig)
        {
            return (await rig.Db.Missions.ReadAsync(rig.Judge.Id).ConfigureAwait(false))!;
        }

        private static async Task WriteMissionLogAsync(Rig rig, string line)
        {
            string dir = Path.Combine(rig.Settings.LogDirectory, "missions");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, rig.Judge.Id + ".log"),
                line + Environment.NewLine + "Agent exited with code 1").ConfigureAwait(false);
        }

        // The first attempt: a Judge that exits without a verdict is re-run in place, so its completion is
        // handled and the mission returns to Pending inside the handler.
        private async Task HandleFirstCompletionAsync(Rig rig)
        {
            int pid = await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
            await ExitAsync(rig, rig.Primary, pid, 0).ConfigureAwait(false);
            Mission afterFirst = await ReadJudgeAsync(rig).ConfigureAwait(false);
            AssertEqual(MissionStatusEnum.Pending, afterFirst.Status, "precondition: the first completion is handled and re-runs the Judge");
        }

        private async Task AssertSecondCompletionProcessedAsync(Rig rig, Captain captain, string path)
        {
            int pid = await LaunchAsync(rig, captain).ConfigureAwait(false);
            await ExitAsync(rig, captain, pid, 0).ConfigureAwait(false);
            Mission after = await ReadJudgeAsync(rig).ConfigureAwait(false);
            AssertTrue(after.Status != MissionStatusEnum.InProgress,
                "after " + path + ", a completion inside the duplicate window must be processed, but the mission is still " + after.Status);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("JudgeCheckHoldCommitChangeRerun_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb, withVessel: true).ConfigureAwait(false);
                    Mission worker = new Mission("[Worker] Implement", "worker description");
                    worker.VesselId = rig.Vessel.Id;
                    worker.VoyageId = rig.Voyage.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.WorkProduced;
                    worker.BranchName = WorkBranch;
                    worker.CommitHash = ReviewedCommit;
                    await rig.Db.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    judge.BranchName = WorkBranch;
                    judge.CommitHash = ReviewedCommit;
                    await rig.Db.Missions.UpdateAsync(judge).ConfigureAwait(false);

                    await rig.Db.CheckRuns.CreateAsync(new CheckRun
                    {
                        VoyageId = rig.Voyage.Id,
                        Label = "Build",
                        Type = CheckRunTypeEnum.Build,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Running,
                        Command = "dotnet build",
                        WorkingDirectory = "C:/temp",
                        BranchName = WorkBranch,
                        CommitHash = ReviewedCommit,
                        StartedUtc = DateTime.UtcNow,
                        Summary = "check"
                    }).ConfigureAwait(false);

                    rig.Output = SectionedPass;
                    int pid = await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    await ExitAsync(rig, rig.Primary, pid, 0).ConfigureAwait(false);
                    Mission held = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    AssertTrue(JudgeCheckWaitHold.IsHeld(held), "precondition: the PASS waits for its Checks: " + held.Status);

                    // The reviewed commit moves while the PASS waits, so the release sweep runs the Judge again.
                    held.CommitHash = "2222222222222222222222222222222222222222";
                    await rig.Db.Missions.UpdateAsync(held).ConfigureAwait(false);
                    AssertEqual(1, await rig.Missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false), "precondition: the moved commit is decided");
                    Mission rerun = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, rerun.Status, "precondition: the Judge re-runs in place");
                    AssertEqual(1, rerun.RecoveryAttempts, "precondition: the re-run counted one attempt");

                    int second = await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    await ExitAsync(rig, rig.Primary, second, 0).ConfigureAwait(false);
                    Mission after = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    AssertTrue(after.Status != MissionStatusEnum.InProgress,
                        "the re-run Judge's completion inside the window must be processed, but the mission is still " + after.Status);
                    AssertTrue(JudgeCheckWaitHold.IsHeld(after), "the second completion held its PASS for the running Check again");
                }
            }).ConfigureAwait(false);

            await RunTest("JudgeMissingVerdictRerun_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);
                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a missing-verdict re-run").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("RefusalContinuationFromCompletion_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    rig.Output = SafeguardLine;
                    int pid = await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    await ExitAsync(rig, rig.Primary, pid, 0).ConfigureAwait(false);
                    Mission continued = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, continued.Status, "precondition: the refusal continues the mission");
                    AssertTrue(PolicyRefusalContinuationService.IsContinuation(continued), "precondition: the mission is a refusal continuation");

                    rig.Output = NoVerdictOutput;
                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a refusal continuation").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("SafeguardBlockExitContinuation_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    int blocked = await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    await WriteMissionLogAsync(rig, SafeguardLine).ConfigureAwait(false);
                    await ExitAsync(rig, rig.Primary, blocked, 1).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, (await ReadJudgeAsync(rig).ConfigureAwait(false)).Status,
                        "precondition: the safeguard block continues the mission");

                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a safeguard-block continuation").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("TransientFailureRequeue_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    int killed = await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    await ExitAsync(rig, rig.Primary, killed, 137).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, (await ReadJudgeAsync(rig).ConfigureAwait(false)).Status,
                        "precondition: a probable resource kill requeues the mission");

                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a transient-failure requeue").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("QuotaReroute_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    int limited = await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    await WriteMissionLogAsync(rig, QuotaLine).ConfigureAwait(false);
                    await ExitAsync(rig, rig.Primary, limited, 1).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, (await ReadJudgeAsync(rig).ConfigureAwait(false)).Status,
                        "precondition: a quota limit re-routes the mission");

                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a quota re-route").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("OperatorRestart_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    Mission failed = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    failed.Status = MissionStatusEnum.Failed;
                    failed.FailureReason = "example failure";
                    failed.CompletedUtc = DateTime.UtcNow;
                    failed.VesselId = rig.Vessel.Id;
                    failed = await rig.Db.Missions.UpdateAsync(failed).ConfigureAwait(false);
                    Captain released = (await rig.Db.Captains.ReadAsync(rig.Primary.Id).ConfigureAwait(false))!;
                    released.State = CaptainStateEnum.Idle;
                    released.CurrentMissionId = null;
                    await rig.Db.Captains.UpdateAsync(released).ConfigureAwait(false);

                    Mission restarted = await new MissionRestartService(rig.Db, rig.Settings, rig.Logging).RestartAsync(failed).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, restarted.Status, "precondition: the operator restart requeues the mission");

                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "an operator restart").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("ReviewDenial_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    Mission review = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    review.Status = MissionStatusEnum.Review;
                    review.RequiresReview = true;
                    await rig.Db.Missions.UpdateAsync(review).ConfigureAwait(false);

                    Mission denied = await rig.Missions.DenyReviewAsync(review.Id, "reviewer", "Rework the example change.").ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, denied.Status, "precondition: a denied review returns the stage for rework");

                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a review denial").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("MergeRecoveryRedispatch_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    await LaunchAsync(rig, rig.Primary).ConfigureAwait(false);
                    Mission landing = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    landing.Status = MissionStatusEnum.LandingFailed;
                    landing.BranchName = WorkBranch;
                    await rig.Db.Missions.UpdateAsync(landing).ConfigureAwait(false);

                    MergeEntry entry = await rig.Db.MergeEntries.CreateAsync(new MergeEntry(WorkBranch, "main")
                    {
                        MissionId = landing.Id,
                        Status = MergeStatusEnum.Failed,
                        MergeFailureClass = MergeFailureClassEnum.StaleBase,
                        MergeFailureSummary = "target advanced",
                        ConflictedFiles = "[]",
                        DiffLineCount = 1
                    }).ConfigureAwait(false);
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(rig.Logging, rig.Db, rig.Settings, new RecoveryRouter(3),
                        new MergeRecoveryHandlerRebasePathTests.StubRebaseCaptainDockSetup(),
                        new MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery());
                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, (await ReadJudgeAsync(rig).ConfigureAwait(false)).Status,
                        "precondition: merge recovery redispatches the mission");

                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a merge-recovery redispatch").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("StaleCaptainReset_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    await LaunchAsync(rig, rig.Primary, processId: 99999931).ConfigureAwait(false);
                    await rig.Admiral.CleanupStaleCaptainsAsync().ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, (await ReadJudgeAsync(rig).ConfigureAwait(false)).Status,
                        "precondition: a captain whose process is gone has its mission reset to Pending");

                    await AssertSecondCompletionProcessedAsync(rig, rig.Alternate, "a stale-captain reset").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("StallRecoveryRelaunch_SecondCompletionInsideWindow_IsProcessed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);

                    string worktree = Path.Combine(rig.Settings.DocksDirectory, "relaunch");
                    Directory.CreateDirectory(worktree);
                    Dock dock = new Dock(rig.Vessel.Id);
                    dock.WorktreePath = worktree;
                    dock.BranchName = WorkBranch;
                    dock = await rig.Db.Docks.CreateAsync(dock).ConfigureAwait(false);

                    await LaunchAsync(rig, rig.Alternate).ConfigureAwait(false);
                    Mission launched = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    DateTime? startedBeforeRelaunch = launched.StartedUtc;
                    launched.DockId = dock.Id;
                    await rig.Db.Missions.UpdateAsync(launched).ConfigureAwait(false);
                    Captain stalled = (await rig.Db.Captains.ReadAsync(rig.Alternate.Id).ConfigureAwait(false))!;
                    stalled.CurrentDockId = dock.Id;
                    await rig.Db.Captains.UpdateAsync(stalled).ConfigureAwait(false);

                    rig.Captains.OnLaunchAgent = (_, _, _) => Task.FromResult(93777);
                    await rig.Captains.TryRecoverAsync(stalled).ConfigureAwait(false);
                    Mission relaunched = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    AssertEqual(93777, relaunched.ProcessId ?? 0, "precondition: stall recovery relaunched the agent in place");
                    AssertEqual(startedBeforeRelaunch, relaunched.StartedUtc, "precondition: an in-place relaunch keeps the start time");

                    await ExitAsync(rig, rig.Alternate, 93777, 0).ConfigureAwait(false);
                    Mission after = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    AssertTrue(after.Status != MissionStatusEnum.InProgress,
                        "after a stall relaunch, a completion inside the duplicate window must be processed, but the mission is still " + after.Status);
                }
            }).ConfigureAwait(false);

            await RunTest("LateDuplicateOfTheHandledLaunch_InsideWindow_IsStillSkipped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Rig rig = await CreateRigAsync(testDb).ConfigureAwait(false);
                    await HandleFirstCompletionAsync(rig).ConfigureAwait(false);
                    Mission requeued = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    string skipListAfterFirst = requeued.RetrySkipCaptainIds ?? "";

                    // The health check reports the same exit again before the mission is launched again.
                    await rig.Missions.HandleCompletionAsync(rig.Primary, rig.Judge.Id).ConfigureAwait(false);

                    Mission after = await ReadJudgeAsync(rig).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, after.Status, "a late duplicate must not complete a requeued mission");
                    AssertEqual(skipListAfterFirst, after.RetrySkipCaptainIds ?? "", "a late duplicate must not run the re-run logic a second time");
                }
            }).ConfigureAwait(false);
        }
    }
}
