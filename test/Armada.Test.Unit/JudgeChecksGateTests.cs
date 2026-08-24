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
                AssertFalse(CheckRunGateRules.IsStale(failedAtA, "bbbbbbbb2222"),
                    "only a Passed record can be a stale green; a Failed one is a real failure whatever it measured");

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
