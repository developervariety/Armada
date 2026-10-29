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

        private static AdmiralService CreateAdmiral(Scene scene, CaptainService captains, DispatchHold? hold = null, IResourcePressureAdmission? admission = null,
            ProviderProgressTracker? providerProgress = null, TerminalMarkerTracker? terminalMarkers = null, ICaptainProcessTreeReader? processTree = null)
        {
            LoggingModule logging = CreateLogging();
            IDockService docks = new DockService(logging, scene.Db, scene.Settings, scene.Git);
            MissionService missions = new MissionService(logging, scene.Db, scene.Settings, docks, captains, git: scene.Git);
            AdmiralService admiral = new AdmiralService(logging, scene.Db, scene.Settings, captains, missions, new VoyageService(logging, scene.Db), docks,
                resourcePressureAdmission: admission, git: scene.Git, dispatchHold: hold, processTree: processTree);
            if (providerProgress != null) admiral.SetProviderProgress(providerProgress);
            if (terminalMarkers != null) admiral.SetTerminalMarkers(terminalMarkers);
            return admiral;
        }

        private static async Task<ProviderProgressTracker> CreateStaleProviderProgressAsync(Scene scene, DateTime? progressUtc = null)
        {
            scene.Captain.Runtime = AgentRuntimeEnum.OpenCode;
            scene.Captain.LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-1);
            scene.Captain = await scene.Db.Captains.UpdateAsync(scene.Captain).ConfigureAwait(false);
            scene.Git.CommitTimes[scene.Vessel.LocalPath + "|" + Branch] = DateTime.UtcNow.AddHours(-2);
            ProviderProgressTracker tracker = new ProviderProgressTracker();
            tracker.Record(scene.Captain.Id, progressUtc ?? DateTime.UtcNow.AddMinutes(-30));
            return tracker;
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

        // Answers every read with the same process tree and counts the reads.
        private sealed class FixedProcessTree : ICaptainProcessTreeReader
        {
            private readonly CaptainProcessTreeRead _Read;

            public int Reads;

            public FixedProcessTree(CaptainProcessTreeRead read)
            {
                _Read = read;
            }

            public CaptainProcessTreeRead ReadDescendants(int rootProcessId)
            {
                Reads++;
                return _Read;
            }
        }

        // An agent process started 40 minutes ago with one launch-time companion and the given later children.
        private static FixedProcessTree TreeWithChildren(params CaptainChildProcess[] children)
        {
            DateTime rootStartedUtc = DateTime.UtcNow.AddMinutes(-40);
            List<CaptainChildProcess> all = new List<CaptainChildProcess>
            {
                new CaptainChildProcess(5001, 5000, "codex", rootStartedUtc.AddSeconds(2))
            };
            all.AddRange(children);
            return new FixedProcessTree(CaptainProcessTreeRead.Read(all, rootStartedUtc));
        }

        private static CaptainStallEvaluator CreateEvaluator(Scene scene, ICaptainProcessTreeReader tree)
        {
            return new CaptainStallEvaluator(scene.Db, scene.Git, CreateLogging(), processTree: tree);
        }

        // Writes a fake /proc/<pid>/stat whose start time is the given clock ticks after boot.
        private static void WriteProcStat(string procRoot, int pid, string name, char state, int parentPid, long startTicks)
        {
            string directory = Path.Combine(procRoot, pid.ToString());
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "stat"),
                pid + " (" + name + ") " + state + " " + parentPid + " " + String.Join(" ", Enumerable.Repeat("0", 17)) + " " + startTicks + " 0 0 0\n");
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

            await RunTest("OpenCodeProviderStall_RecentDockWritesPreventRecovery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110001;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateSceneAsync(testDb, DockActivity.Recent, DateTime.UtcNow.AddMinutes(-1), pid).ConfigureAwait(false);
                        scene.Captain.Runtime = AgentRuntimeEnum.OpenCode;
                        scene.Captain = await scene.Db.Captains.UpdateAsync(scene.Captain).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        ProviderProgressTracker providerProgress = new ProviderProgressTracker();
                        providerProgress.Record(scene.Captain.Id, DateTime.UtcNow.AddMinutes(-30));
                        AdmiralService admiral = CreateAdmiral(scene, captains, providerProgress: providerProgress);

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

            await RunTest("OpenCodeProviderStall_FreshHeartbeatAndStaleProvider_StopsAndRequeuesThroughRecovery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110010;
                    const int relaunchedPid = 88110011;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        ProviderProgressTracker providerProgress = await CreateStaleProviderProgressAsync(scene).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        captains.OnLaunchAgent = (_, _, _) =>
                        {
                            ProcessSupervisor.RegisterSyntheticProcess(relaunchedPid);
                            return Task.FromResult(relaunchedPid);
                        };
                        CountingAdmission admission = new CountingAdmission();
                        AdmiralService admiral = CreateAdmiral(scene, captains, admission: admission, providerProgress: providerProgress);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertEqual(1, stops, "the actual health-recovery path stops a provider-silent process despite its fresh heartbeat");
                        AssertEqual(1, (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!.RecoveryAttempts,
                            "the existing bounded recovery policy owns the retry");
                        await AssertRelaunchedMissionRunsAsync(scene, relaunchedPid, admission, "after provider-silent recovery").ConfigureAwait(false);
                        AssertFalse(providerProgress.TryGet(scene.Captain.Id, out _), "the old process provider time is cleared before the relaunch");
                        await admiral.HealthCheckAsync().ConfigureAwait(false);
                        AssertEqual(1, stops, "a relaunch with fresh output does not consume another attempt before it reports provider progress");
                        List<ArmadaEvent> confirmed = await StallEventsAsync(scene, ConfirmedEvent).ConfigureAwait(false);
                        AssertEqual(1, confirmed.Count, "the stop records the confirmed stall");
                        AssertContains("provider progress 30", confirmed[0].Message, "the evidence records the stale provider timestamp");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                        ProcessSupervisor.UnregisterSyntheticProcess(relaunchedPid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("OpenCodeProviderStall_StartupGracePreventsRecovery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110012;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-1), pid).ConfigureAwait(false);
                        ProviderProgressTracker providerProgress = await CreateStaleProviderProgressAsync(scene).ConfigureAwait(false);
                        scene.Mission.StartedUtc = DateTime.UtcNow.AddSeconds(-30);
                        scene.Mission = await scene.Db.Missions.UpdateAsync(scene.Mission).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        AdmiralService admiral = CreateAdmiral(scene, captains, providerProgress: providerProgress);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertEqual(0, stops, "a stale prior provider timestamp does not stop a captain inside mission startup grace");
                        AssertEqual(0, (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!.RecoveryAttempts,
                            "startup grace does not spend a recovery attempt");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("OpenCodeProviderStall_TerminalMarkerPreventsRecovery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110013;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        ProviderProgressTracker providerProgress = await CreateStaleProviderProgressAsync(scene).ConfigureAwait(false);
                        TerminalMarkerTracker terminalMarkers = new TerminalMarkerTracker();
                        terminalMarkers.TryRecordFirst(scene.Mission.Id,
                            new ProgressParser.ProgressSignal { Type = "result", Value = "COMPLETE" }, DateTime.UtcNow);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        AdmiralService admiral = CreateAdmiral(scene, captains, providerProgress: providerProgress, terminalMarkers: terminalMarkers);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertEqual(0, stops, "a terminal marker keeps a completed stage out of provider-stall recovery");
                        AssertEqual(0, (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!.RecoveryAttempts,
                            "terminal output does not spend a recovery attempt");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("OpenCodeProviderStall_RecentProviderProgressPreventsRecovery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110014;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-1), pid).ConfigureAwait(false);
                        ProviderProgressTracker providerProgress = await CreateStaleProviderProgressAsync(scene, DateTime.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        AdmiralService admiral = CreateAdmiral(scene, captains, providerProgress: providerProgress);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertEqual(0, stops, "recent provider progress keeps a healthy long OpenCode run alive");
                        AssertEqual(0, (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!.RecoveryAttempts,
                            "recent provider progress does not spend a recovery attempt");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("OpenCodeProviderStall_RecentBranchCommitPreventsRecovery", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110015;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateSceneAsync(testDb, DockActivity.Old, DateTime.UtcNow.AddMinutes(-1), pid).ConfigureAwait(false);
                        ProviderProgressTracker providerProgress = await CreateStaleProviderProgressAsync(scene).ConfigureAwait(false);
                        scene.Git.CommitTimes[scene.Vessel.LocalPath + "|" + Branch] = DateTime.UtcNow.AddMinutes(-1);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        AdmiralService admiral = CreateAdmiral(scene, captains, providerProgress: providerProgress);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertEqual(0, stops, "a recent branch commit remains valid work evidence during provider silence");
                        AssertEqual(0, (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!.RecoveryAttempts,
                            "recent branch work does not spend a recovery attempt");
                        List<ArmadaEvent> cleared = await StallEventsAsync(scene, ClearedEvent).ConfigureAwait(false);
                        AssertEqual(1, cleared.Count, "the provider stall is recorded as cleared");
                        AssertContains("branch_tip", cleared[0].Message, "the branch tip is named as the clearing evidence");
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
                        AssertContains("child processes: process tree unreadable", confirmed[0].Message,
                            "a process the host cannot read falls back to the other signals and says so");
                        AssertEqual(0, scene.Git.SnapshotCalls.Count, "an early relaunch keeps its dock and saves no snapshot");
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

            await RunTest("AdmiralHeartbeatStall_UnderDispatchHold_RelaunchesTheInFlightMissionInItsDock", async () =>
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
                        CountingAdmission admission = new CountingAdmission();
                        DispatchHold hold = new DispatchHold();
                        hold.Engage("example rebuild", "example-session");
                        AdmiralService admiral = CreateAdmiral(scene, captains, hold, admission);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Captain recovered = (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
                        AssertEqual(1, stopped.Count, "the stalled process is stopped once");
                        AssertEqual(pid, stopped[0] ?? 0, "the stop names the stalled process");
                        AssertEqual(1, launches, "the stalled mission is relaunched while the hold is engaged");
                        AssertEqual(1, recovered.RecoveryAttempts, "the relaunch spends one recovery attempt");
                        AssertNotNull(hold.Snapshot(), "the hold stays engaged");
                        await AssertRelaunchedMissionRunsAsync(scene, relaunchedPid, admission, "under the hold").ConfigureAwait(false);

                        // The stopped process's kill arrives late; the relaunched process still owns the mission.
                        await admiral.HandleProcessExitAsync(pid, 137, scene.Captain.Id, scene.Mission.Id).ConfigureAwait(false);
                        await AssertRelaunchedMissionRunsAsync(scene, relaunchedPid, admission, "after the stopped process's exit under the hold").ConfigureAwait(false);
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("SuiteLease_WaitingOrHolding_HoldsTheStallWindowOpen", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateConfirmedStallSceneAsync(testDb, 88110024).ConfigureAwait(false);
                    Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                    int previousFloor = HostWideCommandLock.MemoryFloorMb;
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        await CaptainSuiteLeaseRegistry.AcquireAsync(scene.Mission.Id, scene.Vessel.Id, CancellationToken.None).ConfigureAwait(false);

                        CaptainStallDecision decision = await CreateEvaluator(scene, TreeWithChildren()).EvaluateAsync(
                            scene.Captain, scene.Mission, 10, DateTime.UtcNow, null, 45).ConfigureAwait(false);

                        AssertFalse(decision.IsStalled, "a captain waiting on or holding the suite lease is not stalled: " + decision.DescribeEvidence());
                        AssertEqual(CaptainStallEvaluator.SignalSuiteLease, decision.DecidingSignal, "the suite lease decides");
                    }
                    finally
                    {
                        CaptainSuiteLeaseRegistry.Release(scene.Mission.Id);
                        HostWideCommandLock.SetMemoryFloorMb(previousFloor);
                        HostWideCommandLock.SetMemoryReader(previousReader);
                        HostWideCommandLock.SetSlotCount(1);
                        HostWideCommandLock.SetVesselSlotCount(1);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ChildProcess_TestRunStartedAfterTheLastOutput_HoldsTheStallWindowOpen", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateConfirmedStallSceneAsync(testDb, 88110020).ConfigureAwait(false);
                    FixedProcessTree tree = TreeWithChildren(new CaptainChildProcess(5002, 5001, "dotnet", DateTime.UtcNow.AddMinutes(-29)));

                    CaptainStallDecision decision = await CreateEvaluator(scene, tree).EvaluateAsync(
                        scene.Captain, scene.Mission, 10, DateTime.UtcNow, null, 45).ConfigureAwait(false);

                    AssertFalse(decision.IsStalled, "a captain waiting on its own test run is not stalled: " + decision.DescribeEvidence());
                    AssertEqual(CaptainStallEvaluator.SignalChildProcess, decision.DecidingSignal, "the running child decides");
                    AssertEqual(1, decision.ChildProcessCount, "only the child started after the last output counts, not the launch-time agent binary");
                    AssertContains("dotnet", decision.DescribeEvidence(), "the evidence names the running child");
                }
            }).ConfigureAwait(false);

            await RunTest("ChildProcess_CeilingReached_ConfirmsTheStall", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateConfirmedStallSceneAsync(testDb, 88110021).ConfigureAwait(false);
                    FixedProcessTree tree = TreeWithChildren(new CaptainChildProcess(5002, 5001, "dotnet", DateTime.UtcNow.AddMinutes(-29)));

                    // The last output is 30 minutes old, so a 20-minute ceiling is already spent.
                    CaptainStallDecision decision = await CreateEvaluator(scene, tree).EvaluateAsync(
                        scene.Captain, scene.Mission, 10, DateTime.UtcNow, null, 20).ConfigureAwait(false);

                    AssertTrue(decision.IsStalled, "a child running past the ceiling no longer holds the window");
                    AssertTrue(decision.ChildProcessCeilingReached, "the decision says the ceiling ended the hold");
                    AssertContains("20-minute child-process ceiling is reached", decision.DescribeEvidence(), "the evidence names the ceiling");
                }
            }).ConfigureAwait(false);

            await RunTest("ChildProcess_OnlyLaunchCompanionsAndOlderChildren_DoNotHoldTheWindow", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateConfirmedStallSceneAsync(testDb, 88110022).ConfigureAwait(false);

                    // A child started well before the last output (30 minutes ago) is not work the agent waits on.
                    FixedProcessTree older = TreeWithChildren(new CaptainChildProcess(5004, 5001, "bash", DateTime.UtcNow.AddMinutes(-35)));
                    CaptainStallDecision decision = await CreateEvaluator(scene, older).EvaluateAsync(
                        scene.Captain, scene.Mission, 10, DateTime.UtcNow, null, 45).ConfigureAwait(false);
                    AssertTrue(decision.IsStalled, "an idle agent CLI with a standing older child is still stalled");
                    AssertEqual(0, decision.ChildProcessCount, "the older child does not count");
                    AssertContains("no descendant started after the last output", decision.DescribeEvidence(), "the evidence says why no child counted");

                    // An agent relaunched just before its last output: the runtime binary and the tool server it starts
                    // at launch are inside the start slack, and still never count.
                    DateTime rootStartedUtc = DateTime.UtcNow.AddMinutes(-31);
                    FixedProcessTree launched = new FixedProcessTree(CaptainProcessTreeRead.Read(new List<CaptainChildProcess>
                    {
                        new CaptainChildProcess(5001, 5000, "codex", rootStartedUtc.AddSeconds(5)),
                        new CaptainChildProcess(5003, 5001, "mcp-server", rootStartedUtc.AddSeconds(30))
                    }, rootStartedUtc));
                    CaptainStallDecision companions = await CreateEvaluator(scene, launched).EvaluateAsync(
                        scene.Captain, scene.Mission, 10, DateTime.UtcNow, null, 45).ConfigureAwait(false);
                    AssertTrue(companions.IsStalled, "launch-time companions do not hold the window");
                    AssertEqual(0, companions.ChildProcessCount, "no launch-time companion counts");
                }
            }).ConfigureAwait(false);

            await RunTest("ChildProcess_UnreadableTreeOrSignalOff_FallsBackToTheOtherSignals", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Scene scene = await CreateConfirmedStallSceneAsync(testDb, 88110023).ConfigureAwait(false);
                    FixedProcessTree unreadable = new FixedProcessTree(CaptainProcessTreeRead.Unavailable(
                        CaptainProcessTreeRead.ProcessTableUnavailableReason, "no process table at /example"));

                    CaptainStallDecision decision = await CreateEvaluator(scene, unreadable).EvaluateAsync(
                        scene.Captain, scene.Mission, 10, DateTime.UtcNow, null, 45).ConfigureAwait(false);
                    AssertTrue(decision.IsStalled, "an unreadable process tree decides as before");
                    AssertContains("process tree unreadable (process_table_unavailable)", decision.DescribeEvidence(), "the evidence names why");

                    FixedProcessTree live = TreeWithChildren(new CaptainChildProcess(5002, 5001, "dotnet", DateTime.UtcNow.AddMinutes(-29)));
                    CaptainStallDecision off = await CreateEvaluator(scene, live).EvaluateAsync(
                        scene.Captain, scene.Mission, 10, DateTime.UtcNow, null, 0).ConfigureAwait(false);
                    AssertTrue(off.IsStalled, "a ceiling of 0 turns the child-process signal off");
                    AssertEqual(0, live.Reads, "the process tree is not read while the signal is off");
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_LiveTestRunChild_IsNotStopped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110024;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        int stops = 0;
                        captains.OnStopAgent = _ => { stops++; return Task.CompletedTask; };
                        FixedProcessTree tree = TreeWithChildren(new CaptainChildProcess(5002, 5001, "dotnet", DateTime.UtcNow.AddMinutes(-29)));
                        AdmiralService admiral = CreateAdmiral(scene, captains, processTree: tree);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Captain after = (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
                        AssertEqual(0, stops, "a captain whose test run is still running is not stopped");
                        AssertEqual(0, after.RecoveryAttempts, "no recovery attempt is spent");
                        List<ArmadaEvent> cleared = await StallEventsAsync(scene, ClearedEvent).ConfigureAwait(false);
                        AssertEqual(1, cleared.Count, "the clearing is recorded");
                        AssertContains("child_process", cleared[0].Message, "the event names the child-process signal");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_FinalRecoveryAttempt_SavesTheDockWorkFirst", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110025;
                    const int relaunchedPid = 88110026;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        scene.Settings.MaxRecoveryAttempts = 1;
                        string refName = "refs/heads/recover/" + scene.Mission.Id + "-1";
                        scene.Git.SnapshotResult = DockWorkSnapshot.Saved(refName, new string('d', 40));
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        captains.OnStopAgent = _ => Task.CompletedTask;
                        captains.OnLaunchAgent = (_, _, _) => Task.FromResult(relaunchedPid);
                        AdmiralService admiral = CreateAdmiral(scene, captains);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        AssertEqual(1, scene.Git.SnapshotCalls.Count, "the final relaunch saves the dock's work");
                        AssertEqual(scene.Worktree + "|" + scene.Mission.Id, scene.Git.SnapshotCalls[0], "the snapshot reads the mission's dock");
                        List<ArmadaEvent> saved = (await scene.Db.Events.EnumerateByMissionAsync(scene.Mission.Id, 200).ConfigureAwait(false))
                            .Where(e => e.EventType == "mission.work_snapshot_saved").ToList();
                        AssertEqual(1, saved.Count, "the snapshot is recorded on the mission");
                        AssertContains(refName, saved[0].Message, "the event names the recover branch");
                        Captain after = (await scene.Db.Captains.ReadAsync(scene.Captain.Id).ConfigureAwait(false))!;
                        AssertEqual(1, after.RecoveryAttempts, "the final relaunch still runs");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_RecoveryExhausted_SavesTheDockWorkBeforeReclaim_AndNamesTheBranch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110027;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        scene.Settings.MaxRecoveryAttempts = 0;
                        string refName = "refs/heads/recover/" + scene.Mission.Id + "-1";
                        scene.Git.SnapshotResult = DockWorkSnapshot.Saved(refName, new string('d', 40));
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        captains.OnStopAgent = _ => Task.CompletedTask;
                        AdmiralService admiral = CreateAdmiral(scene, captains);

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Mission failed = (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Failed, failed.Status, "an exhausted stall fails the mission");
                        AssertContains("recovery exhausted", failed.FailureReason ?? "", "the failure keeps its reason");
                        AssertContains(refName, failed.FailureReason ?? "", "the failure names the branch that holds the work");
                        int snapshotAt = scene.Git.OperationCalls.IndexOf("snapshot:" + scene.Mission.Id);
                        AssertTrue(snapshotAt >= 0, "the dock's work is saved");
                        for (int i = 0; i < scene.Git.OperationCalls.Count; i++)
                        {
                            if (scene.Git.OperationCalls[i].StartsWith("remove-worktree:", StringComparison.Ordinal))
                                AssertTrue(i > snapshotAt, "the dock is removed only after its work is saved");
                        }
                        List<ArmadaEvent> saved = (await scene.Db.Events.EnumerateByMissionAsync(scene.Mission.Id, 200).ConfigureAwait(false))
                            .Where(e => e.EventType == "mission.work_snapshot_saved").ToList();
                        AssertEqual(1, saved.Count, "the snapshot is recorded on the mission");
                        AssertContains(refName, saved[0].Message, "the event names the recover branch");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("AdmiralHeartbeatStall_RecoveryExhausted_KeepsTheDockWhenItsWorkCannotBeSaved", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    const int pid = 88110028;
                    ProcessSupervisor.RegisterSyntheticProcess(pid);
                    try
                    {
                        Scene scene = await CreateConfirmedStallSceneAsync(testDb, pid).ConfigureAwait(false);
                        scene.Settings.MaxRecoveryAttempts = 0;
                        scene.Git.SnapshotResult = DockWorkSnapshot.Failed("could not stage the working tree: disk full");
                        LoggingModule logging = CreateLogging();
                        CaptainService captains = new CaptainService(logging, scene.Db, scene.Settings, scene.Git, new DockService(logging, scene.Db, scene.Settings, scene.Git));
                        captains.OnStopAgent = _ => Task.CompletedTask;
                        AdmiralService admiral = CreateAdmiral(scene, captains);
                        string? missionDockId = (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!.DockId;

                        await admiral.HealthCheckAsync().ConfigureAwait(false);

                        Mission failed = (await scene.Db.Missions.ReadAsync(scene.Mission.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Failed, failed.Status, "an exhausted stall still fails the mission");
                        AssertContains("NOT saved", failed.FailureReason ?? "", "the failure says the work was not saved");
                        AssertContains("disk full", failed.FailureReason ?? "", "the failure names why");
                        AssertFalse(scene.Git.OperationCalls.Any(c => c.StartsWith("remove-worktree:", StringComparison.Ordinal)),
                            "a dock whose work was not saved is not removed");
                        AssertEqual(missionDockId, failed.DockId, "the mission keeps the link to its kept dock");
                        Dock kept = (await scene.Db.Docks.ReadAsync(scene.Dock.Id).ConfigureAwait(false))!;
                        AssertTrue(kept.Active, "the kept dock stays active");
                        List<ArmadaEvent> unsaved = (await scene.Db.Events.EnumerateByMissionAsync(scene.Mission.Id, 200).ConfigureAwait(false))
                            .Where(e => e.EventType == "mission.work_snapshot_failed").ToList();
                        AssertTrue(unsaved.Count >= 1, "the unsaved work raises a named event");
                    }
                    finally
                    {
                        ProcessSupervisor.UnregisterSyntheticProcess(pid);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ProcTreeReader_WalksDescendants_SkipsZombies_AndNamesAMissingRoot", () =>
            {
                string procRoot = Path.Combine(Path.GetTempPath(), "armada_stall_proc_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(procRoot);
                    File.WriteAllText(Path.Combine(procRoot, "stat"), "cpu  1 2 3 4\nbtime 1700000000\nprocesses 10\n");
                    WriteProcStat(procRoot, 100, "node", 'S', 1, 1000);
                    WriteProcStat(procRoot, 101, "codex", 'S', 100, 1100);
                    WriteProcStat(procRoot, 102, "odd) (name", 'R', 101, 50000);
                    WriteProcStat(procRoot, 103, "dotnet", 'Z', 102, 50100);
                    WriteProcStat(procRoot, 104, "testhost", 'S', 103, 50200);
                    WriteProcStat(procRoot, 200, "unrelated", 'S', 1, 60000);

                    ProcCaptainProcessTreeReader reader = new ProcCaptainProcessTreeReader(procRoot);
                    CaptainProcessTreeRead read = reader.ReadDescendants(100);

                    AssertTrue(read.Available, "a readable table is read");
                    List<int> ids = read.Descendants.Select(d => d.ProcessId).OrderBy(id => id).ToList();
                    AssertEqual("101,102,104", String.Join(",", ids), "descendants are walked through every level, zombies excluded");
                    AssertEqual("odd) (name", read.Descendants.First(d => d.ProcessId == 102).Name, "a name holding parentheses is read whole");
                    DateTime boot = DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime;
                    AssertEqual(boot.AddSeconds(500), read.Descendants.First(d => d.ProcessId == 102).StartedUtc, "start time is boot time plus ticks over USER_HZ");
                    AssertEqual(boot.AddSeconds(10), read.RootStartedUtc ?? DateTime.MinValue, "the agent process start time is reported");

                    CaptainProcessTreeRead missing = reader.ReadDescendants(999);
                    AssertFalse(missing.Available, "a missing agent process is not an empty tree");
                    AssertEqual(CaptainProcessTreeRead.RootNotFoundReason, missing.UnavailableReason ?? "", "the reason is named");

                    CaptainProcessTreeRead noTable = new ProcCaptainProcessTreeReader(Path.Combine(procRoot, "absent")).ReadDescendants(100);
                    AssertEqual(CaptainProcessTreeRead.ProcessTableUnavailableReason, noTable.UnavailableReason ?? "", "a host without a process table says so");
                }
                finally
                {
                    try { Directory.Delete(procRoot, true); } catch { }
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
