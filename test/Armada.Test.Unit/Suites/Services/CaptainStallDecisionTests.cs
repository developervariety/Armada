namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// A quiet captain is stalled only when its output, its dock worktree and its branch tip are all
    /// older than the stall window. Covers both decision paths: the autonomous recovery Mail nudge and
    /// the admiral heartbeat-stall kill and restart.
    /// </summary>
    public sealed class CaptainStallDecisionTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Stall Decision";

        private const string ClearedEvent = "captain.stall_cleared";
        private const string ConfirmedEvent = "captain.stall_confirmed";
        private const string Branch = "armada/worker/example-stall";

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private sealed class Scene
        {
            public SqliteDatabaseDriver Db = null!;
            public ArmadaSettings Settings = null!;
            public StubGitService Git = null!;
            public Vessel Vessel = null!;
            public Mission Mission = null!;
            public Captain Captain = null!;
            public Dock Dock = null!;
            public string Worktree = "";
        }

        private enum DockActivity
        {
            Recent,
            Old,
            OnlyGitRecent
        }

        private static async Task<Scene> CreateSceneAsync(TestDatabase testDb, DockActivity activity, DateTime heartbeatUtc, int processId = 0)
        {
            string id = Guid.NewGuid().ToString("N");
            Scene scene = new Scene();
            scene.Db = testDb.Driver;
            scene.Git = new StubGitService();
            scene.Settings = new ArmadaSettings();
            scene.Settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_stall_docks_" + id);
            scene.Settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_stall_repos_" + id);
            scene.Settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_stall_logs_" + id);
            scene.Settings.MinIdleCaptains = 0;
            scene.Settings.StallThresholdMinutes = 10;
            scene.Settings.AutonomousRecovery = new AutonomousRecoverySettings
            {
                SendStallMailNudges = true,
                StallMailNudgeThresholdRatio = 0.5,
                StallMailNudgeCooldownMinutes = 30
            };

            Vessel vessel = new Vessel("stall-vessel-" + id, "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            vessel.LocalPath = Path.Combine(scene.Settings.ReposDirectory, "stall.git");
            scene.Vessel = await scene.Db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            scene.Worktree = Path.Combine(scene.Settings.DocksDirectory, "stall-worktree");
            WriteWorktree(scene.Worktree, activity);

            Dock dock = new Dock(scene.Vessel.Id);
            dock.WorktreePath = scene.Worktree;
            dock.BranchName = Branch;
            scene.Dock = await scene.Db.Docks.CreateAsync(dock).ConfigureAwait(false);

            Mission mission = new Mission("Quiet worker", "Port the example reader.");
            mission.VesselId = scene.Vessel.Id;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.InProgress;
            mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
            mission.BranchName = Branch;
            mission.DockId = scene.Dock.Id;
            mission.StartedUtc = DateTime.UtcNow.AddMinutes(-40);
            if (processId != 0) mission.ProcessId = processId;
            scene.Mission = await scene.Db.Missions.CreateAsync(mission).ConfigureAwait(false);

            Captain captain = new Captain("quiet-captain-" + id.Substring(0, 8));
            captain.Runtime = AgentRuntimeEnum.Cursor;
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = scene.Mission.Id;
            captain.CurrentDockId = scene.Dock.Id;
            captain.LastHeartbeatUtc = heartbeatUtc;
            if (processId != 0) captain.ProcessId = processId;
            scene.Captain = await scene.Db.Captains.CreateAsync(captain).ConfigureAwait(false);
            return scene;
        }

        // Builds a dock worktree. Old entries are dated an hour back; a .git entry is always current,
        // because git metadata is not evidence of the captain's work.
        private static void WriteWorktree(string root, DockActivity activity)
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            string file = Path.Combine(src, "ExampleReader.cs");
            File.WriteAllText(file, "// example");
            string git = Path.Combine(root, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "index"), "index");

            if (activity == DockActivity.Recent) return;

            DateTime old = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(file, old);
            Directory.SetLastWriteTimeUtc(src, old);
            Directory.SetLastWriteTimeUtc(root, old);
        }

        // Creates a bare repository whose Branch tip has the given committer time, and points the
        // scene's vessel at it.
        private static async Task UseRealRepositoryAsync(Scene scene, DateTime tipCommittedUtc)
        {
            string root = Path.Combine(scene.Settings.ReposDirectory, "real");
            string work = Path.Combine(root, "work");
            string bare = Path.Combine(root, "stall.git");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(bare);
            await GitServiceTests.RunGitAsync(bare, "init", "--bare", "-b", "main").ConfigureAwait(false);
            await GitServiceTests.RunGitAsync(work, "init", "-b", "main").ConfigureAwait(false);
            await GitServiceTests.RunGitAsync(work, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await GitServiceTests.RunGitAsync(work, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            File.WriteAllText(Path.Combine(work, "ExampleReader.cs"), "// example");
            await GitServiceTests.RunGitAsync(work, "add", "ExampleReader.cs").ConfigureAwait(false);
            string gitDate = "@" + new DateTimeOffset(tipCommittedUtc).ToUnixTimeSeconds() + " +0000";
            await GitServiceTests.RunGitAsync(work, new Dictionary<string, string>
            {
                ["GIT_COMMITTER_DATE"] = gitDate,
                ["GIT_AUTHOR_DATE"] = gitDate
            }, "commit", "-m", "Port the example reader").ConfigureAwait(false);
            await GitServiceTests.RunGitAsync(work, "push", bare, "HEAD:refs/heads/" + Branch).ConfigureAwait(false);

            scene.Vessel.LocalPath = bare;
            scene.Vessel = await scene.Db.Vessels.UpdateAsync(scene.Vessel).ConfigureAwait(false);
        }

        private static AutonomousRecoveryOrchestrator CreateOrchestrator(Scene scene, IGitService? git = null)
        {
            LoggingModule logging = CreateLogging();
            return new AutonomousRecoveryOrchestrator(
                scene.Db,
                CreateAdmiral(scene, new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git))),
                new IncidentService(scene.Db),
                new RunbookService(scene.Db, logging),
                scene.Settings,
                logging,
                null,
                git ?? scene.Git,
                null,
                null,
                null,
                null);
        }

        private static AdmiralService CreateAdmiral(Scene scene, CaptainService captains, DispatchHold? hold = null, IResourcePressureAdmission? admission = null)
        {
            LoggingModule logging = CreateLogging();
            IDockService docks = new DockService(logging, scene.Db, scene.Settings, scene.Git);
            MissionService missions = new MissionService(logging, scene.Db, scene.Settings, docks, captains, git: scene.Git, dispatchHold: hold);
            return new AdmiralService(logging, scene.Db, scene.Settings, captains, missions, new VoyageService(logging, scene.Db), docks,
                resourcePressureAdmission: admission, git: scene.Git, dispatchHold: hold);
        }

        // Admits everything and counts out-of-memory marks.
        private sealed class CountingAdmission : IResourcePressureAdmission
        {
            public int OomMarks;

            public ResourcePressureDecision Evaluate(int activeBuildPressure)
            {
                return new ResourcePressureDecision { Admit = true, Reason = "admitted" };
            }

            public void MarkOom() { OomMarks++; }

            public bool IsCapacitySuspended() { return false; }
        }

        private static async Task<Scene> CreateConfirmedStallSceneAsync(TestDatabase testDb, int processId)
        {
            Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-30), processId).ConfigureAwait(false);
            scene.Git.CommitTimes[scene.Vessel.LocalPath + "|" + Branch] = DateTime.UtcNow.AddHours(-2);
            scene.Mission.CaptainId = scene.Captain.Id;
            scene.Mission = await scene.Db.Missions.UpdateAsync(scene.Mission).ConfigureAwait(false);
            scene.Dock.CaptainId = scene.Captain.Id;
            scene.Dock.Active = true;
            scene.Dock = await scene.Db.Docks.UpdateAsync(scene.Dock).ConfigureAwait(false);
            return scene;
        }

        private async Task AssertRelaunchedMissionRunsAsync(Scene scene, int relaunchedPid, CountingAdmission admission, string context)
        {
            Mission mission = (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
            Captain captain = (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
            Dock dock = (await scene.Db.Docks.ReadAsync(scene.Dock.Id).ConfigureAwait(false))!;
            AssertEqual(MissionStatusEnum.InProgress, mission.Status, context + ": the relaunched mission keeps running");
            AssertEqual(relaunchedPid, mission.ProcessId ?? 0, context + ": the mission records the relaunched process");
            AssertEqual(CaptainStateEnum.Working, captain.State, context + ": the captain keeps working");
            AssertEqual(scene.Mission.Id, captain.CurrentMissionId ?? "", context + ": the captain keeps the mission");
            AssertEqual(relaunchedPid, captain.ProcessId ?? 0, context + ": the captain records the relaunched process");
            AssertTrue(dock.Active, context + ": the relaunched mission's dock is not reclaimed");
            AssertEqual(0, admission.OomMarks, context + ": the stopped process's kill is not marked as an out-of-memory kill");
            List<ArmadaEvent> events = await scene.Db.Events.EnumerateByMissionAsync(scene.Mission.Id, 200).ConfigureAwait(false);
            AssertEqual(0, events.Count(e => e.EventType == "mission.retry_requeued" || e.EventType == "mission.failed"),
                context + ": the mission is neither requeued nor failed");
        }

        private static async Task<int> CountNudgesAsync(Scene scene)
        {
            EnumerationResult<Signal> signals = await scene.Db.Signals.EnumerateAsync(new EnumerationQuery
            {
                PageNumber = 1,
                PageSize = 50,
                SignalType = SignalTypeEnum.Mail.ToString(),
                ToCaptainId = scene.Captain.Id
            }).ConfigureAwait(false);
            return signals.Objects.Count(s => (s.Payload ?? "").Contains("ARMADA_AUTO_NUDGE", StringComparison.Ordinal));
        }

        private static async Task<List<ArmadaEvent>> StallEventsAsync(Scene scene, string eventType)
        {
            List<ArmadaEvent> events = await scene.Db.Events.EnumerateByMissionAsync(scene.Mission.Id, 200).ConfigureAwait(false);
            return events.Where(e => e.EventType == eventType).ToList();
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("RecoveryNudge_QuietCaptainWithRecentDockWrites_IsNotNudged_AndTheClearingIsRecorded", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateSceneAsync(testDb, DockActivity.Recent, DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(scene);

                    await orchestrator.SweepAsync().ConfigureAwait(false);
                    await orchestrator.SweepAsync().ConfigureAwait(false);

                    AssertEqual(0, await CountNudgesAsync(scene).ConfigureAwait(false), "a captain writing its dock is working, however quiet its output");
                    List<ArmadaEvent> cleared = await StallEventsAsync(scene, ClearedEvent).ConfigureAwait(false);
                    AssertEqual(1, cleared.Count, "the clearing is recorded once while the same signal keeps clearing it");
                    AssertContains("dock_write", cleared[0].Message, "the event names the signal that cleared the stall");
                    AssertContains("heartbeat 20", cleared[0].Message, "the event carries the quiet output as evidence");
                    AssertEqual(0, (await StallEventsAsync(scene, ConfirmedEvent).ConfigureAwait(false)).Count, "no stall is confirmed");
                }
            }).ConfigureAwait(false);

            await RunTest("RecoveryNudge_NoOutputNoDockWritesNoCommits_IsNudged_AndTheEvidenceIsRecorded", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    scene.Git.CommitTimes[scene.Vessel.LocalPath + "|" + Branch] = DateTime.UtcNow.AddHours(-2);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(scene);

                    await orchestrator.SweepAsync().ConfigureAwait(false);

                    AssertEqual(1, await CountNudgesAsync(scene).ConfigureAwait(false), "a captain with no signal inside the window is nudged");
                    List<ArmadaEvent> confirmed = await StallEventsAsync(scene, ConfirmedEvent).ConfigureAwait(false);
                    AssertEqual(1, confirmed.Count, "the confirmed stall is recorded");
                    AssertContains("no signal", confirmed[0].Message, "the event says no signal was inside the window");
                    AssertContains("newest dock write 60", confirmed[0].Message, "the event names the dock evidence");
                    AssertContains("branch tip " + Branch + " committed 120", confirmed[0].Message, "the event names the branch evidence");
                }
            }).ConfigureAwait(false);

            await RunTest("RecoveryNudge_BranchTipCommittedInsideWindow_IsNotNudged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    scene.Git.CommitTimes[scene.Vessel.LocalPath + "|" + Branch] = DateTime.UtcNow.AddMinutes(-1);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(scene);

                    await orchestrator.SweepAsync().ConfigureAwait(false);

                    AssertEqual(0, await CountNudgesAsync(scene).ConfigureAwait(false), "a captain that just committed is working");
                    List<ArmadaEvent> cleared = await StallEventsAsync(scene, ClearedEvent).ConfigureAwait(false);
                    AssertEqual(1, cleared.Count, "the clearing is recorded");
                    AssertContains("branch_tip", cleared[0].Message, "the event names the branch tip as the clearing signal");
                }
            }).ConfigureAwait(false);

            await RunTest("RecoveryNudge_WritesOnlyUnderDotGit_AreNotWorkEvidence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateSceneAsync(testDb, DockActivity.OnlyGitRecent, DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(scene);

                    await orchestrator.SweepAsync().ConfigureAwait(false);

                    AssertEqual(1, await CountNudgesAsync(scene).ConfigureAwait(false), "git metadata writes do not clear a stall");
                    AssertEqual(1, (await StallEventsAsync(scene, ConfirmedEvent).ConfigureAwait(false)).Count, "the stall is confirmed");
                }
            }).ConfigureAwait(false);

            await RunTest("RecoveryNudge_RealRepositoryTipCommittedInsideWindow_ClearsTheStall", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    await UseRealRepositoryAsync(scene, DateTime.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(scene, new GitService(CreateLogging()));

                    await orchestrator.SweepAsync().ConfigureAwait(false);

                    AssertEqual(0, await CountNudgesAsync(scene).ConfigureAwait(false), "a real branch tip committed a minute ago means the captain is working");
                    List<ArmadaEvent> cleared = await StallEventsAsync(scene, ClearedEvent).ConfigureAwait(false);
                    AssertEqual(1, cleared.Count, "the clearing is recorded");
                    AssertContains("branch_tip", cleared[0].Message, "the real tip time is the clearing signal");
                    AssertEqual(0, (await StallEventsAsync(scene, ConfirmedEvent).ConfigureAwait(false)).Count, "no stall is confirmed");
                }
            }).ConfigureAwait(false);

            await RunTest("RecoveryNudge_RealRepositoryTipOlderThanWindow_ConfirmsTheStallWithTheTipTime", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    await UseRealRepositoryAsync(scene, DateTime.UtcNow.AddHours(-2)).ConfigureAwait(false);
                    AutonomousRecoveryOrchestrator orchestrator = CreateOrchestrator(scene, new GitService(CreateLogging()));

                    await orchestrator.SweepAsync().ConfigureAwait(false);

                    AssertEqual(1, await CountNudgesAsync(scene).ConfigureAwait(false), "an old real tip does not clear the stall");
                    List<ArmadaEvent> confirmed = await StallEventsAsync(scene, ConfirmedEvent).ConfigureAwait(false);
                    AssertEqual(1, confirmed.Count, "the confirmed stall is recorded");
                    AssertContains("branch tip " + Branch + " committed 120", confirmed[0].Message,
                        "the event carries the tip time read from the real repository");
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_QuietCaptainWithRecentDockWrites_IsNotKilledOrRestarted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110001;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateSceneAsync(testDb, DockActivity.Recent, DateTime.UtcNow.AddMinutes(-30), pid).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        AdmiralService admiral = CreateAdmiral(scene, captains);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Captain after = (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
                        Mission mission = (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertEqual(0, stops, "a working captain's process is not killed");
                        AssertEqual(0, after.RecoveryAttempts, "a working captain is not restarted");
                        AssertEqual(MissionStatusEnum.InProgress, mission.Status, "the mission keeps running");
                        List<ArmadaEvent> cleared = await StallEventsAsync(scene, ClearedEvent).ConfigureAwait(false);
                        AssertEqual(1, cleared.Count, "the clearing is recorded");
                        AssertContains("dock_write", cleared[0].Message, "the event names the signal that cleared the stall");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_NoOutputNoDockWritesNoCommits_IsRestarted_AndTheEvidenceIsRecorded", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110002;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-30), pid).ConfigureAwait(false);
                        scene.Git.CommitTimes[scene.Vessel.LocalPath + "|" + Branch] = DateTime.UtcNow.AddHours(-2);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        AdmiralService admiral = CreateAdmiral(scene, captains);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Captain after = (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
                        AssertEqual(1, stops, "a stalled captain's process is stopped");
                        AssertEqual(1, after.RecoveryAttempts, "a stalled captain gets a recovery attempt");
                        List<ArmadaEvent> confirmed = await StallEventsAsync(scene, ConfirmedEvent).ConfigureAwait(false);
                        AssertEqual(1, confirmed.Count, "the confirmed stall is recorded");
                        AssertContains("newest dock write 60", confirmed[0].Message, "the event names the dock evidence");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_StoppedProcessExitAfterRelaunch_LeavesTheRelaunchedMissionRunning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110003;
                    const int relaunchedPid = 88110004;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        captains.OnStopAgent = _ => Task.CompletedTask;
                        captains.OnLaunchAgent = (_, _, _) => Task.FromResult(relaunchedPid);
                        CountingAdmission admission = new CountingAdmission();
                        AdmiralService admiral = CreateAdmiral(scene, captains, admission: admission);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);
                        await AssertRelaunchedMissionRunsAsync(scene, relaunchedPid, admission, "after the relaunch").ConfigureAwait(false);

                        // The stopped process's kill reaches the exit handler after the relaunch registered its process.
                        await admiral.HandleProcessExitAsync(pid, 137, scene.Captain.Id, scene.Mission.Id).ConfigureAwait(false);
                        await AssertRelaunchedMissionRunsAsync(scene, relaunchedPid, admission, "after the stopped process's exit").ConfigureAwait(false);

                        // Any process that is not the captain's recorded process no longer speaks for the mission.
                        await admiral.HandleProcessExitAsync(88110005, 1, scene.Captain.Id, scene.Mission.Id).ConfigureAwait(false);
                        await AssertRelaunchedMissionRunsAsync(scene, relaunchedPid, admission, "after a stale process's exit").ConfigureAwait(false);
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_StoppedProcessExitBeforeRelaunch_IsNotTheMissionsFailure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110006;
                    const int relaunchedPid = 88110007;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        CountingAdmission admission = new CountingAdmission();
                        AdmiralService admiral = CreateAdmiral(scene, captains, admission: admission);

                        // The stop kills the process and its exit is handled before the relaunch starts.
                        captains.OnStopAgent = async c =>
                        {
                            if (c.ProcessId == pid)
                                await admiral.HandleProcessExitAsync(pid, 137, scene.Captain.Id, scene.Mission.Id).ConfigureAwait(false);
                        };
                        captains.OnLaunchAgent = (_, _, _) => Task.FromResult(relaunchedPid);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);
                        await AssertRelaunchedMissionRunsAsync(scene, relaunchedPid, admission, "after an exit that arrived before the relaunch").ConfigureAwait(false);
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_UnderDispatchHold_StopsWithoutRelaunch_NamesTheHoldOnce_AndDispatchesAfterClear", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110008;
                    const int relaunchedPid = 88110009;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        List<int?> stopped = new List<int?>();
                        int launches = 0;
                        captains.OnStopAgent = c => { stopped.Add(c.ProcessId); return Task.CompletedTask; };
                        captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(relaunchedPid); };
                        DispatchHold hold = new DispatchHold();
                        hold.Engage("example rebuild", "example-session");
                        AdmiralService admiral = CreateAdmiral(scene, captains, hold);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);
                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Mission held = (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        Captain released = (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
                        AssertEqual(1, stopped.Count, "the stalled process is stopped once");
                        AssertEqual(pid, stopped[0] ?? 0, "the stop names the stalled process");
                        AssertEqual(0, launches, "nothing is launched while the hold is engaged");
                        AssertEqual(MissionStatusEnum.Pending, held.Status, "the mission waits as Pending for the hold to clear");
                        AssertNull(held.ProcessId, "the held mission records no process");
                        AssertEqual(Branch, held.BranchName ?? "", "the held mission keeps its branch");
                        AssertEqual(CaptainStateEnum.Idle, released.State, "the captain is released");
                        AssertNull(released.CurrentMissionId, "the released captain names no mission");
                        List<ArmadaEvent> deferrals = await StallEventsAsync(scene, AdmiralService.RecoveryDeferredByDispatchHoldEvent).ConfigureAwait(false);
                        AssertEqual(1, deferrals.Count, "the deferral is recorded once for the engagement");
                        AssertContains("example-session", deferrals[0].Message, "the event names the holder");
                        AssertContains("example rebuild", deferrals[0].Message, "the event names the hold reason");

                        // The stopped process's kill arrives late; the admiral already decided the mission's state.
                        await admiral.HandleProcessExitAsync(pid, 137, scene.Captain.Id, scene.Mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Pending, (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!.Status,
                            "the stopped process's exit does not change the held mission");

                        hold.Clear();
                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Mission resumed = (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertEqual(1, launches, "the first dispatch pass after the hold clears launches the mission");
                        AssertTrue(resumed.Status == MissionStatusEnum.InProgress || resumed.Status == MissionStatusEnum.Assigned,
                            "the mission is assigned again after the hold clears, but it is " + resumed.Status);
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
