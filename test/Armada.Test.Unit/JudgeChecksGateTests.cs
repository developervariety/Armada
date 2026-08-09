namespace Armada.Test.Unit
{
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies the real-signal completion gate in UpdateVoyageTerminalStatusAsync: a voyage may only
    /// reach Complete when its Checks are green. A failed Check overrides a Judge PASS (voyage Fails);
    /// a pending Check holds completion; a voyage with no Checks is unaffected (backward compatible).
    /// </summary>
    public sealed class JudgeChecksGateTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "JudgeChecksGate";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_gate_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_gate_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        /// <summary>Seeds a voyage whose missions are all terminal (Worker WorkProduced + Judge Complete,
        /// so the Judge "passed") and returns the service + the still-InProgress voyage.</summary>
        private async Task<(MissionService svc, Voyage voyage)> SeedJudgePassedVoyageAsync(TestDatabase testDb)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService svc = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);

            Vessel vessel = new Vessel("gate-vessel", "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("gate-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission worker = new Mission("[Worker] Implement", "worker description");
            worker.VesselId = vessel.Id;
            worker.VoyageId = voyage.Id;
            worker.Persona = "Worker";
            worker.Status = MissionStatusEnum.WorkProduced;
            await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

            Mission judge = new Mission("[Judge] Review", "judge description");
            judge.VesselId = vessel.Id;
            judge.VoyageId = voyage.Id;
            judge.Persona = "Judge";
            judge.Status = MissionStatusEnum.Complete;
            await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

            return (svc, voyage);
        }

        private async Task AddCheckAsync(TestDatabase testDb, string voyageId, CheckRunStatusEnum status)
        {
            CheckRun run = new CheckRun
            {
                VoyageId = voyageId,
                Label = "Build",
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                Command = "dotnet build",
                WorkingDirectory = "C:/temp",
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : 1,
                Output = status == CheckRunStatusEnum.Passed ? "Build succeeded." : "Build failed.",
                Summary = "check"
            };
            await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("AllChecksGreen_VoyageCompletes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Passed).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, after!.Status, "green Checks -> voyage Complete");
                }
            }).ConfigureAwait(false);

            await RunTest("FailedCheck_OverridesJudgePass_VoyageFails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Failed).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Failed, after!.Status, "a failed Check overrides the Judge PASS -> voyage Failed");
                }
            }).ConfigureAwait(false);

            await RunTest("PendingCheck_HoldsCompletion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Pending).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.InProgress, after!.Status, "a pending Check holds completion -> voyage not Complete");
                }
            }).ConfigureAwait(false);

            await RunTest("NoChecks_VoyageCompletes_BackwardCompatible", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, after!.Status, "no Checks -> voyage Complete (backward compatible)");
                }
            }).ConfigureAwait(false);

            // Judge-level gate (obj_mrzqhz12): the pure classifier behind a Judge PASS.
            await RunTest("JudgeGate_Classify_PureCases", () =>
            {
                CheckRun passed = new CheckRun { Status = CheckRunStatusEnum.Passed };
                CheckRun failed = new CheckRun { Status = CheckRunStatusEnum.Failed };
                CheckRun pending = new CheckRun { Status = CheckRunStatusEnum.Pending };
                CheckRun running = new CheckRun { Status = CheckRunStatusEnum.Running };
                CheckRun canceled = new CheckRun { Status = CheckRunStatusEnum.Canceled };

                AssertEqual(
                    MissionService.JudgeCheckGate.GreenChecks,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passed, canceled }, "review ok"),
                    "Green Checks classify GreenChecks; Canceled Checks are ignored.");
                AssertEqual(
                    MissionService.JudgeCheckGate.HasFailed,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passed, failed }, "review ok"),
                    "A failed Check overrides the PASS.");
                AssertEqual(
                    MissionService.JudgeCheckGate.HasPending,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passed, pending }, "review ok"),
                    "A pending Check holds the PASS.");
                AssertEqual(
                    MissionService.JudgeCheckGate.HasPending,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passed, running }, "review ok"),
                    "A running Check holds the PASS.");
                AssertEqual(
                    MissionService.JudgeCheckGate.NoChecksNoExclusion,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun>(), "review ok"),
                    "No Checks without a documented exclusion rejects the PASS.");
                AssertEqual(
                    MissionService.JudgeCheckGate.NoChecksWithExclusion,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun>(), "review ok\n[JUDGE-CHECK-EXCLUSION] environmental exclusion: no container runtime on this host"),
                    "No Checks WITH the documented exclusion marker is accepted.");
                AssertEqual(
                    MissionService.JudgeCheckGate.NoChecksWithExclusion,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { canceled }, "[JUDGE-CHECK-EXCLUSION]"),
                    "Only-Canceled Checks count as no Checks for the exclusion path.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // Judge-level gate against the database: voyage-scoped Checks reach the classifier.
            await RunTest("JudgeGate_CollectsVoyageAndMissionChecks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService svc = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);

                    Vessel vessel = new Vessel("gate-vessel-2", "https://github.com/test/repo.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage("gate-voyage-2");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review 2", "judge description");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    // Voyage-scoped Check, green -> gate passes.
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Passed).ConfigureAwait(false);
                    AssertEqual(
                        MissionService.JudgeCheckGate.GreenChecks,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "A green voyage-scoped Check satisfies the Judge gate.");

                    // Flip the voyage Check to Failed -> gate rejects.
                    CheckRun? run = (await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery { VoyageId = voyage.Id }, CancellationToken.None).ConfigureAwait(false)).Objects.FirstOrDefault();
                    AssertNotNull(run, "Check run should exist");
                    run!.Status = CheckRunStatusEnum.Failed;
                    run.ExitCode = 1;
                    await testDb.Driver.CheckRuns.UpdateAsync(run).ConfigureAwait(false);
                    AssertEqual(
                        MissionService.JudgeCheckGate.HasFailed,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "A failed voyage-scoped Check rejects the Judge PASS.");

                    // Mission-scoped Pending Check holds even when the voyage Check is green again.
                    run.Status = CheckRunStatusEnum.Passed;
                    run.ExitCode = 0;
                    await testDb.Driver.CheckRuns.UpdateAsync(run).ConfigureAwait(false);
                    CheckRun missionCheck = new CheckRun
                    {
                        MissionId = judge.Id,
                        Label = "UnitTest",
                        Type = CheckRunTypeEnum.UnitTest,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Pending,
                        Command = "dotnet test",
                        WorkingDirectory = "C:/temp",
                        Summary = "check"
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(missionCheck).ConfigureAwait(false);
                    AssertEqual(
                        MissionService.JudgeCheckGate.HasPending,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "A pending mission-scoped Check holds the Judge PASS.");
                }
            }).ConfigureAwait(false);

            // The NoChecksNoExclusion failure reason must not name a tool captains do not
            // receive. The old text told the Judge to run armada_run_check, which is
            // operator-only -- a closed loop the captain could not exit (six High papercuts).
            await RunTest("JudgeGate_NoChecksFailureReason_DoesNotNameOperatorOnlyTool", () =>
            {
                string reason = MissionService.JudgeNoChecksFailureReason;
                AssertContains("[JUDGE-CHECK-EXCLUSION]", reason,
                    "the reason still documents the exclusion-marker escape");
                AssertFalse(reason.Contains("armada_run_check", StringComparison.Ordinal),
                    "the reason must not instruct the captain to run an operator-only tool");
                AssertContains("attached by the operator", reason,
                    "the reason states who attaches Checks instead of ordering the captain to");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
