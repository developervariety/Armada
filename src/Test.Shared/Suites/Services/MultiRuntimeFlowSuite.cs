namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// The flow where frontier captains plan and review and a local-model captain does the work: missions without a
    /// persona are routed as Worker, a captain that cannot start backs off with a visible reason, a gated Judge block
    /// goes to a person instead of failing the voyage, a restarted later stage keeps the earlier stages' branch, and
    /// work a captain left uncommitted is committed instead of lost.
    /// </summary>
    public sealed class MultiRuntimeFlowSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.MultiRuntimeFlow";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("persona_less_mission_routes_as_worker", "A mission dispatched without a persona follows the Worker persona's captain priorities", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Captains.OnLaunchAgent = (_, _, _) => Task.FromResult(4242);

                Persona worker = new Persona(Armada.Core.PersonaCatalog.Worker, "persona.worker");
                worker.CaptainPriorities = new List<PersonaCaptainPriority> { new PersonaCaptainPriority { Runtime = AgentRuntimeEnum.Mux } };
                await testDb.Driver.Personas.CreateAsync(worker).ConfigureAwait(false);

                // By name the Claude captain sorts first, which is what legacy routing picked.
                Captain claude = await CreateCaptainAsync(testDb, "A Claude", AgentRuntimeEnum.ClaudeCode).ConfigureAwait(false);
                Captain mux = await CreateCaptainAsync(testDb, "Z Mux", AgentRuntimeEnum.Mux).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);

                Mission mission = new Mission("No persona", "Plain dispatch");
                mission.VesselId = vessel.Id;
                mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                AssertTrue(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "assigned");
                Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(mux.Id, stored!.CaptainId, "the Worker priority picked the Mux captain, not " + claude.Name);
            }));

            cases.Add(CaseAsync("launch_failure_backs_off_with_reason", "A captain whose CLI is missing is not relaunched every cycle, and the mission says why it is waiting", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                int launches = 0;
                h.Captains.OnLaunchAgent = (_, _, _) =>
                {
                    launches++;
                    throw new AgentRuntimeNotInstalledException(AgentRuntimeEnum.Mux, "mux", null);
                };

                await CreateCaptainAsync(testDb, "Mux", AgentRuntimeEnum.Mux).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission mission = new Mission("Needs mux", "Work");
                mission.VesselId = vessel.Id;
                mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "first launch fails");
                Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, stored!.Status, "back to Pending");

                MissionAssignmentBlocker? blocker = await h.Missions.GetAssignmentBlockerAsync(stored).ConfigureAwait(false);
                AssertNotNull(blocker, "a blocker explains the wait");
                AssertEqual(MissionAssignmentBlockerReasonEnum.LaunchFailing, blocker.Reason);
                AssertContains("not installed", blocker.Summary);
                AssertNotNull(blocker.UntilUtc, "the next attempt time is shown");

                AssertFalse(await h.Missions.TryAssignAsync(stored, vessel).ConfigureAwait(false), "still backing off");
                AssertEqual(1, launches, "no second launch during the backoff");
            }));

            cases.Add(CaseAsync("gated_judge_block_goes_to_review", "With a review gate, a Judge block waits for a person, and approving lands the work", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                int landingCalls = 0;
                h.Missions.OnMissionComplete = (m, d) => { landingCalls++; return Task.CompletedTask; };
                h.Missions.OnGetMissionOutput = _ => "[ARMADA:VERDICT] NEEDS_REVISION\nScenario: the change misses a case.";

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain judgeCaptain = await CreateCaptainAsync(testDb, "Codex", AgentRuntimeEnum.Codex, CaptainStateEnum.Working).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("judged")).ConfigureAwait(false);
                Mission judge = await CreateRunningMissionAsync(testDb, h, vessel, voyage, judgeCaptain, "Judge", true).ConfigureAwait(false);

                await h.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);
                Mission? inReview = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Review, inReview!.Status, "a gated Judge block waits for a person");
                AssertContains("Judge blocked landing", inReview.ReviewComment ?? "");
                AssertEqual(0, landingCalls, "nothing landed yet");

                Mission approved = await h.Missions.ApproveReviewAsync(judge.Id, "usr_reviewer", "Overriding: the case is out of scope.").ConfigureAwait(false);
                AssertEqual(1, landingCalls, "approving the override lands the work");
                AssertNotEqual(MissionStatusEnum.Failed, approved.Status);
            }));

            cases.Add(CaseAsync("ungated_judge_block_fails", "Without a review gate, a Judge block still fails the mission automatically", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                int landingCalls = 0;
                h.Missions.OnMissionComplete = (m, d) => { landingCalls++; return Task.CompletedTask; };
                h.Missions.OnGetMissionOutput = _ => "[ARMADA:VERDICT] FAIL\nScenario: the build breaks.";

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain judgeCaptain = await CreateCaptainAsync(testDb, "Codex", AgentRuntimeEnum.Codex, CaptainStateEnum.Working).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("judged")).ConfigureAwait(false);
                Mission judge = await CreateRunningMissionAsync(testDb, h, vessel, voyage, judgeCaptain, "Judge", false).ConfigureAwait(false);

                await h.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);
                Mission? failed = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, failed!.Status);
                AssertEqual(MissionFailureKindEnum.JudgeRejected, failed.FailureKind);
                AssertEqual(0, landingCalls);
            }));

            cases.Add(CaseAsync("restart_later_stage_keeps_branch", "Restarting a failed later stage keeps the earlier stage's branch, reopens the voyage, and restores the cancelled stages after it", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = new Voyage("pipeline");
                voyage.Status = VoyageStatusEnum.Failed;
                voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                Mission worker = new Mission("[Worker] Build", "Build it") { VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Worker", Status = MissionStatusEnum.Complete, BranchName = "armada/mux/worker" };
                worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);
                Mission judge = new Mission("[Judge] Review", "Review it") { VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Judge", Status = MissionStatusEnum.Failed, DependsOnMissionId = worker.Id, FailureReason = "crashed", FailureKind = MissionFailureKindEnum.Crash };
                judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);
                Mission recorder = new Mission("[Recorder] Record", "Record it") { VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Recorder", Status = MissionStatusEnum.Cancelled, DependsOnMissionId = judge.Id, FailureKind = MissionFailureKindEnum.DependencyFailed };
                recorder = await testDb.Driver.Missions.CreateAsync(recorder).ConfigureAwait(false);

                Mission restarted = await h.Missions.RestartAsync(judge).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, restarted.Status);
                AssertEqual("armada/mux/worker", restarted.BranchName, "the Judge reviews the Worker's branch, not a fresh one");
                AssertNull(restarted.FailureReason, "the old failure is cleared");

                Voyage? reopened = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertEqual(VoyageStatusEnum.InProgress, reopened!.Status, "the voyage is reopened");
                Mission? restoredRecorder = await testDb.Driver.Missions.ReadAsync(recorder.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, restoredRecorder!.Status, "the stage cancelled because of the failure is restored");

                bool refused = false;
                try { await h.Missions.RestartAsync(worker).ConfigureAwait(false); }
                catch (InvalidOperationException) { refused = true; }
                AssertTrue(refused, "a Complete mission cannot be restarted");
            }));

            cases.Add(CaseAsync("uncommitted_work_is_committed", "Work a captain left uncommitted is committed before the diff is taken", TestTags.Positive, async () =>
            {
                string repo = Path.Combine(Path.GetTempPath(), "armada_uncommitted_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(repo);
                try
                {
                    await GitAsync(repo, "init", "-q", "-b", "main").ConfigureAwait(false);
                    await GitAsync(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "initial").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(repo, "work.txt"), "done by a model that forgot to commit\n").ConfigureAwait(false);

                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    Harness h = new Harness(testDb, new DirCreatingGitService(), new GitService(CreateLogging()));
                    h.Missions.OnGetMissionOutput = _ => "Done.";
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Captain mux = await CreateCaptainAsync(testDb, "Mux", AgentRuntimeEnum.Mux, CaptainStateEnum.Working).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("work")).ConfigureAwait(false);

                    // A review gate keeps the dock in place so the commit can be inspected afterwards.
                    Mission mission = await CreateRunningMissionAsync(testDb, h, vessel, voyage, mux, "Worker", true, repo).ConfigureAwait(false);
                    await h.Missions.HandleCompletionAsync(mux, mission.Id).ConfigureAwait(false);

                    string log = await GitAsync(repo, "log", "--format=%s", "-n", "1").ConfigureAwait(false);
                    AssertContains("Armada: commit work left uncommitted by Mux", log);
                    string status = await GitAsync(repo, "status", "--porcelain").ConfigureAwait(false);
                    AssertEqual("", status.Trim(), "nothing is left uncommitted");
                }
                finally
                {
                    try { Directory.Delete(repo, true); } catch { }
                }
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Multi-Runtime Flow", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<Captain> CreateCaptainAsync(TestDatabase testDb, string name, AgentRuntimeEnum runtime, CaptainStateEnum state = CaptainStateEnum.Idle)
        {
            Captain captain = new Captain(name, runtime);
            captain.State = state;
            return await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("flow-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_flow_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_flow_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateRunningMissionAsync(TestDatabase testDb, Harness h, Vessel vessel, Voyage voyage, Captain captain, string persona, bool requiresReview, string? worktreePath = null)
        {
            Mission mission = new Mission("[" + persona + "] Task", "Do the task");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.RequiresReview = requiresReview;
            mission.Status = MissionStatusEnum.InProgress;
            mission.CaptainId = captain.Id;
            mission.BranchName = "armada/flow/" + persona.ToLowerInvariant();
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = worktreePath ?? Path.Combine(h.Settings.DocksDirectory, vessel.Name, mission.Id);
            Directory.CreateDirectory(dock.WorktreePath);
            dock.BranchName = mission.BranchName;
            dock.Active = true;
            dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

            mission.DockId = dock.Id;
            await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
            return mission;
        }

        private static async Task<string> GitAsync(string directory, params string[] args)
        {
            ProcessStartInfo info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            using Process process = Process.Start(info)!;
            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return output;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Private-Classes

        private sealed class Harness
        {
            public ArmadaSettings Settings { get; }

            public ICaptainService Captains { get; }

            public MissionService Missions { get; }

            public Harness(TestDatabase testDb, IGitService dockGit, IGitService? missionGit = null)
            {
                LoggingModule logging = CreateLogging();
                Settings = new ArmadaSettings();
                Settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_flow_docks_" + Guid.NewGuid().ToString("N"));
                Settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_flow_repos_" + Guid.NewGuid().ToString("N"));
                IDockService docks = new DockService(logging, testDb.Driver, Settings, dockGit);
                Captains = new CaptainService(logging, testDb.Driver, Settings, dockGit, docks);
                Missions = new MissionService(logging, testDb.Driver, Settings, docks, Captains, git: missionGit ?? dockGit);
            }
        }

        #endregion
    }
}
