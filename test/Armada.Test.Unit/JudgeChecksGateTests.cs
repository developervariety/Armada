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
        private async Task<(MissionService svc, Voyage voyage)> SeedReportOnlyJudgePassedVoyageAsync(
            TestDatabase testDb,
            MissionModeEnum mode)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService svc = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);

            Vessel vessel = new Vessel("report-only-vessel", "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("report-only-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission worker = new Mission("[Worker] Audit", "worker description");
            worker.VesselId = vessel.Id;
            worker.VoyageId = voyage.Id;
            worker.Persona = "Worker";
            worker.Mode = mode;
            worker.Status = MissionStatusEnum.WorkProduced;
            await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

            Mission judge = new Mission("[Judge] Review", "judge description");
            judge.VesselId = vessel.Id;
            judge.VoyageId = voyage.Id;
            judge.Persona = "Judge";
            judge.Mode = mode;
            judge.Status = MissionStatusEnum.Complete;
            await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

            return (svc, voyage);
        }

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

        private const string _ReviewedCommit = "1111111111111111111111111111111111111111";
        private const string _OtherCommit = "2222222222222222222222222222222222222222";
        private const string _WorkBranch = "armada/worker/msn_committed_work";

        /// <summary>Seeds a voyage whose work is measurable: a Worker that reached WorkProduced on a
        /// branch and commit, and a Judge reviewing that same tip. The Worker may have committed
        /// nothing; its recorded commit is still the tip under review.</summary>
        private async Task<(MissionService svc, Voyage voyage, Mission judge)> SeedVoyageWithMeasurableWorkAsync(
            TestDatabase testDb,
            MissionModeEnum mode,
            MissionStatusEnum judgeStatus)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService svc = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);

            Vessel vessel = new Vessel("measurable-vessel", "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("measurable-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission worker = new Mission("[Worker] Implement", "worker description");
            worker.VesselId = vessel.Id;
            worker.VoyageId = voyage.Id;
            worker.Persona = "Worker";
            worker.Mode = mode;
            worker.Status = MissionStatusEnum.WorkProduced;
            worker.BranchName = _WorkBranch;
            worker.CommitHash = _ReviewedCommit;
            await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

            Mission judge = new Mission("[Judge] Review", "judge description");
            judge.VesselId = vessel.Id;
            judge.VoyageId = voyage.Id;
            judge.Persona = "Judge";
            judge.Mode = mode;
            judge.Status = judgeStatus;
            judge.BranchName = _WorkBranch;
            judge.CommitHash = _ReviewedCommit;
            judge.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";
            judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

            return (svc, voyage, judge);
        }

        private async Task<CheckRun> AddExecutedCheckAsync(TestDatabase testDb, string voyageId, CheckRunTypeEnum type, CheckRunStatusEnum status, string commit)
        {
            CheckRun run = new CheckRun
            {
                VoyageId = voyageId,
                Label = type.ToString(),
                Type = type,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                Command = type == CheckRunTypeEnum.Build ? "dotnet build" : "dotnet test",
                WorkingDirectory = "C:/temp",
                BranchName = _WorkBranch,
                CommitHash = commit,
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : 1,
                Summary = "check"
            };
            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        /// <summary>Creates a Check exactly as dispatch-time arming does: Pending, never started,
        /// carrying the unresolved placeholder command and no branch or commit.</summary>
        private async Task<CheckRun> AddArmedIntentMarkerAsync(TestDatabase testDb, string voyageId, CheckRunTypeEnum type)
        {
            CheckRun run = new CheckRun
            {
                VoyageId = voyageId,
                Label = type.ToString() + " (armed at dispatch)",
                Type = type,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Pending
            };
            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
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

            // Judge-level gate: the pure classifier behind a Judge PASS.
            await RunTest("JudgeGate_Classify_PureCases", () =>
            {
                CheckRun passed = new CheckRun { Status = CheckRunStatusEnum.Passed };
                CheckRun failed = new CheckRun { Status = CheckRunStatusEnum.Failed };
                // These fixtures carry a real command on purpose. A Pending record with no command
                // is an armed intent marker, which is a different case with its own assertion below.
                CheckRun pending = new CheckRun { Status = CheckRunStatusEnum.Pending, Command = "dotnet build" };
                CheckRun running = new CheckRun { Status = CheckRunStatusEnum.Running, Command = "dotnet build" };
                CheckRun canceled = new CheckRun { Status = CheckRunStatusEnum.Canceled };
                CheckRun marker = new CheckRun { Status = CheckRunStatusEnum.Pending };

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
                AssertEqual(
                    MissionService.JudgeCheckGate.GreenChecks,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passed, marker }, "review ok"),
                    "An armed intent marker never ran, so it neither holds nor decides the PASS.");
                AssertEqual(
                    MissionService.JudgeCheckGate.NoChecksNoExclusion,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { marker }, "review ok"),
                    "Only-marker Checks count as no Checks, which is reported instead of an unresolvable wait.");
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

            // A rejection that names only the rule leaves the operator hunting for WHICH record
            // blocked the PASS. When several Checks fail for one environmental cause it is easy to
            // resolve all but one, and the leftover silently rejects the PASS hours later.
            await RunTest("DescribeBlockingChecks_NamesEachFailedCheck", () =>
            {
                List<CheckRun> checks = new List<CheckRun>
                {
                    new CheckRun { Id = "chk_b", Type = CheckRunTypeEnum.UnitTest, Label = "Voyage gate: UnitTest", Status = CheckRunStatusEnum.Failed },
                    new CheckRun { Id = "chk_a", Type = CheckRunTypeEnum.Build, Label = "Voyage gate: Build", Status = CheckRunStatusEnum.Failed },
                    new CheckRun { Id = "chk_ok", Type = CheckRunTypeEnum.Build, Label = "green", Status = CheckRunStatusEnum.Passed },
                    new CheckRun { Id = "chk_x", Type = CheckRunTypeEnum.Build, Label = "cancelled", Status = CheckRunStatusEnum.Canceled }
                };

                string described = MissionService.DescribeBlockingChecks(checks, CheckRunStatusEnum.Failed);
                AssertContains("chk_a", described, "the failed Build check is named");
                AssertContains("chk_b", described, "the failed UnitTest check is named");
                AssertContains("Voyage gate: Build", described, "the label is carried so the operator can recognise it");
                AssertFalse(described.Contains("chk_ok", StringComparison.Ordinal),
                    "a passing Check is not reported as blocking");
                AssertFalse(described.Contains("chk_x", StringComparison.Ordinal),
                    "a Canceled Check is not reported as blocking - the gate ignores those");
                AssertTrue(described.IndexOf("chk_a", StringComparison.Ordinal) < described.IndexOf("chk_b", StringComparison.Ordinal),
                    "ordering is deterministic so the same failure renders identically each time");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // The helper must be safe on the paths that produce no blocking records at all,
            // because the caller appends its result unconditionally.
            await RunTest("DescribeBlockingChecks_EmptyWhenNothingBlocks", () =>
            {
                AssertEqual(String.Empty, MissionService.DescribeBlockingChecks(null, CheckRunStatusEnum.Failed),
                    "a null collection renders as empty rather than throwing");
                AssertEqual(String.Empty, MissionService.DescribeBlockingChecks(new List<CheckRun>(), CheckRunStatusEnum.Failed),
                    "an empty collection renders as empty");
                AssertEqual(
                    String.Empty,
                    MissionService.DescribeBlockingChecks(
                        new List<CheckRun> { new CheckRun { Id = "chk_ok", Status = CheckRunStatusEnum.Passed } },
                        CheckRunStatusEnum.Failed),
                    "no failed records renders as empty");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // A Check with no Label must still be identifiable; falling back to the type keeps the
            // message useful instead of emitting a bare id with empty parentheses.
            await RunTest("DescribeBlockingChecks_FallsBackToTypeWhenLabelMissing", () =>
            {
                string described = MissionService.DescribeBlockingChecks(
                    new List<CheckRun>
                    {
                        new CheckRun { Id = "chk_nolabel", Type = CheckRunTypeEnum.UnitTest, Label = null, Status = CheckRunStatusEnum.Failed }
                    },
                    CheckRunStatusEnum.Failed);
                AssertContains("chk_nolabel", described, "the id is present");
                AssertContains("UnitTest", described, "the type stands in for the missing label");
                AssertFalse(described.Contains("()", StringComparison.Ordinal),
                    "no empty parentheses are emitted for a missing label");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // A voyage-armed Check is stamped once, at the first stage that commits, and every later
            // stage commits on top. A green that measured the first commit says nothing about the
            // tip the Judge reviews, and a gate that reads Status alone honors it anyway.
            await RunTest("GateRules_StaleGreen_IsAGreenForADifferentCommit", () =>
            {
                CheckRun passedAtA = new CheckRun
                {
                    Status = CheckRunStatusEnum.Passed,
                    Command = "dotnet build",
                    CommitHash = "aaaaaaaa1111111111111111111111111111aaaa"
                };
                AssertTrue(CheckRunGateRules.IsStale(passedAtA, "bbbbbbbb2222222222222222222222222222bbbb"),
                    "a green for commit A is stale when the review is of commit B");
                AssertFalse(CheckRunGateRules.IsStale(passedAtA, "aaaaaaaa1111111111111111111111111111aaaa"),
                    "a green for the reviewed commit is not stale");
                AssertFalse(CheckRunGateRules.IsStale(passedAtA, "aaaaaaaa111"),
                    "an abbreviated reviewed commit matches by prefix");
                AssertFalse(CheckRunGateRules.IsStale(passedAtA, null),
                    "no reviewed commit means nothing to compare against");
                AssertFalse(CheckRunGateRules.IsStale(passedAtA, "   "),
                    "a blank reviewed commit means nothing to compare against");

                CheckRun passedUnstampedOnVoyage = new CheckRun { Status = CheckRunStatusEnum.Passed, Command = "dotnet build", VoyageId = "vyg_example" };
                AssertTrue(CheckRunGateRules.IsStale(passedUnstampedOnVoyage, "bbbbbbbb2222222222222222222222222222bbbb"),
                    "a voyage green with no commit measured the default branch, not the work, so it is stale");
                CheckRun passedUnstampedNoVoyage = new CheckRun { Status = CheckRunStatusEnum.Passed, Command = "dotnet build" };
                AssertFalse(CheckRunGateRules.IsStale(passedUnstampedNoVoyage, "bbbbbbbb2222222222222222222222222222bbbb"),
                    "a record attached to no voyage is left alone, because nothing re-arms it");

                CheckRun failedAtA = new CheckRun { Status = CheckRunStatusEnum.Failed, Command = "dotnet build", CommitHash = "aaaaaaaa1111" };
                AssertTrue(CheckRunGateRules.IsStale(failedAtA, "bbbbbbbb2222"),
                    "a Failed record for an older commit is stale too: the reviewed commit may be its fix");
                AssertFalse(CheckRunGateRules.IsStale(failedAtA, "aaaaaaaa1111"),
                    "a Failed record for the reviewed commit is a real failure");

                CheckRun canceledAtA = new CheckRun { Status = CheckRunStatusEnum.Canceled, Command = "dotnet build", CommitHash = "aaaaaaaa1111" };
                AssertFalse(CheckRunGateRules.IsStale(canceledAtA, "bbbbbbbb2222"),
                    "a Canceled record does not participate at all");

                AssertTrue(CheckRunGateRules.SameCommit("ABCDEF0123456789", "abcdef01"),
                    "commit comparison is case-insensitive and prefix-tolerant");
                AssertFalse(CheckRunGateRules.SameCommit("abcdef", "abcdef0123"),
                    "an abbreviation shorter than seven characters is too weak to match");
                AssertFalse(CheckRunGateRules.SameCommit(null, "abcdef0123"),
                    "a missing side never matches");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // The Judge gate must hold a PASS on a stale green exactly as it holds one on a Pending
            // record: the executor re-arms the Check for the reviewed tip while the hold lasts.
            // Without the reviewed commit the classifier keeps its old, Status-only behaviour.
            await RunTest("JudgeGate_StaleGreen_HoldsThePass", () =>
            {
                CheckRun passedAtA = new CheckRun
                {
                    Id = "chk_stale",
                    Type = CheckRunTypeEnum.UnitTest,
                    Status = CheckRunStatusEnum.Passed,
                    Command = "dotnet test",
                    CommitHash = "aaaaaaaa1111111111111111111111111111aaaa"
                };
                const string tip = "bbbbbbbb2222222222222222222222222222bbbb";

                AssertEqual(
                    MissionService.JudgeCheckGate.HasPending,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passedAtA }, "review ok", tip),
                    "a green for an older commit holds the PASS for the reviewed tip");
                AssertEqual(
                    MissionService.JudgeCheckGate.GreenChecks,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passedAtA }, "review ok", "aaaaaaaa1111111111111111111111111111aaaa"),
                    "a green for the reviewed commit passes the gate");
                AssertEqual(
                    MissionService.JudgeCheckGate.GreenChecks,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passedAtA }, "review ok"),
                    "with no reviewed commit the classifier reads Status only, as before");

                CheckRun failedAtTip = new CheckRun { Status = CheckRunStatusEnum.Failed, Command = "dotnet test", CommitHash = tip };
                AssertEqual(
                    MissionService.JudgeCheckGate.HasFailed,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { passedAtA, failedAtTip }, "review ok", tip),
                    "a real failure still outranks a hold");

                CheckRun failedAtA = new CheckRun { Id = "chk_stale_red", Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Failed, Command = "dotnet test", CommitHash = "aaaaaaaa1111111111111111111111111111aaaa" };
                AssertEqual(
                    MissionService.JudgeCheckGate.HasPending,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { failedAtA }, "review ok", tip),
                    "a failure for an older commit holds the PASS instead of rejecting it: the tip may be the fix, and the re-armed record decides");
                AssertEqual(
                    MissionService.JudgeCheckGate.HasFailed,
                    MissionService.ClassifyJudgeCheckGate(new List<CheckRun> { failedAtA }, "review ok"),
                    "with no reviewed commit the classifier keeps its Status-only behaviour: a failure rejects");
                AssertContains("stale: Failed at aaaaaaaa1111", MissionService.DescribeUnresolvedChecks(new List<CheckRun> { failedAtA }, tip),
                    "the hold names the stale failure with its status and commit");

                string described = MissionService.DescribeUnresolvedChecks(new List<CheckRun> { passedAtA }, tip);
                AssertContains("chk_stale", described, "the hold names the stale record");
                AssertContains("stale", described, "the hold says WHY the record does not count");
                AssertContains("aaaaaaaa1111", described, "the hold names the commit the record measured");
                AssertContains("bbbbbbbb2222", described, "the hold names the commit under review");
                AssertEqual(String.Empty, MissionService.DescribeUnresolvedChecks(new List<CheckRun> { passedAtA }, null),
                    "with no reviewed commit a Passed record is not described as blocking");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // A Check record is created before it runs, so its existence and its signal are two
            // different facts. A record armed at dispatch has never executed: it holds no command
            // output and carries no branch, so it can neither vouch for the work nor be waited on.
            await RunTest("GateRules_IntentMarkerIsExcluded_RealRecordsAreNot", () =>
            {
                CheckRun armed = new CheckRun { Status = CheckRunStatusEnum.Pending };
                AssertTrue(CheckRunGateRules.IsUnexecutedIntentMarker(armed),
                    "a Pending record with the placeholder command and no start time is an intent marker");
                AssertFalse(CheckRunGateRules.ParticipatesInRealSignalGate(armed),
                    "an intent marker does not decide the gate");
                AssertFalse(CheckRunGateRules.IsUnresolved(armed),
                    "an intent marker is not something the gate can wait on");

                CheckRun queued = new CheckRun { Status = CheckRunStatusEnum.Pending, Command = "dotnet build" };
                AssertFalse(CheckRunGateRules.IsUnexecutedIntentMarker(queued),
                    "a Pending record with a real command is genuine queued work, not a marker");
                AssertTrue(CheckRunGateRules.IsUnresolved(queued),
                    "genuine queued work still holds the gate");

                CheckRun started = new CheckRun
                {
                    Status = CheckRunStatusEnum.Pending,
                    StartedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                };
                AssertFalse(CheckRunGateRules.IsUnexecutedIntentMarker(started),
                    "a record that has started is not an intent marker whatever its command");
                AssertTrue(CheckRunGateRules.IsUnresolved(started),
                    "a started record still holds the gate");

                CheckRun canceled = new CheckRun { Status = CheckRunStatusEnum.Canceled, Command = "dotnet build" };
                AssertFalse(CheckRunGateRules.ParticipatesInRealSignalGate(canceled),
                    "a Canceled record is ruled out by the operator");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // The reported shape: a voyage carrying a dispatch-armed marker beside a genuinely
            // green Check. The marker can never resolve while the voyage is live -- the only
            // executor of a voyage-linked Pending Check requires the voyage to be Complete, and
            // the voyage cannot complete while the Check is unresolved -- so the gate held a
            // correct PASS until its wait budget ran out and rejected it.
            await RunTest("JudgeGate_ArmedMarkerBesideGreenCheck_AcceptsPass", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService svc = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git);

                    Vessel vessel = new Vessel("marker-vessel", "https://github.com/test/repo.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage("marker-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "judge description");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    // Markers alone are not signal: the PASS is rejected for the honest reason,
                    // rather than held for a resolution that can never arrive.
                    await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.Build).ConfigureAwait(false);
                    await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.UnitTest).ConfigureAwait(false);
                    AssertEqual(
                        MissionService.JudgeCheckGate.NoChecksNoExclusion,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "armed markers alone are reported as no Checks, not as an unresolvable wait");

                    // The reported shape: add the genuinely green Check the operator attached.
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Passed).ConfigureAwait(false);
                    AssertEqual(
                        MissionService.JudgeCheckGate.GreenChecks,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "a green Check beside armed markers satisfies the gate; the markers no longer hold the PASS");

                    // The exclusion must not be over-broad: real unresolved work still holds.
                    CheckRun queued = new CheckRun
                    {
                        VoyageId = voyage.Id,
                        Label = "UnitTest",
                        Type = CheckRunTypeEnum.UnitTest,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Pending,
                        Command = "dotnet test",
                        WorkingDirectory = "C:/temp"
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(queued).ConfigureAwait(false);
                    AssertEqual(
                        MissionService.JudgeCheckGate.HasPending,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "a Check with a real command is genuine queued work and still holds the PASS");
                }
            }).ConfigureAwait(false);

            // The completion gate carries the same deadlock: a marker held the voyage InProgress
            // for ever, and the voyage reaching Complete is the only thing that would have run it.
            await RunTest("VoyageGate_ArmedMarkerDoesNotHoldCompletion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);
                    await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.Build).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Passed).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, after!.Status,
                        "an armed marker beside a green Check must not hold the voyage out of Complete");
                }
            }).ConfigureAwait(false);

            // The rejection message forced the operator to guess which record blocked the PASS.
            // The obvious wrong guess was a degraded captain, which benches healthy Judges and
            // fixes nothing, so the message must name the records instead.
            await RunTest("ReportOnlyAuditJudgePass_AcceptsWithoutChecksOrExclusion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedReportOnlyJudgePassedVoyageAsync(
                        testDb, MissionModeEnum.Audit).ConfigureAwait(false);

                    Mission? judge = (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false))
                        .FirstOrDefault(m => m.Persona == "Judge");
                    AssertNotNull(judge, "judge mission should exist");
                    judge!.AgentOutput = "report review\n[ARMADA:VERDICT] PASS";

                    AssertEqual(
                        MissionService.JudgeCheckGate.GreenChecks,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "a report-only Audit Judge PASS is accepted without code Checks");
                }
            }).ConfigureAwait(false);

            await RunTest("ReportOnlyResearchJudgePass_AcceptsWithoutChecksOrExclusion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedReportOnlyJudgePassedVoyageAsync(
                        testDb, MissionModeEnum.Research).ConfigureAwait(false);

                    Mission? judge = (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false))
                        .FirstOrDefault(m => m.Persona == "Judge");
                    AssertNotNull(judge, "judge mission should exist");
                    judge!.AgentOutput = "report review\n[ARMADA:VERDICT] PASS";

                    AssertEqual(
                        MissionService.JudgeCheckGate.GreenChecks,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "a report-only Research Judge PASS is accepted without code Checks");
                }
            }).ConfigureAwait(false);

            await RunTest("ReportOnlyJudgeCompletion_UsesReportSectionsAndCompletesVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedReportOnlyJudgePassedVoyageAsync(
                        testDb, MissionModeEnum.Audit).ConfigureAwait(false);
                    Mission? judge = (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false))
                        .FirstOrDefault(m => m.Persona == "Judge");
                    AssertNotNull(judge, "judge mission should exist");

                    Captain captain = new Captain("report-only-judge-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    judge!.Status = MissionStatusEnum.Pending;
                    judge.CaptainId = captain.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                    captain.CurrentMissionId = judge.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    svc.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "The report covers every requested finding and its conclusion.\n\n" +
                        "## Correctness\n" +
                        "The cited observations support the report without contradiction.\n\n" +
                        "## Evidence\n" +
                        "Each claim names the checked source and the exact supporting evidence.\n\n" +
                        "## Residual Risks\n" +
                        "No unresolved risk changes the report conclusion.\n\n" +
                        "## Verdict\n" +
                        "The report is ready for acceptance.\n\n" +
                        "[ARMADA:VERDICT] PASS";

                    await svc.HandleCompletionAsync(captain, judge.Id).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);

                    Mission? completedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    Voyage? completedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(completedJudge, "judge mission should remain readable");
                    AssertNotNull(completedVoyage, "voyage should remain readable");
                    AssertEqual(MissionStatusEnum.WorkProduced, completedJudge!.Status,
                        "a report-only Judge PASS must pass structural validation before terminal handling");
                    AssertEqual(VoyageStatusEnum.Complete, completedVoyage!.Status,
                        "a report-only Judge PASS must complete its voyage without code Checks");
                    AssertTrue(String.IsNullOrEmpty(completedJudge.FailureReason),
                        "a contract-compliant report-only PASS must not be degraded to NEEDS_REVISION");
                }
            }).ConfigureAwait(false);

            await RunTest("ReportOnlyJudgeCompletion_RejectsMissingEvidenceSection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedReportOnlyJudgePassedVoyageAsync(
                        testDb, MissionModeEnum.Research).ConfigureAwait(false);
                    Mission? judge = (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false))
                        .FirstOrDefault(m => m.Persona == "Judge");
                    AssertNotNull(judge, "judge mission should exist");

                    Captain captain = new Captain("report-only-invalid-judge-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    judge!.Status = MissionStatusEnum.Pending;
                    judge.CaptainId = captain.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                    captain.CurrentMissionId = judge.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    svc.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "The report covers the requested finding.\n\n" +
                        "## Correctness\n" +
                        "The conclusion follows from the reviewed material.\n\n" +
                        "## Residual Risks\n" +
                        "No unresolved risk changes the conclusion.\n\n" +
                        "[ARMADA:VERDICT] PASS";

                    await svc.HandleCompletionAsync(captain, judge.Id).ConfigureAwait(false);
                    Mission? rejectedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(rejectedJudge, "judge mission should remain readable");
                    AssertEqual(MissionStatusEnum.Failed, rejectedJudge!.Status,
                        "a report-only PASS without Evidence must be rejected");
                    AssertContains("Evidence", rejectedJudge.FailureReason ?? String.Empty,
                        "the failure reason must name the missing report-only section");
                }
            }).ConfigureAwait(false);

            await RunTest("ReportOnlyVoyage_LegacyFailedChecks_DoNotFailTerminalization", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedReportOnlyJudgePassedVoyageAsync(
                        testDb, MissionModeEnum.Research).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Failed).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, after!.Status,
                        "legacy code Checks must not fail a fully report-only voyage");
                }
            }).ConfigureAwait(false);

            await RunTest("ReportOnlyVoyage_LegacyPendingChecks_DoNotHoldTerminalization", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedReportOnlyJudgePassedVoyageAsync(
                        testDb, MissionModeEnum.Audit).ConfigureAwait(false);
                    await AddCheckAsync(testDb, voyage.Id, CheckRunStatusEnum.Pending).ConfigureAwait(false);
                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, after!.Status,
                        "legacy pending code Checks must not hold a fully report-only voyage");
                }
            }).ConfigureAwait(false);

            await RunTest("MixedAuditAndResearchVoyage_StillRequiresGreenChecks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedReportOnlyJudgePassedVoyageAsync(
                        testDb, MissionModeEnum.Audit).ConfigureAwait(false);
                    Mission? judge = (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false))
                        .FirstOrDefault(m => m.Persona == "Judge");
                    AssertNotNull(judge, "judge mission should exist");
                    judge!.Mode = MissionModeEnum.Research;
                    judge.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";
                    await testDb.Driver.Missions.UpdateAsync(judge, CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(
                        MissionService.JudgeCheckGate.NoChecksNoExclusion,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "an Audit and Research voyage is mixed-mode and still requires green Checks");
                }
            }).ConfigureAwait(false);

            await RunTest("MixedModeVoyage_StillRequiresGreenChecks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage) = await SeedJudgePassedVoyageAsync(testDb).ConfigureAwait(false);

                    Mission? judge = (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false))
                        .FirstOrDefault(m => m.Persona == "Judge");
                    AssertNotNull(judge, "judge mission should exist");
                    judge!.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";

                    AssertEqual(
                        MissionService.JudgeCheckGate.NoChecksNoExclusion,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "implementation voyages still require green independent Checks");
                }
            }).ConfigureAwait(false);

            // A voyage whose Checks were armed at dispatch reached its Judge before the executor ran
            // them -- for example a Judge-only continuation whose Worker committed nothing. The work
            // was measurable, so those records were queued work, yet the gate read them as "no
            // Checks" and rejected a valid PASS.
            await RunTest("JudgeGate_ArmedChecksOnMeasurableWork_HoldPassAndAreStampedAtReviewedCommit", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage, Mission judge) = await SeedVoyageWithMeasurableWorkAsync(
                        testDb, MissionModeEnum.Implementation, MissionStatusEnum.InProgress).ConfigureAwait(false);
                    CheckRun build = await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.Build).ConfigureAwait(false);
                    CheckRun unit = await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.UnitTest).ConfigureAwait(false);

                    AssertEqual(
                        MissionService.JudgeCheckGate.HasPending,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "armed Checks on measurable work are queued work: they hold the PASS, they do not reject it");

                    CheckRun? stampedBuild = await testDb.Driver.CheckRuns.ReadAsync(build.Id).ConfigureAwait(false);
                    CheckRun? stampedUnit = await testDb.Driver.CheckRuns.ReadAsync(unit.Id).ConfigureAwait(false);
                    AssertEqual(_WorkBranch, stampedBuild!.BranchName, "the armed Build is stamped with the reviewed branch");
                    AssertEqual(_ReviewedCommit, stampedBuild.CommitHash, "the armed Build is stamped with the reviewed commit");
                    AssertEqual(_WorkBranch, stampedUnit!.BranchName, "the armed UnitTest is stamped with the reviewed branch");
                    AssertEqual(_ReviewedCommit, stampedUnit.CommitHash, "the armed UnitTest is stamped with the reviewed commit");

                    // Once the executor has run both at the reviewed commit, the PASS stands.
                    foreach (CheckRun stamped in new[] { stampedBuild, stampedUnit })
                    {
                        stamped.Status = CheckRunStatusEnum.Passed;
                        stamped.Command = stamped.Type == CheckRunTypeEnum.Build ? "dotnet build" : "dotnet test";
                        stamped.StartedUtc = DateTime.UtcNow;
                        stamped.CompletedUtc = DateTime.UtcNow;
                        stamped.ExitCode = 0;
                        await testDb.Driver.CheckRuns.UpdateAsync(stamped).ConfigureAwait(false);
                    }

                    AssertEqual(
                        MissionService.JudgeCheckGate.GreenChecks,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "green Checks at the reviewed commit let the PASS stand");
                }
            }).ConfigureAwait(false);

            await RunTest("JudgeGate_FailedCheckAtReviewedCommit_StillRejectsPassBesideArmedChecks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage, Mission judge) = await SeedVoyageWithMeasurableWorkAsync(
                        testDb, MissionModeEnum.Implementation, MissionStatusEnum.InProgress).ConfigureAwait(false);
                    await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.UnitTest).ConfigureAwait(false);
                    await AddExecutedCheckAsync(testDb, voyage.Id, CheckRunTypeEnum.Build, CheckRunStatusEnum.Failed, _ReviewedCommit).ConfigureAwait(false);

                    AssertEqual(
                        MissionService.JudgeCheckGate.HasFailed,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "a failed Check at the reviewed commit still rejects the PASS");
                }
            }).ConfigureAwait(false);

            await RunTest("JudgeGate_GreenCheckAtAnotherCommit_DoesNotCountAsGreen", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage, Mission judge) = await SeedVoyageWithMeasurableWorkAsync(
                        testDb, MissionModeEnum.Implementation, MissionStatusEnum.InProgress).ConfigureAwait(false);
                    await AddExecutedCheckAsync(testDb, voyage.Id, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, _OtherCommit).ConfigureAwait(false);
                    await AddExecutedCheckAsync(testDb, voyage.Id, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, _OtherCommit).ConfigureAwait(false);

                    MissionService.JudgeCheckGate gate = await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(MissionService.JudgeCheckGate.HasPending, gate,
                        "a green for another commit is stale and must not satisfy the gate");
                }
            }).ConfigureAwait(false);

            await RunTest("VoyageGate_ArmedChecksOnMeasurableWork_HoldCompletion", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage, Mission judge) = await SeedVoyageWithMeasurableWorkAsync(
                        testDb, MissionModeEnum.Implementation, MissionStatusEnum.Complete).ConfigureAwait(false);
                    await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.Build).ConfigureAwait(false);

                    await svc.UpdateVoyageTerminalStatusAsync(voyage.Id, CancellationToken.None).ConfigureAwait(false);
                    Voyage? after = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.InProgress, after!.Status,
                        "an armed Check on measurable work is queued work and holds completion until it runs");
                }
            }).ConfigureAwait(false);

            await RunTest("ReportOnlyVoyage_ArmedChecksOnMeasurableWork_DoNotHoldJudgePass", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    (MissionService svc, Voyage voyage, Mission judge) = await SeedVoyageWithMeasurableWorkAsync(
                        testDb, MissionModeEnum.Research, MissionStatusEnum.InProgress).ConfigureAwait(false);
                    await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.Build).ConfigureAwait(false);
                    await AddArmedIntentMarkerAsync(testDb, voyage.Id, CheckRunTypeEnum.UnitTest).ConfigureAwait(false);

                    AssertEqual(
                        MissionService.JudgeCheckGate.GreenChecks,
                        await svc.EvaluateJudgeCheckGateAsync(judge, CancellationToken.None).ConfigureAwait(false),
                        "a fully report-only voyage needs no code Checks, armed or not");
                }
            }).ConfigureAwait(false);

            await RunTest("DescribeUnresolvedChecks_NamesTheBlockingRecords", () =>
            {
                CheckRun queued = new CheckRun
                {
                    Id = "chk_queued",
                    Type = CheckRunTypeEnum.UnitTest,
                    Label = "UnitTest",
                    Status = CheckRunStatusEnum.Pending,
                    Command = "dotnet test"
                };
                CheckRun running = new CheckRun
                {
                    Id = "chk_running",
                    Type = CheckRunTypeEnum.Build,
                    Label = null,
                    Status = CheckRunStatusEnum.Running,
                    Command = "dotnet build"
                };
                CheckRun marker = new CheckRun { Id = "chk_marker", Status = CheckRunStatusEnum.Pending };
                CheckRun green = new CheckRun { Id = "chk_green", Status = CheckRunStatusEnum.Passed, Command = "dotnet build" };

                string described = MissionService.DescribeUnresolvedChecks(
                    new List<CheckRun> { queued, running, marker, green });
                AssertContains("chk_queued", described, "the queued record is named");
                AssertContains("chk_running", described, "the running record is named");
                AssertContains("Build", described, "the type stands in for a missing label");
                AssertFalse(described.Contains("chk_marker", StringComparison.Ordinal),
                    "an intent marker is not something the operator can resolve, so it is not named");
                AssertFalse(described.Contains("chk_green", StringComparison.Ordinal),
                    "a resolved record does not block");

                AssertEqual(String.Empty, MissionService.DescribeUnresolvedChecks(null),
                    "null renders as empty so callers can append unconditionally");
                AssertEqual(String.Empty, MissionService.DescribeUnresolvedChecks(new List<CheckRun> { marker, green }),
                    "nothing genuinely unresolved renders as empty");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
