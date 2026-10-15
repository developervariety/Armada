namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Hosting;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End to end: a mission whose Mux captain (a shim CLI) hits a denied tool call, answers anyway, and exits 2 with
    /// completed_with_errors and no final message. The mission completes (it is not a crash) with the reply rebuilt from
    /// the stream as its AgentOutput, and the lifecycle handler's mission telemetry carries mux's reported usage and the
    /// endpoint's model. Runs on the Admiral host and on an in-process Harbor.
    /// </summary>
    public sealed class MuxMissionTelemetrySuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.MuxMissionTelemetry";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            foreach (bool onHarbor in new bool[] { false, true })
            {
                string where = onHarbor ? "harbor" : "admiral_host";
                cases.Add(CaseAsync("mux_completed_with_errors_mission_completes_with_usage_" + where,
                    "A Mux mission (" + where.Replace('_', ' ') + ") that exits 2 after completed_with_errors completes with the rebuilt reply and mux's usage and model",
                    TestTags.Positive, async () =>
                {
                    using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                    using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                    InProcessHarbor? harbor = null;
                    try
                    {
                        if (onHarbor)
                        {
                            HarborDockSettings discovered = git.Settings.Clone();
                            discovered.RepositoryRoots.Add(git.CodeRoot);
                            harbor = await s.ConnectAsync("hbr_mux_e2e", discovered).ConfigureAwait(false);
                        }

                        Vessel vessel = await s.CreateVesselAsync(git.Origin).ConfigureAwait(false);
                        await s.CreateCaptainAsync().ConfigureAwait(false);
                        Mission mission = await s.CreateMissionAsync(vessel).ConfigureAwait(false);
                        AssertTrue(await s.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "the mission was assigned");

                        Mission? done = await WaitForEndAsync(s, mission.Id).ConfigureAwait(false);
                        AssertTrue(done!.Status == MissionStatusEnum.WorkProduced || done.Status == MissionStatusEnum.Complete,
                            "the mission completed rather than failing: " + done.Status + " " + done.FailureReason);
                        AssertEqual(MuxJsonlFixtures.FinalReply, (done.AgentOutput ?? String.Empty).Trim().Replace("\r\n", "\n"), "the reply rebuilt from the stream");

                        ChatTurnTelemetryRecorder? telemetry = s.TakenTelemetry;
                        AssertNotNull(telemetry, "the completion flow took the mission's telemetry");
                        AssertEqual(1200L, telemetry!.InputTokens ?? 0, "mux's reported input tokens");
                        AssertEqual(340L, telemetry.OutputTokens ?? 0, "mux's reported output tokens");
                        AssertEqual(100L, telemetry.CachedTokens ?? 0);
                        AssertEqual("gpt-oss:20b", telemetry.Model, "the endpoint's model");
                        AssertNull(s.Handler.TakeMissionTelemetry(mission.Id), "taken once");
                    }
                    finally
                    {
                        harbor?.Dispose();
                    }
                }));
            }

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Mux mission outcome and telemetry (end to end)",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<Mission?> WaitForEndAsync(Scenario s, string missionId)
        {
            Mission? done = null;
            MonotonicDeadline deadline = MonotonicDeadline.After(TimeSpan.FromSeconds(60));
            while (!deadline.Passed)
            {
                done = await s.Db.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                if (done != null && (done.Status == MissionStatusEnum.Complete
                    || done.Status == MissionStatusEnum.Failed
                    || done.Status == MissionStatusEnum.LandingFailed
                    || done.Status == MissionStatusEnum.WorkProduced && done.AgentOutput != null))
                    return done;
                await Task.Delay(50).ConfigureAwait(false);
            }

            AssertTrue(false, "the mission finished: " + done?.Status);
            return done;
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

        #region Private-Types

        private sealed class Scenario : IDisposable
        {
            public TestDatabase Db { get; }

            public LoggingModule Logging { get; }

            public ArmadaSettings Settings { get; }

            public HarborConnectionManager Manager { get; }

            public MissionService Missions { get; }

            public AgentLifecycleHandler Handler { get; }

            public AgentRuntimeFactory Runtimes { get; }

            public ChatTurnTelemetryRecorder? TakenTelemetry { get; private set; } = null;

            private readonly string _Root;

            private Scenario(TestDatabase db, HarborDockGitFixture git)
            {
                Db = db;
                Logging = new LoggingModule();
                Logging.Settings.EnableConsole = false;
                Settings = new ArmadaSettings();
                Settings.LogDirectory = Path.Combine(git.AdmiralData, "logs");
                Settings.DocksDirectory = Path.Combine(git.AdmiralData, "docks");
                Settings.ReposDirectory = Path.Combine(git.AdmiralData, "repos");

                // The captain: a Mux CLI that commits its work, prints a completed_with_errors run, and exits 2 without
                // writing the final message (as mux does on a non-zero exit).
                _Root = TestTemp.NewDirectory("mux_mission_e2e");
                string shim = MuxShimCli.Write(Path.Combine(_Root, "bin"), MuxJsonlFixtures.CompletedWithErrorsRun(), 2, null, "captain-work.txt");
                Runtimes = new AgentRuntimeFactory(Logging);
                LoggingModule logging = Logging;
                Runtimes.Override(AgentRuntimeEnum.Mux, () => new MuxRuntime(logging) { ExecutablePath = shim });

                Manager = new HarborConnectionManager(new HarborService(db.Driver, Logging), Logging, null);
                GitService gitService = new GitService(Logging);
                DockHostResolver hosts = new DockHostResolver(gitService, Logging, Manager);
                DockService docks = new DockService(Logging, db.Driver, Settings, gitService) { Hosts = hosts };
                CaptainService captains = new CaptainService(Logging, db.Driver, Settings, gitService, docks) { DockHosts = hosts };
                Missions = new MissionService(Logging, db.Driver, Settings, docks, captains, git: gitService) { DockHosts = hosts };
                AdmiralService admiral = new AdmiralService(Logging, db.Driver, Settings, captains, Missions, new VoyageService(Logging, db.Driver), docks);

                Handler = new AgentLifecycleHandler(
                    Logging, db.Driver, Settings, Runtimes, admiral, new MessageTemplateService(Logging), null, null,
                    (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) => Task.CompletedTask);
                Handler.SetHarborConnections(Manager);

                MissionLandingHandler landing = new MissionLandingHandler(Logging, db.Driver, Settings, gitService, new MergeQueueService(Logging, db.Driver, Settings, gitService),
                    new MessageTemplateService(Logging), null, docks, null) { DockHosts = hosts };

                // As MissionService will: take the launch's telemetry when the mission's output is collected.
                Missions.ResolveDockPlacementAsync = Handler.ResolveDockPlacementAsync;
                Missions.OnGetMissionOutput = missionId =>
                {
                    TakenTelemetry = Handler.TakeMissionTelemetry(missionId);
                    return Handler.GetAndClearMissionOutput(missionId);
                };
                admiral.OnLaunchAgent = Handler.HandleLaunchAgentAsync;
                admiral.OnCaptureDiff = landing.HandleCaptureDiffAsync;
                admiral.OnMissionComplete = landing.HandleMissionCompleteAsync;
            }

            public static async Task<Scenario> CreateAsync(HarborDockGitFixture git)
            {
                TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                return new Scenario(db, git);
            }

            public Task<InProcessHarbor> ConnectAsync(string harborId, HarborDockSettings settings)
            {
                HarborDockManager docks = new HarborDockManager(() => settings, Logging);
                LocalHarborJobRunner runner = new LocalHarborJobRunner(Logging, Runtimes, Path.Combine(_Root, "harbor_scratch"))
                {
                    JobLogs = new HarborLogPaths(Path.Combine(_Root, "harbor_logs"))
                };
                return InProcessHarbor.ConnectAsync(Manager, harborId, null, null, Runtimes,
                    new List<string> { "Mux", "git" }, Logging, null, runner, docks);
            }

            public async Task<Vessel> CreateVesselAsync(string repoUrl)
            {
                Vessel vessel = new Vessel("app", repoUrl);
                vessel.DefaultBranch = "main";
                vessel.LandingMode = LandingModeEnum.LocalMerge;
                return await Db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            }

            public async Task<Captain> CreateCaptainAsync()
            {
                Captain captain = new Captain("mux-captain-" + Guid.NewGuid().ToString("N").Substring(0, 8), AgentRuntimeEnum.Mux);
                captain.State = CaptainStateEnum.Idle;
                return await Db.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
            }

            public async Task<Mission> CreateMissionAsync(Vessel vessel)
            {
                Mission mission = new Mission("Add a file", "Add a file to the repository");
                mission.VesselId = vessel.Id;
                mission.Status = MissionStatusEnum.Pending;
                return await Db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
            }

            public void Dispose()
            {
                Db.Dispose();
            }
        }

        #endregion
    }
}
